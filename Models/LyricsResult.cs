namespace SpotifyDashboard.Models;

/// <summary>One timestamped line of synchronized lyrics.</summary>
public sealed record LyricsLine(int TimeMs, string Text);

/// <summary>
/// Provider-independent lyrics for a track. A result may have synced lines,
/// plain text, both, or neither (instrumental).
/// </summary>
public sealed class LyricsResult
{
    /// <summary>Display name of where the lyrics came from, e.g. "LRCLIB".</summary>
    public required string Source { get; init; }

    public string? TrackName { get; init; }
    public string? ArtistName { get; init; }

    public bool IsInstrumental { get; init; }

    public string? PlainLyrics { get; init; }

    /// <summary>Synced lines sorted by time; empty when only plain lyrics exist.</summary>
    public IReadOnlyList<LyricsLine> SyncedLines { get; init; } = Array.Empty<LyricsLine>();

    public bool IsSynced => SyncedLines.Count > 0;

    public bool HasPlainLyrics => !string.IsNullOrWhiteSpace(PlainLyrics);

    /// <summary>
    /// Index of the line being sung at <paramref name="positionMs"/>,
    /// or -1 before the first line.
    /// </summary>
    public int GetLineIndexAt(int positionMs)
    {
        // Binary search for the last line whose timestamp is <= position.
        int lo = 0, hi = SyncedLines.Count - 1, result = -1;

        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;

            if (SyncedLines[mid].TimeMs <= positionMs)
            {
                result = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return result;
    }
}
