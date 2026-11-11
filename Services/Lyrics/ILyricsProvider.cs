using SpotifyDashboard.Models;

namespace SpotifyDashboard.Services.Lyrics;

/// <summary>
/// A source of lyrics. Spotify's public Web API has no lyrics endpoint, so lyrics
/// come from independent providers behind this abstraction.
/// </summary>
public interface ILyricsProvider
{
    /// <summary>
    /// Finds lyrics for <paramref name="track"/>.
    /// Returns null when the provider has no match.
    /// </summary>
    /// <exception cref="LyricsProviderException">
    /// The provider couldn't be queried (network failure, rate limit, server error).
    /// </exception>
    Task<LyricsResult?> GetLyricsAsync(SpotifyTrack track, CancellationToken cancellationToken = default);
}

/// <summary>A lyrics lookup failed (as opposed to "no lyrics found", which is a null result).</summary>
public sealed class LyricsProviderException : Exception
{
    public LyricsProviderException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
