using System.Text.Json.Serialization;

namespace SpotifyDashboard.Models;

/// <summary>
/// Envelope for GET /v1/me/player/devices: { "devices": [ ... ] }.
/// </summary>
public sealed class SpotifyDevicesResponse
{
    [JsonPropertyName("devices")]
    public List<SpotifyDevice>? Devices { get; set; }
}
