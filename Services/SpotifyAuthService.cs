
using Microsoft.JSInterop;

namespace SpotifyDashboard.Services;

public sealed class SpotifyAuthService : IAsyncDisposable
{
    private readonly IConfiguration _configuration;
    private readonly Lazy<Task<IJSObjectReference>> _module;

    /// <summary>Read-only access to the user's playlists and saved music (Library view).</summary>
    public static readonly string[] LibraryScopes =
    {
        "playlist-read-private",
        "playlist-read-collaborative",
        "user-library-read"
    };

    private static readonly string[] Scopes =
    [
        "user-read-playback-state",
        "user-modify-playback-state",
        "user-read-currently-playing",
        .. LibraryScopes
    ];

    public SpotifyAuthService(
        IJSRuntime jsRuntime,
        IConfiguration configuration)
    {
        _configuration = configuration;

        _module = new Lazy<Task<IJSObjectReference>>(
            () => jsRuntime.InvokeAsync<IJSObjectReference>(
                "import",
                "./js/spotifyAuth.js"
            ).AsTask()
        );
    }

    private string GetRequiredSetting(string key)
    {
        var value = _configuration[key];

        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(
                $"Missing configuration setting: {key}");

        return value;
    }

    public async Task LoginAsync()
    {
        var module = await _module.Value;

        await module.InvokeVoidAsync(
            "beginLogin",
            GetRequiredSetting("Spotify:ClientId"),
            GetRequiredSetting("Spotify:RedirectUri"),
            string.Join(" ", Scopes)
        );
    }

    public async Task CompleteLoginAsync()
    {
        var module = await _module.Value;

        await module.InvokeVoidAsync(
            "completeLogin",
            GetRequiredSetting("Spotify:ClientId"),
            GetRequiredSetting("Spotify:RedirectUri")
        );
    }

    public async Task<string?> GetAccessTokenAsync()
{
    var module = await _module.Value;

    return await module.InvokeAsync<string?>(
        "getAccessToken",
        GetRequiredSetting("Spotify:ClientId")
    );
}

    /// <summary>
    /// True when the stored token was granted all of <paramref name="required"/>.
    /// Tokens saved before scopes were recorded count as missing them.
    /// </summary>
    public async Task<bool> HasScopesAsync(IEnumerable<string> required)
    {
        var module = await _module.Value;
        var granted = await module.InvokeAsync<string?>("getGrantedScopes");

        if (string.IsNullOrWhiteSpace(granted))
            return false;

        var set = granted.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return required.All(set.Contains);
    }

    public async Task LogoutAsync()
    {
        var module = await _module.Value;

        await module.InvokeVoidAsync("logout");
    }

    public async ValueTask DisposeAsync()
    {
        if (_module.IsValueCreated)
        {
            var module = await _module.Value;
            await module.DisposeAsync();
        }
    }
}
