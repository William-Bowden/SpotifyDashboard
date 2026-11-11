using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SpotifyDashboard;
using SpotifyDashboard.Services;
using SpotifyDashboard.Services.Lyrics;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<SpotifyAuthService>();
builder.Services.AddScoped(sp => new SpotifyApiService(
    new HttpClient { BaseAddress = new Uri(SpotifyApiService.BaseAddress) },
    sp.GetRequiredService<SpotifyAuthService>()));
builder.Services.AddScoped<PlaybackController>();

// Lyrics: swap the provider here to use a different source.
builder.Services.AddScoped<ILyricsProvider>(_ => new LrcLibLyricsProvider(
    new HttpClient { BaseAddress = new Uri(LrcLibLyricsProvider.BaseAddress) }));
builder.Services.AddScoped<LyricsService>();

await builder.Build().RunAsync();
