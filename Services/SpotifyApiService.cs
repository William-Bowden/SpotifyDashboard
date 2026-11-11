using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SpotifyDashboard.Models;

namespace SpotifyDashboard.Services;

/// <summary>
/// Thin wrapper over the Spotify Web API. Tokens come from
/// <see cref="SpotifyAuthService"/>; this class never handles OAuth itself.
/// </summary>
public sealed class SpotifyApiService
{
    public const string BaseAddress = "https://api.spotify.com/v1/";

    // One retry for transient Spotify-side failures (502/503 are common on /me/player).
    // Only GETs are retried: re-sending a command such as "next" could skip twice.
    private const int MaxAttempts = 2;
    private static readonly TimeSpan TransientRetryDelay = TimeSpan.FromMilliseconds(750);

    private readonly HttpClient _http;
    private readonly SpotifyAuthService _auth;

    public SpotifyApiService(HttpClient http, SpotifyAuthService auth)
    {
        _http = http;
        _auth = auth;
    }

    /// <summary>
    /// Returns the user's available Spotify Connect devices, active device first.
    /// Returns an empty list when Spotify reports no devices.
    /// </summary>
    /// <remarks>
    /// Spotify's device list can lag behind reality: devices that were just opened
    /// may not appear yet, and recently closed ones may linger. Callers should offer
    /// a manual refresh rather than treating the list as authoritative.
    /// </remarks>
    /// <exception cref="SpotifyApiException">Authentication or API failure.</exception>
    public async Task<List<SpotifyDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "me/player/devices", cancellationToken);

        // Spotify returns 200 with a body here, but be tolerant of 204 / empty bodies.
        if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
            return new List<SpotifyDevice>();

        SpotifyDevicesResponse? payload;
        try
        {
            payload = await response.Content.ReadFromJsonAsync<SpotifyDevicesResponse>(cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new SpotifyApiException(
                SpotifyApiErrorKind.Other,
                "Spotify returned an unexpected device list format.",
                response.StatusCode,
                innerException: ex);
        }

        return (payload?.Devices ?? new List<SpotifyDevice>())
            .Where(d => !string.IsNullOrWhiteSpace(d.Name))
            .OrderByDescending(d => d.IsActive)
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Returns the current playback state, or null when nothing is playing on any device
    /// (Spotify answers 204 No Content in that case).
    /// </summary>
    public async Task<SpotifyPlaybackState?> GetPlaybackStateAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "me/player", cancellationToken);

        if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
            return null;

        try
        {
            return await response.Content.ReadFromJsonAsync<SpotifyPlaybackState>(cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new SpotifyApiException(
                SpotifyApiErrorKind.Other,
                "Spotify returned an unexpected playback state format.",
                response.StatusCode,
                innerException: ex);
        }
    }

    // ---------- Library (read-only) ----------
    // Each takes the "next" URL from the previous page, or null for the first page.

    public Task<SpotifyPage<SpotifyPlaylist>> GetMyPlaylistsAsync(string? next, CancellationToken cancellationToken = default) =>
        GetPageAsync<SpotifyPlaylist>(next ?? "me/playlists?limit=50", cancellationToken);

    /// <summary>
    /// Items of a playlist. Spotify only returns these for playlists the user owns or
    /// collaborates on; others answer 403 (Forbidden).
    /// </summary>
    public Task<SpotifyPage<SpotifyPlaylistItem>> GetPlaylistItemsAsync(
        string playlistId, string? next, CancellationToken cancellationToken = default) =>
        GetPageAsync<SpotifyPlaylistItem>(
            next ?? $"playlists/{Uri.EscapeDataString(playlistId)}/items?limit=100&additional_types=track,episode",
            cancellationToken);

    public Task<SpotifyPage<SpotifySavedTrack>> GetSavedTracksAsync(string? next, CancellationToken cancellationToken = default) =>
        GetPageAsync<SpotifySavedTrack>(next ?? "me/tracks?limit=50", cancellationToken);

    public Task<SpotifyPage<SpotifySavedAlbum>> GetSavedAlbumsAsync(string? next, CancellationToken cancellationToken = default) =>
        GetPageAsync<SpotifySavedAlbum>(next ?? "me/albums?limit=20", cancellationToken);

    private async Task<SpotifyPage<T>> GetPageAsync<T>(string uri, CancellationToken cancellationToken)
    {
        // "next" links are absolute; never send the token anywhere but the Web API.
        if (System.Uri.TryCreate(uri, UriKind.Absolute, out var absolute)
            && !absolute.AbsoluteUri.StartsWith(BaseAddress, StringComparison.OrdinalIgnoreCase))
        {
            throw new SpotifyApiException(SpotifyApiErrorKind.Other, "Spotify returned an unexpected paging link.");
        }

        using var response = await SendAsync(HttpMethod.Get, uri, cancellationToken);

        try
        {
            return await response.Content.ReadFromJsonAsync<SpotifyPage<T>>(cancellationToken) ?? new SpotifyPage<T>();
        }
        catch (JsonException ex)
        {
            throw new SpotifyApiException(
                SpotifyApiErrorKind.Other,
                "Spotify returned an unexpected library format.",
                response.StatusCode,
                innerException: ex);
        }
    }

    // ---------- Playback commands ----------
    // Every command takes the target device ID explicitly and sends it as device_id,
    // so a command can never fall through to whatever Spotify considers "active".

    /// <summary>
    /// Moves playback to <paramref name="deviceId"/>. When <paramref name="play"/> is false,
    /// the current paused/playing state is kept as-is by Spotify.
    /// </summary>
    public async Task TransferPlaybackAsync(string deviceId, bool play, CancellationToken cancellationToken = default)
    {
        RequireDeviceId(deviceId);

        var body = JsonContent.Create(new TransferPlaybackRequest(new[] { deviceId }, play));
        using var _ = await SendAsync(HttpMethod.Put, "me/player", cancellationToken, body);
    }

    /// <summary>
    /// Starts something new on the device: a context (playlist/album URI, optionally at a
    /// position within it) or an explicit list of track URIs.
    /// </summary>
    public async Task StartPlaybackAsync(
        string deviceId,
        string? contextUri,
        IReadOnlyList<string>? uris,
        int? offsetPosition,
        CancellationToken cancellationToken = default)
    {
        RequireDeviceId(deviceId);

        if (string.IsNullOrEmpty(contextUri) && (uris is null || uris.Count == 0))
            throw new ArgumentException("Nothing to play.", nameof(uris));

        var body = JsonContent.Create(new StartPlaybackRequest(
            string.IsNullOrEmpty(contextUri) ? null : contextUri,
            uris is { Count: > 0 } ? uris.ToArray() : null,
            offsetPosition is int position ? new PlaybackOffset(Math.Max(position, 0)) : null));

        using var _ = await SendAsync(
            HttpMethod.Put,
            $"me/player/play?device_id={Uri.EscapeDataString(deviceId)}",
            cancellationToken,
            body);
    }

    /// <summary>Resumes the current playback context on the device.</summary>
    public Task PlayAsync(string deviceId, CancellationToken cancellationToken = default) =>
        SendCommandAsync(HttpMethod.Put, "me/player/play", deviceId, cancellationToken);

    public Task PauseAsync(string deviceId, CancellationToken cancellationToken = default) =>
        SendCommandAsync(HttpMethod.Put, "me/player/pause", deviceId, cancellationToken);

    public Task NextAsync(string deviceId, CancellationToken cancellationToken = default) =>
        SendCommandAsync(HttpMethod.Post, "me/player/next", deviceId, cancellationToken);

    public Task PreviousAsync(string deviceId, CancellationToken cancellationToken = default) =>
        SendCommandAsync(HttpMethod.Post, "me/player/previous", deviceId, cancellationToken);

    public Task SeekAsync(string deviceId, int positionMs, CancellationToken cancellationToken = default) =>
        SendCommandAsync(
            HttpMethod.Put,
            $"me/player/seek?position_ms={Math.Max(0, positionMs)}",
            deviceId,
            cancellationToken);

    public Task SetVolumeAsync(string deviceId, int volumePercent, CancellationToken cancellationToken = default) =>
        SendCommandAsync(
            HttpMethod.Put,
            $"me/player/volume?volume_percent={Math.Clamp(volumePercent, 0, 100)}",
            deviceId,
            cancellationToken);

    private async Task SendCommandAsync(
        HttpMethod method,
        string relativeUri,
        string deviceId,
        CancellationToken cancellationToken)
    {
        RequireDeviceId(deviceId);

        var separator = relativeUri.Contains('?') ? '&' : '?';
        var uri = $"{relativeUri}{separator}device_id={Uri.EscapeDataString(deviceId)}";

        using var _ = await SendAsync(method, uri, cancellationToken);
    }

    private static void RequireDeviceId(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("A target device ID is required for playback commands.", nameof(deviceId));
    }

    private sealed record StartPlaybackRequest(
        [property: JsonPropertyName("context_uri"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContextUri,
        [property: JsonPropertyName("uris"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? Uris,
        [property: JsonPropertyName("offset"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PlaybackOffset? Offset);

    private sealed record PlaybackOffset([property: JsonPropertyName("position")] int Position);

    private sealed record TransferPlaybackRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("device_ids")] string[] DeviceIds,
        [property: System.Text.Json.Serialization.JsonPropertyName("play")] bool Play);

    /// <summary>
    /// Sends an authenticated request and returns a successful response,
    /// or throws <see cref="SpotifyApiException"/>. The caller owns (and must dispose) the response.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string relativeUri,
        CancellationToken cancellationToken,
        HttpContent? content = null)
    {
        var token = await _auth.GetAccessTokenAsync();

        if (string.IsNullOrEmpty(token))
        {
            throw new SpotifyApiException(
                SpotifyApiErrorKind.NotAuthenticated,
                "Not connected to Spotify. Please connect your account.");
        }

        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, relativeUri) { Content = content };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                if (attempt < MaxAttempts && method == HttpMethod.Get)
                {
                    await Task.Delay(TransientRetryDelay, cancellationToken);
                    continue;
                }

                throw new SpotifyApiException(
                    SpotifyApiErrorKind.Unavailable,
                    "Couldn't reach Spotify. Check the network connection and try again.",
                    innerException: ex);
            }

            if (response.IsSuccessStatusCode)
                return response;

            var isTransient = response.StatusCode is HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.GatewayTimeout;

            if (isTransient && attempt < MaxAttempts && method == HttpMethod.Get)
            {
                response.Dispose();
                await Task.Delay(TransientRetryDelay, cancellationToken);
                continue;
            }

            using (response)
            {
                throw await CreateExceptionAsync(response, cancellationToken);
            }
        }
    }

    private static async Task<SpotifyApiException> CreateExceptionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var spotifyMessage = await TryReadSpotifyErrorMessageAsync(response, cancellationToken);
        var status = response.StatusCode;

        return status switch
        {
            HttpStatusCode.Unauthorized => new SpotifyApiException(
                SpotifyApiErrorKind.Unauthorized,
                "Your Spotify session has expired or was revoked. Please reconnect.",
                status),

            HttpStatusCode.Forbidden => new SpotifyApiException(
                SpotifyApiErrorKind.Forbidden,
                spotifyMessage ?? "Spotify refused this request (missing permission or Premium required).",
                status),

            HttpStatusCode.NotFound => new SpotifyApiException(
                SpotifyApiErrorKind.DeviceUnavailable,
                "That device isn't available right now. Refresh the device list and try again.",
                status),

            HttpStatusCode.TooManyRequests => new SpotifyApiException(
                SpotifyApiErrorKind.RateLimited,
                "Spotify is rate-limiting requests. Wait a moment and try again.",
                status,
                response.Headers.RetryAfter?.Delta),

            >= HttpStatusCode.InternalServerError => new SpotifyApiException(
                SpotifyApiErrorKind.Unavailable,
                "Spotify is temporarily unavailable. Try again shortly.",
                status),

            _ => new SpotifyApiException(
                SpotifyApiErrorKind.Other,
                spotifyMessage ?? $"Spotify request failed ({(int)status}).",
                status)
        };
    }

    /// <summary>
    /// Spotify errors look like { "error": { "status": 401, "message": "..." } }.
    /// </summary>
    private static async Task<string?> TryReadSpotifyErrorMessageAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
                return null;

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString();
            }
        }
        catch (JsonException)
        {
            // Non-JSON error body; fall back to a generic message.
        }

        return null;
    }
}
