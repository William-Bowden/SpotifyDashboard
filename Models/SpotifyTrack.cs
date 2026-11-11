using System.Text.Json.Serialization;

namespace SpotifyDashboard.Models;

public sealed class SpotifyTrack
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("duration_ms")]
    public int DurationMs { get; set; }

    /// <summary>Local files can be listed but not started from the Web API by URI.</summary>
    [JsonPropertyName("is_local")]
    public bool IsLocal { get; set; }

    [JsonPropertyName("artists")]
    public List<SpotifyArtist> Artists { get; set; } = new();

    [JsonPropertyName("album")]
    public SpotifyAlbum? Album { get; set; }

    [JsonIgnore]
    public string ArtistNames => string.Join(", ", Artists.Select(a => a.Name));
}

public sealed class SpotifyArtist
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public sealed class SpotifyAlbum
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    /// <summary>Album art, largest first (per Spotify's docs).</summary>
    [JsonPropertyName("images")]
    public List<SpotifyImage> Images { get; set; } = new();

    [JsonPropertyName("artists")]
    public List<SpotifyArtist>? Artists { get; set; }

    [JsonPropertyName("total_tracks")]
    public int TotalTracks { get; set; }

    /// <summary>First page of the album's tracks; only present on full album objects (e.g. saved albums).</summary>
    [JsonPropertyName("tracks")]
    public SpotifyPage<SpotifyTrack>? Tracks { get; set; }

    [JsonIgnore]
    public string ArtistNames => Artists is null ? string.Empty : string.Join(", ", Artists.Select(a => a.Name));
}

public sealed class SpotifyImage
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("width")]
    public int? Width { get; set; }

    [JsonPropertyName("height")]
    public int? Height { get; set; }
}
