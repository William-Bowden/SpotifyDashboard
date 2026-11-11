using System.Globalization;
using System.Text.RegularExpressions;
using SpotifyDashboard.Models;

namespace SpotifyDashboard.Services.Lyrics;

/// <summary>
/// Parses LRC-formatted synced lyrics, e.g. "[00:17.12] Some line".
/// </summary>
/// <remarks>
/// Handles: several timestamps on one line ("[00:12.00][01:30.00]Chorus"),
/// 1–3 digit fractions ([mm:ss], [mm:ss.x], [mm:ss.xx], [mm:ss.xxx], and ':' as separator),
/// the [offset:±ms] tag, metadata tags ([ar:], [ti:] …, ignored), word-level
/// "&lt;mm:ss.xx&gt;" timings (stripped), and empty lines (kept as instrumental gaps).
/// </remarks>
public static partial class LrcParser
{
    [GeneratedRegex(@"^\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex LeadingTimestamp();

    [GeneratedRegex(@"^\[offset:\s*([+-]?\d+)\s*\]$", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetTag();

    [GeneratedRegex(@"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>")]
    private static partial Regex WordTimestamp();

    public static IReadOnlyList<LyricsLine> Parse(string? lrc)
    {
        if (string.IsNullOrWhiteSpace(lrc))
            return Array.Empty<LyricsLine>();

        var rawLines = lrc.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        // [offset:+500] means "show lyrics 500 ms earlier"; it may appear anywhere, so find it first.
        var offsetMs = 0;
        foreach (var raw in rawLines)
        {
            var match = OffsetTag().Match(raw.Trim());
            if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var offset))
            {
                offsetMs = offset;
                break;
            }
        }

        var lines = new List<LyricsLine>();

        foreach (var raw in rawLines)
        {
            var rest = raw.Trim();
            var times = new List<int>();

            Match match;
            while ((match = LeadingTimestamp().Match(rest)).Success)
            {
                times.Add(ToMilliseconds(match));
                rest = rest[match.Length..];
            }

            if (times.Count == 0)
                continue; // Metadata tag, comment, or junk.

            var text = WordTimestamp().Replace(rest, string.Empty).Trim();

            foreach (var time in times)
                lines.Add(new LyricsLine(Math.Max(0, time - offsetMs), text));
        }

        // Stable sort keeps original order for identical timestamps.
        return lines
            .Select((line, index) => (line, index))
            .OrderBy(x => x.line.TimeMs)
            .ThenBy(x => x.index)
            .Select(x => x.line)
            .ToList();
    }

    private static int ToMilliseconds(Match match)
    {
        var minutes = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);

        var fraction = 0;
        if (match.Groups[3].Success)
        {
            var digits = match.Groups[3].Value;
            var value = int.Parse(digits, CultureInfo.InvariantCulture);

            // .x = tenths, .xx = hundredths, .xxx = thousandths
            fraction = digits.Length switch
            {
                1 => value * 100,
                2 => value * 10,
                _ => value
            };
        }

        return (minutes * 60 + seconds) * 1000 + fraction;
    }
}
