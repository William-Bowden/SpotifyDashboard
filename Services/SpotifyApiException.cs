using System.Net;

namespace SpotifyDashboard.Services;

public enum SpotifyApiErrorKind
{
    /// <summary>No usable access token; the user needs to (re)connect.</summary>
    NotAuthenticated,

    /// <summary>Spotify rejected the token (401) — expired, revoked, or invalid.</summary>
    Unauthorized,

    /// <summary>Token is valid but not allowed to do this (403), e.g. missing scope or non-Premium.</summary>
    Forbidden,

    /// <summary>Too many requests (429).</summary>
    RateLimited,

    /// <summary>
    /// 404 from a player endpoint — usually the target device went offline or there is
    /// no active playback session to control.
    /// </summary>
    DeviceUnavailable,

    /// <summary>Spotify-side failure (5xx) or the network request itself failed.</summary>
    Unavailable,

    /// <summary>Any other unexpected response.</summary>
    Other
}

/// <summary>
/// Error raised by <see cref="SpotifyApiService"/> with enough detail for the UI
/// to decide what to show (e.g. a reconnect prompt vs. a retry hint).
/// </summary>
public sealed class SpotifyApiException : Exception
{
    public SpotifyApiErrorKind Kind { get; }
    public HttpStatusCode? StatusCode { get; }
    public TimeSpan? RetryAfter { get; }

    public bool RequiresReauthentication =>
        Kind is SpotifyApiErrorKind.NotAuthenticated or SpotifyApiErrorKind.Unauthorized;

    public SpotifyApiException(
        SpotifyApiErrorKind kind,
        string message,
        HttpStatusCode? statusCode = null,
        TimeSpan? retryAfter = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }
}
