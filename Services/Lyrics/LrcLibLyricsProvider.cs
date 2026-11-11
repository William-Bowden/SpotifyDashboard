using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SpotifyDashboard.Models;

namespace SpotifyDashboard.Services.Lyrics;

/// <summary>
/// Lyrics from LRCLIB (https://lrclib.net), a free, open lyrics database with
/// plain and LRC-synced lyrics. Called directly from the browser (CORS is open).
/// </summary>
/// <remarks>
/// Lookup order, stopping at the first hit (at most 3 requests per track):
/// <list type="number">
/// <item>/api/get with the exact Spotify title, first artist, album, and duration.</item>
/// <item>/api/get with a cleaned title ("Song - Remastered 2011" → "Song") and no album,
/// since album names often differ between single, album, and deluxe releases.</item>
/// <item>/api/search, accepting a result only if artist, title, and duration agree.</item>
/// </list>
/// </remarks>
public sealed partial class LrcLibLyricsProvider : ILyricsProvider
{
    public const string BaseAddress = "https://lrclib.net/api/";
    public const string SourceName = "LRCLIB";

    // LRCLIB asks clients to identify themselves; browsers can't set User-Agent, so the
    // docs recommend this header instead.
    private const string ClientHeaderName = "Lrclib-Client";
    private const string ClientHeaderValue = "SpotifyDashboard/1.0 (personal Spotify Connect remote)";

    // /api/get itself matches within ±2 s; search results get a little more slack.
    private const int SearchDurationToleranceSeconds = 3;
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private bool _sendClientHeader = true;
    private DateTime _blockedUntilUtc;

    public LrcLibLyricsProvider(HttpClient http)
    {
        _http = http;
    }

    public async Task<LyricsResult?> GetLyricsAsync(SpotifyTrack track, CancellationToken cancellationToken = default)
    {
        var artist = track.Artists.FirstOrDefault()?.Name;

        if (string.IsNullOrWhiteSpace(track.Name) || string.IsNullOrWhiteSpace(artist))
            return null;

        var durationSeconds = track.DurationMs > 0
            ? (int?)Math.Clamp((int)Math.Round(track.DurationMs / 1000.0), 1, 3600)
            : null;

        // 1. Exact match.
        var record = await GetAsync(track.Name, artist, track.Album?.Name, durationSeconds, cancellationToken);

        // 2. Cleaned title, no album.
        var cleanTitle = CleanTitle(track.Name);
        record ??= await GetAsync(cleanTitle, artist, albumName: null, durationSeconds, cancellationToken);

        // 3. Search, with strict verification.
        if (record is null)
        {
            var candidates = await SearchAsync(cleanTitle, artist, cancellationToken);
            record = PickBestSearchResult(candidates, cleanTitle, artist, durationSeconds);
        }

        return record is null ? null : ToResult(record);
    }

    // ---------- Requests ----------

    private async Task<LrcLibRecord?> GetAsync(
        string trackName, string artistName, string? albumName, int? durationSeconds, CancellationToken ct)
    {
        var query = new StringBuilder("get?")
            .Append("track_name=").Append(Uri.EscapeDataString(trackName))
            .Append("&artist_name=").Append(Uri.EscapeDataString(artistName));

        if (!string.IsNullOrWhiteSpace(albumName))
            query.Append("&album_name=").Append(Uri.EscapeDataString(albumName));

        if (durationSeconds is int duration)
            query.Append("&duration=").Append(duration.ToString(CultureInfo.InvariantCulture));

        using var response = await SendAsync(query.ToString(), ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        return await ReadJsonAsync<LrcLibRecord>(response, ct);
    }

    private async Task<List<LrcLibRecord>> SearchAsync(string trackName, string artistName, CancellationToken ct)
    {
        var uri = $"search?track_name={Uri.EscapeDataString(trackName)}&artist_name={Uri.EscapeDataString(artistName)}";

        using var response = await SendAsync(uri, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return new List<LrcLibRecord>();

        return await ReadJsonAsync<List<LrcLibRecord>>(response, ct) ?? new List<LrcLibRecord>();
    }

    private async Task<HttpResponseMessage> SendAsync(string relativeUri, CancellationToken ct)
    {
        if (DateTime.UtcNow < _blockedUntilUtc)
            throw new LyricsProviderException("The lyrics service is busy. Lyrics will be retried shortly.");

        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUri);
        if (_sendClientHeader)
            request.Headers.TryAddWithoutValidation(ClientHeaderName, ClientHeaderValue);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (HttpRequestException) when (_sendClientHeader)
        {
            // In the browser, a CORS preflight rejection of the custom header surfaces as a
            // generic network error. Drop the header for the rest of the session and retry once.
            _sendClientHeader = false;
            return await SendAsync(relativeUri, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new LyricsProviderException("Couldn't reach the lyrics service.", ex);
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            // LRCLIB requires honouring Retry-After, plus up to ~30% random jitter.
            var retryAfter = response.Headers.RetryAfter?.Delta ?? DefaultRetryAfter;
            var jitter = 1 + Random.Shared.NextDouble() * 0.3;
            _blockedUntilUtc = DateTime.UtcNow + TimeSpan.FromTicks((long)(retryAfter.Ticks * jitter));

            response.Dispose();
            throw new LyricsProviderException("The lyrics service is busy. Lyrics will be retried shortly.");
        }

        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new LyricsProviderException($"The lyrics service returned an error ({status}).");
        }

        return response;
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(ct);
        }
        catch (JsonException ex)
        {
            throw new LyricsProviderException("The lyrics service returned an unexpected response.", ex);
        }
    }

    // ---------- Matching ----------

    private static LrcLibRecord? PickBestSearchResult(
        IEnumerable<LrcLibRecord> candidates, string title, string artist, int? durationSeconds)
    {
        var wantedTitle = Normalize(title);
        var wantedArtist = Normalize(artist);

        return candidates
            .Where(c => durationSeconds is null
                || Math.Abs(c.Duration - durationSeconds.Value) <= SearchDurationToleranceSeconds)
            .Where(c => LooselyEqual(Normalize(c.ArtistName), wantedArtist))
            .Where(c => LooselyEqual(Normalize(CleanTitle(c.TrackName ?? string.Empty)), wantedTitle))
            .OrderByDescending(c => !string.IsNullOrWhiteSpace(c.SyncedLyrics))
            .ThenByDescending(c => c.Instrumental || !string.IsNullOrWhiteSpace(c.PlainLyrics))
            .ThenBy(c => durationSeconds is null ? 0 : Math.Abs(c.Duration - durationSeconds.Value))
            .FirstOrDefault();
    }

    /// <summary>Equal, or one contains the other (handles "Artist" vs "Artist & Friend").</summary>
    private static bool LooselyEqual(string a, string b) =>
        a.Length > 0 && b.Length > 0 && (a == b || a.Contains(b) || b.Contains(a));

    [GeneratedRegex(
        @"\s+-\s+.*\b(remaster(ed)?|live|version|edit|mix|mono|stereo|acoustic|demo|radio|single|bonus|deluxe|from|recorded)\b.*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DashSuffix();

    [GeneratedRegex(@"\s*[\(\[](feat\.?|ft\.?|with)\s[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex FeaturingSuffix();

    /// <summary>
    /// Removes release decorations Spotify adds to titles:
    /// "Song - Remastered 2011" → "Song", "Song (feat. X)" → "Song".
    /// </summary>
    internal static string CleanTitle(string title)
    {
        var cleaned = FeaturingSuffix().Replace(title, string.Empty);
        cleaned = DashSuffix().Replace(cleaned, string.Empty);
        cleaned = cleaned.Trim();
        return cleaned.Length > 0 ? cleaned : title.Trim();
    }

    /// <summary>Lowercase, strip accents and punctuation, collapse whitespace.</summary>
    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var lastWasSpace = true;

        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }

    // ---------- Mapping ----------

    private static LyricsResult ToResult(LrcLibRecord record) => new()
    {
        Source = SourceName,
        TrackName = record.TrackName,
        ArtistName = record.ArtistName,
        IsInstrumental = record.Instrumental,
        PlainLyrics = record.PlainLyrics,
        SyncedLines = LrcParser.Parse(record.SyncedLyrics)
    };

    private sealed class LrcLibRecord
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("trackName")]
        public string? TrackName { get; set; }

        [JsonPropertyName("artistName")]
        public string? ArtistName { get; set; }

        [JsonPropertyName("albumName")]
        public string? AlbumName { get; set; }

        /// <summary>Seconds (may be fractional).</summary>
        [JsonPropertyName("duration")]
        public double Duration { get; set; }

        [JsonPropertyName("instrumental")]
        public bool Instrumental { get; set; }

        [JsonPropertyName("plainLyrics")]
        public string? PlainLyrics { get; set; }

        [JsonPropertyName("syncedLyrics")]
        public string? SyncedLyrics { get; set; }
    }
}
