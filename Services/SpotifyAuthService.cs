
using Microsoft.JSInterop;

namespace SpotifyDashboard.Services;

public sealed class SpotifyAuthService : IAsyncDisposable
{
    private readonly IConfiguration _configuration;
    private readonly Lazy<Task<IJSObjectReference>> _module;

    private static readonly string[] Scopes =
    {
        "user-read-playback-state",
        "user-modify-playback-state",
        "user-read-currently-playing"
    };

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
