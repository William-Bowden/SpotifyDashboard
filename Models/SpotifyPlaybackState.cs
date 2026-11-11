using System.Text.Json.Serialization;

namespace SpotifyDashboard.Models;

/// <summary>
/// Response of GET /v1/me/player. Spotify returns 204 (no body) when nothing
/// is playing anywhere; the API service maps that to null.
/// </summary>
public sealed class SpotifyPlaybackState
{
    [JsonPropertyName("device")]
    public SpotifyDevice? Device { get; set; }

    [JsonPropertyName("is_playing")]
    public bool IsPlaying { get; set; }

    [JsonPropertyName("progress_ms")]
    public int? ProgressMs { get; set; }

    /// <summary>"off", "track" or "context".</summary>
    [JsonPropertyName("repeat_state")]
    public string? RepeatState { get; set; }

    [JsonPropertyName("shuffle_state")]
    public bool ShuffleState { get; set; }

    /// <summary>"track", "episode", "ad" or "unknown".</summary>
    [JsonPropertyName("currently_playing_type")]
    public string? CurrentlyPlayingType { get; set; }

    /// <summary>Unix ms timestamp of when Spotify sampled this state.</summary>
    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    /// <summary>
    /// The current track. Null for ads, unknown content, or podcast episodes
    /// (episodes aren't requested yet).
    /// </summary>
    [JsonPropertyName("item")]
    public SpotifyTrack? Item { get; set; }

    /// <summary>What's playing from (playlist, album, ...); null for e.g. a plain track list.</summary>
    [JsonPropertyName("context")]
    public SpotifyContext? Context { get; set; }

    [JsonPropertyName("actions")]
    public SpotifyPlaybackActions? Actions { get; set; }
}

/// <summary>
/// Which actions Spotify currently disallows (e.g. skipping during an ad).
/// </summary>
public sealed class SpotifyPlaybackActions
{
    [JsonPropertyName("disallows")]
    public Dictionary<string, bool>? Disallows { get; set; }

    public bool IsDisallowed(string action) =>
        Disallows is not null && Disallows.TryGetValue(action, out var disallowed) && disallowed;
}
