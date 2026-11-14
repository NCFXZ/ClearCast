using System.Globalization;
using System.Text;

namespace ClearCast;

internal readonly record struct LyricLine(long TimeMs, string Text);

internal enum LyricsKind { Synced, Plain, Instrumental }

/// <summary>Bounded, sanitized lyrics for the current track; synced lines are sorted by time.</summary>
internal sealed record Lyrics(LyricsKind Kind, IReadOnlyList<LyricLine> Lines)
{
    internal const int MaximumLines = 2000;
    internal const int MaximumLineLength = 400;
    private const long MaximumTimeMs = 7L * 24 * 60 * 60 * 1000;

    internal static readonly Lyrics Instrumental = new(LyricsKind.Instrumental, []);

    internal static Lyrics? ParseSynced(string? source)
    {
        if (string.IsNullOrEmpty(source)) return null;
        long offset = 0;
        var lines = new List<LyricLine>();
        var times = new List<long>(4);
        ReadOnlySpan<char> input = source.AsSpan();
        foreach (Range range in input.Split('\n'))
        {
            ReadOnlySpan<char> line = input[range].Trim();
            times.Clear();
            while (line.Length > 0 && line[0] == '[')
            {
                int close = line.IndexOf(']');
                if (close < 0) break;
                ReadOnlySpan<char> tag = line[1..close];
                if (TryParseTime(tag, out long time))
                {
                    if (times.Count < MaximumLines) times.Add(time);
                }
                else if (times.Count == 0 && tag.StartsWith("offset:", StringComparison.OrdinalIgnoreCase) &&
                    long.TryParse(tag[7..].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value))
                    offset = Math.Clamp(value, -MaximumTimeMs, MaximumTimeMs);
                else break;
                line = line[(close + 1)..];
            }
            if (times.Count == 0 || lines.Count == MaximumLines) continue;
            string text = Clean(line);
            foreach (long time in times)
            {
                if (lines.Count == MaximumLines) break;
                lines.Add(new LyricLine(time, text));
            }
        }
        // A positive LRC offset shows lines earlier.
        var sorted = lines.Select(line => line with { TimeMs = Math.Clamp(line.TimeMs - offset, 0, MaximumTimeMs) })
            .OrderBy(line => line.TimeMs).ToArray();
        return sorted.Any(line => line.Text.Length > 0) ? new Lyrics(LyricsKind.Synced, sorted) : null;
    }

    internal static Lyrics? ParsePlain(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        var lines = new List<LyricLine>();
        ReadOnlySpan<char> input = source.AsSpan();
        foreach (Range range in input.Split('\n'))
        {
            lines.Add(new LyricLine(0, Clean(input[range])));
            if (lines.Count == MaximumLines) break;
        }
        return lines.Any(line => line.Text.Length > 0) ? new Lyrics(LyricsKind.Plain, lines) : null;
    }

    /// <summary>Index of the last synced line at or before the position, or -1.</summary>
    internal int ActiveIndex(long positionMs)
    {
        if (Kind != LyricsKind.Synced) return -1;
        int low = 0, high = Lines.Count - 1, found = -1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            if (Lines[middle].TimeMs <= positionMs) { found = middle; low = middle + 1; }
            else high = middle - 1;
        }
        return found;
    }

    /// <summary>Time of the next synced line change after the position, or -1.</summary>
    internal long NextChangeMs(long positionMs)
    {
        if (Kind != LyricsKind.Synced) return -1;
        int next = ActiveIndex(positionMs) + 1;
        while (next < Lines.Count && Lines[next].TimeMs <= positionMs) ++next;
        return next < Lines.Count ? Lines[next].TimeMs : -1;
    }

    private static bool TryParseTime(ReadOnlySpan<char> tag, out long milliseconds)
    {
        milliseconds = 0;
        int colon = tag.IndexOf(':');
        if (colon is < 1 or > 4 || !uint.TryParse(tag[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out uint minutes))
            return false;
        ReadOnlySpan<char> rest = tag[(colon + 1)..];
        int separator = rest.IndexOfAny('.', ':');
        ReadOnlySpan<char> secondsText = separator < 0 ? rest : rest[..separator];
        if (secondsText.Length is < 1 or > 2 ||
            !uint.TryParse(secondsText, NumberStyles.None, CultureInfo.InvariantCulture, out uint seconds) || seconds >= 60)
            return false;
        uint fraction = 0;
        if (separator >= 0)
        {
            ReadOnlySpan<char> fractionText = rest[(separator + 1)..];
            if (fractionText.Length is < 1 or > 3 ||
                !uint.TryParse(fractionText, NumberStyles.None, CultureInfo.InvariantCulture, out fraction))
                return false;
            fraction *= fractionText.Length switch { 1 => 100u, 2 => 10u, _ => 1u };
        }
        milliseconds = (minutes * 60L + seconds) * 1000 + fraction;
        return milliseconds <= MaximumTimeMs;
    }

    private static string Clean(ReadOnlySpan<char> text)
    {
        var builder = new StringBuilder(Math.Min(text.Length, MaximumLineLength));
        foreach (char character in text.Trim())
        {
            if (builder.Length >= MaximumLineLength - (char.IsHighSurrogate(character) ? 1 : 0)) break;
            if (!char.IsControl(character)) builder.Append(character);
        }
        return builder.ToString().Trim();
    }
}
