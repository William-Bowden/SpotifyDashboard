using SpotifyDashboard.Models;

namespace SpotifyDashboard.Services;

/// <summary>
/// The user's playlists, saved albums and Liked Songs, loaded a page at a time and kept
/// in memory for the session so reopening the library is instant. Read-only.
/// </summary>
public sealed class LibraryService
{
    private readonly SpotifyApiService _api;
    private readonly Dictionary<string, LibraryList<SpotifyTrack?>> _playlistTracks = new();

    public LibraryService(SpotifyApiService api)
    {
        _api = api;

        Playlists = new LibraryList<SpotifyPlaylist>(NotifyChanged, async (next, ct) =>
        {
            var page = await _api.GetMyPlaylistsAsync(next, ct);
            return (page.Items.Where(p => p is not null).ToList(), page.Next, page.Total);
        });

        Albums = new LibraryList<SpotifyAlbum>(NotifyChanged, async (next, ct) =>
        {
            var page = await _api.GetSavedAlbumsAsync(next, ct);
            return (page.Items.Select(i => i.Album).OfType<SpotifyAlbum>().ToList(), page.Next, page.Total);
        });

        LikedSongs = new LibraryList<SpotifyTrack>(NotifyChanged, async (next, ct) =>
        {
            var page = await _api.GetSavedTracksAsync(next, ct);
            return (page.Items.Select(i => i.Track).OfType<SpotifyTrack>().ToList(), page.Next, page.Total);
        });
    }

    /// <summary>Raised whenever any list starts or finishes loading.</summary>
    public event Action? Changed;

    public LibraryList<SpotifyPlaylist> Playlists { get; }
    public LibraryList<SpotifyAlbum> Albums { get; }
    public LibraryList<SpotifyTrack> LikedSongs { get; }

    /// <summary>
    /// Tracks of one playlist. Entries stay in playlist order, with null for items that are
    /// no longer available, so a row's index is its position for "play from here".
    /// </summary>
    public LibraryList<SpotifyTrack?> PlaylistTracks(string playlistId)
    {
        if (!_playlistTracks.TryGetValue(playlistId, out var list))
        {
            list = new LibraryList<SpotifyTrack?>(NotifyChanged, async (next, ct) =>
            {
                var page = await _api.GetPlaylistItemsAsync(playlistId, next, ct);
                return (page.Items.Select(i => i?.Resolved).ToList(), page.Next, page.Total);
            });

            _playlistTracks[playlistId] = list;
        }

        return list;
    }

    /// <summary>Forgets everything (on disconnect).</summary>
    public void Reset()
    {
        Playlists.Clear();
        Albums.Clear();
        LikedSongs.Clear();
        _playlistTracks.Clear();
        NotifyChanged();
    }

    private void NotifyChanged() => Changed?.Invoke();
}

/// <summary>A lazily loaded, paged list from the Spotify Web API.</summary>
public sealed class LibraryList<T>
{
    public delegate Task<(List<T> Items, string? Next, int Total)> PageFetcher(string? next, CancellationToken ct);

    private readonly Action _notify;
    private readonly PageFetcher _fetch;
    private string? _next;
    private int _generation;

    public LibraryList(Action notify, PageFetcher fetch)
    {
        _notify = notify;
        _fetch = fetch;
    }

    public List<T> Items { get; } = new();
    public int Total { get; private set; }
    public bool IsLoaded { get; private set; }
    public bool IsLoading { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Spotify refused (403): e.g. the track list of a playlist the user doesn't own.</summary>
    public bool IsForbidden { get; private set; }

    public bool HasMore => _next is not null;

    public Task EnsureLoadedAsync() =>
        IsLoaded || IsLoading ? Task.CompletedTask : LoadAsync(null);

    public Task LoadMoreAsync() =>
        !HasMore || IsLoading ? Task.CompletedTask : LoadAsync(_next);

    public Task ReloadAsync()
    {
        Clear();
        return LoadAsync(null);
    }

    public void Clear()
    {
        _generation++; // results of any in-flight load are dropped
        Items.Clear();
        _next = null;
        Total = 0;
        IsLoaded = false;
        IsLoading = false;
        Error = null;
        IsForbidden = false;
    }

    private async Task LoadAsync(string? next)
    {
        var generation = _generation;

        IsLoading = true;
        Error = null;
        _notify();

        try
        {
            var page = await _fetch(next, CancellationToken.None);

            if (generation != _generation)
                return;

            if (next is null)
                Items.Clear();

            Items.AddRange(page.Items);
            _next = page.Next;
            Total = page.Total;
            IsLoaded = true;
            IsForbidden = false;
        }
        catch (SpotifyApiException ex)
        {
            if (generation != _generation)
                return;

            Error = ex.Message;
            IsForbidden = ex.Kind == SpotifyApiErrorKind.Forbidden;
        }
        finally
        {
            if (generation == _generation)
            {
                IsLoading = false;
                _notify();
            }
        }
    }
}
