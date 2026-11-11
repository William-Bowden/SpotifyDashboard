using SpotifyDashboard.Models;

namespace SpotifyDashboard.Services.Lyrics;

/// <summary>
/// Caches lyrics lookups per track for the session, so revisiting a track
/// (or re-rendering) never re-queries the provider. "Not found" results are cached;
/// failures are not, so they're retried next time.
/// </summary>
public sealed class LyricsService
{
    private const int MaxCachedTracks = 100;

    private readonly ILyricsProvider _provider;
    private readonly Dictionary<string, LyricsResult?> _cache = new();
    private readonly Queue<string> _insertionOrder = new();

    public LyricsService(ILyricsProvider provider)
    {
        _provider = provider;
    }

    public static string GetTrackKey(SpotifyTrack track) =>
        track.Id ?? $"{track.Name}|{track.ArtistNames}|{track.DurationMs}";

    public bool TryGetCached(SpotifyTrack track, out LyricsResult? result) =>
        _cache.TryGetValue(GetTrackKey(track), out result);

    /// <exception cref="LyricsProviderException">Lookup failed.</exception>
    public async Task<LyricsResult?> GetLyricsAsync(SpotifyTrack track, CancellationToken cancellationToken = default)
    {
        var key = GetTrackKey(track);

        if (_cache.TryGetValue(key, out var cached))
            return cached;

        var result = await _provider.GetLyricsAsync(track, cancellationToken);

        if (!_cache.ContainsKey(key))
        {
            _cache[key] = result;
            _insertionOrder.Enqueue(key);

            while (_insertionOrder.Count > MaxCachedTracks)
                _cache.Remove(_insertionOrder.Dequeue());
        }

        return result;
    }
}
