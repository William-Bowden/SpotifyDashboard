using System.Text.Json.Serialization;

namespace SpotifyDashboard.Models;

/// <summary>
/// A Spotify Connect device, as returned by GET /v1/me/player/devices.
/// </summary>
public sealed class SpotifyDevice
{
    /// <summary>
    /// Device ID. Spotify documents this as nullable: some devices are listed
    /// without an ID and cannot be targeted by the API.
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Device type, e.g. "Computer", "Smartphone", "Speaker", "TV".
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("is_active")]
    public bool IsActive { get; set; }

    [JsonPropertyName("is_private_session")]
    public bool IsPrivateSession { get; set; }

    /// <summary>
    /// When true, Web API commands sent to this device are ignored.
    /// </summary>
    [JsonPropertyName("is_restricted")]
    public bool IsRestricted { get; set; }

    /// <summary>
    /// Current volume (0–100), or null when unavailable.
    /// </summary>
    [JsonPropertyName("volume_percent")]
    public int? VolumePercent { get; set; }

    [JsonPropertyName("supports_volume")]
    public bool SupportsVolume { get; set; }

    /// <summary>
    /// True when this dashboard can send commands to the device.
    /// </summary>
    [JsonIgnore]
    public bool IsControllable => !string.IsNullOrEmpty(Id) && !IsRestricted;
}
