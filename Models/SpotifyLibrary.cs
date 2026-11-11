using System.Text.Json.Serialization;

namespace SpotifyDashboard.Models;

/// <summary>Spotify's paging envelope: { items, next, total, ... }.</summary>
public sealed class SpotifyPage<T>
{
    [JsonPropertyName("items")]
    public List<T> Items { get; set; } = new();

    /// <summary>Absolute URL of the next page, or null on the last page.</summary>
    [JsonPropertyName("next")]
    public string? Next { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }
}

/// <summary>A { href, total } reference, e.g. a playlist's track count.</summary>
public sealed class SpotifyCountRef
{
    [JsonPropertyName("total")]
    public int Total { get; set; }
}

public sealed class SpotifyUser
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }
}

/// <summary>Simplified playlist, as returned by GET /v1/me/playlists.</summary>
public sealed class SpotifyPlaylist
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("uri")]
    public string Uri { get; set; } = string.Empty;

    [JsonPropertyName("collaborative")]
    public bool Collaborative { get; set; }

    /// <summary>Can be null or empty (e.g. a brand-new empty playlist).</summary>
    [JsonPropertyName("images")]
    public List<SpotifyImage>? Images { get; set; }

    [JsonPropertyName("owner")]
    public SpotifyUser? Owner { get; set; }

    // Spotify renamed the playlist's "tracks" reference to "items" in February 2026;
    // accept either so older and newer responses both work.
    [JsonPropertyName("items")]
    public SpotifyCountRef? ItemsRef { get; set; }

    [JsonPropertyName("tracks")]
    public SpotifyCountRef? TracksRef { get; set; }

    [JsonIgnore]
    public int? TrackCount => ItemsRef?.Total ?? TracksRef?.Total;
}

/// <summary>One entry of GET /v1/playlists/{id}/items.</summary>
public sealed class SpotifyPlaylistItem
{
    /// <summary>Current field name (February 2026 onwards).</summary>
    [JsonPropertyName("item")]
    public SpotifyTrack? Item { get; set; }

    /// <summary>Pre-2026 field name.</summary>
    [JsonPropertyName("track")]
    public SpotifyTrack? Track { get; set; }

    /// <summary>The track (or episode); null when it's no longer available.</summary>
    [JsonIgnore]
    public SpotifyTrack? Resolved => Item ?? Track;
}

/// <summary>One entry of GET /v1/me/tracks (Liked Songs).</summary>
public sealed class SpotifySavedTrack
{
    [JsonPropertyName("track")]
    public SpotifyTrack? Track { get; set; }
}

/// <summary>One entry of GET /v1/me/albums.</summary>
public sealed class SpotifySavedAlbum
{
    [JsonPropertyName("album")]
    public SpotifyAlbum? Album { get; set; }
}

/// <summary>The playback context (playlist, album, ...) in GET /v1/me/player.</summary>
public sealed class SpotifyContext
{
    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

/// <summary>Picks an appropriately sized image from Spotify's image lists.</summary>
public static class Artwork
{
    /// <summary>Smallest image at least <paramref name="minWidth"/> wide (sharp at ~50px on 2× screens).</summary>
    public static string? Thumbnail(IEnumerable<SpotifyImage>? images, int minWidth = 100)
    {
        var list = images?.Where(i => !string.IsNullOrEmpty(i.Url)).ToList();
        if (list is null || list.Count == 0)
            return null;

        var sized = list
            .Where(i => i.Width is int w && w >= minWidth)
            .OrderBy(i => i.Width)
            .FirstOrDefault();

        // Playlist mosaics have no width; Spotify lists the largest first.
        return (sized ?? list.OrderByDescending(i => i.Width ?? int.MaxValue).First()).Url;
    }

    public static string? Largest(IEnumerable<SpotifyImage>? images) =>
        images?
            .Where(i => !string.IsNullOrEmpty(i.Url))
            .OrderByDescending(i => i.Width ?? int.MaxValue)
            .FirstOrDefault()
            ?.Url;
}
