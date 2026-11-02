using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>A single subtitle line with its timing.</summary>
/// <param name="Start">Start time, or <see cref="TimeSpan.Zero"/> when unknown.</param>
/// <param name="End">End time, or <see cref="TimeSpan.Zero"/> when unknown.</param>
/// <param name="Text">The dialogue text, markup already stripped.</param>
public sealed record SubtitleCue(TimeSpan Start, TimeSpan End, string Text);

/// <summary>What a subtitle contains, independent of how it is packaged.</summary>
/// <param name="CueCount">Number of dialogue lines.</param>
/// <param name="ContentChars">Letters and digits in the dialogue text, punctuation excluded.</param>
public sealed record SubtitleStats(int CueCount, int ContentChars)
{
    /// <summary>An empty subtitle.</summary>
    public static readonly SubtitleStats Empty = new(0, 0);
}

/// <summary>
/// Parses SRT/VTT/ASS/SSA into cues and measures how much dialogue they actually carry.
/// Byte size cannot be compared across formats: an ASS file stores a style block, field
/// prefixes and override tags that can be a fifth of its size, so the same subtitle looks
/// 20% "bigger" merely because it is wrapped in ASS.
/// </summary>
public static class SubtitleContent
{
    private static readonly string[] AssEventFields = ["Layer", "Start", "End", "Style", "Name", "MarginL", "MarginR", "MarginV", "Effect", "Text"];

    /// <summary>Measures the dialogue volume of a subtitle.</summary>
    /// <param name="text">The subtitle text.</param>
    /// <param name="extension">The extension including the dot, e.g. <c>.ass</c>.</param>
    /// <returns>The cue count and character count.</returns>
    public static SubtitleStats Measure(string text, string extension)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return SubtitleStats.Empty;
        }

        var cues = Parse(text, extension);
        if (cues.Count == 0)
        {
            // Unparseable but decoded: fall back to counting the visible text so the
            // candidate still gets a comparable number instead of zero.
            var plain = StripMarkup(text);
            return new SubtitleStats(0, CountContentChars(plain));
        }

        var total = 0;
        foreach (var cue in cues)
        {
            total += CountContentChars(cue.Text);
        }

        return new SubtitleStats(cues.Count, total);
    }

    /// <summary>Parses a subtitle into cues.</summary>
    /// <param name="text">The subtitle text.</param>
    /// <param name="extension">The extension including the dot.</param>
    /// <returns>The cues, empty when the format is unknown or the text is malformed.</returns>
    public static IReadOnlyList<SubtitleCue> Parse(string text, string extension)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<SubtitleCue>();
        }

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

        return SubtitleText.NormalizeExtension(extension) switch
        {
            "ass" or "ssa" => ParseAss(normalized),
            "vtt" => ParseTextCues(normalized, isVtt: true),
            _ => ParseTextCues(normalized, isVtt: false)
        };
    }

    /// <summary>Removes subtitle markup, leaving the words the viewer actually reads.</summary>
    /// <param name="raw">A cue text or a whole file.</param>
    /// <returns>The plain text.</returns>
    public static string StripMarkup(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(raw.Length);
        var index = 0;

        while (index < raw.Length)
        {
            var current = raw[index];

            // ASS override blocks: {\pos(320,50)\b1} ...
            if (current == '{')
            {
                var close = raw.IndexOf('}', index);
                if (close < 0)
                {
                    break;
                }

                index = close + 1;
                continue;
            }

            // HTML-ish tags used by SRT and VTT: <i>, <font color=...>, <c.yellow>
            if (current == '<')
            {
                var close = raw.IndexOf('>', index);
                if (close > 0 && close - index <= 80)
                {
                    index = close + 1;
                    continue;
                }
            }

            // ASS line breaks and hard spaces.
            if (current == '\\' && index + 1 < raw.Length && (raw[index + 1] is 'N' or 'n' or 'h'))
            {
                builder.Append(' ');
                index += 2;
                continue;
            }

            builder.Append(current);
            index++;
        }

        return builder.ToString();
    }

    /// <summary>Counts letters and digits, which is what carries meaning in any script.</summary>
    /// <param name="text">Plain text.</param>
    /// <returns>The character count.</returns>
    public static int CountContentChars(string text)
    {
        var count = 0;
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                count++;
            }
        }

        return count;
    }

    private static IReadOnlyList<SubtitleCue> ParseAss(string text)
    {
        var cues = new List<SubtitleCue>();
        string[] fields = AssEventFields;
        var inEvents = false;

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith('['))
            {
                inEvents = trimmed.StartsWith("[Events]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inEvents)
            {
                continue;
            }

            if (trimmed.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                fields = trimmed["Format:".Length..]
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                continue;
            }

            if (!trimmed.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = trimmed["Dialogue:".Length..]
                .Split(',', fields.Length, StringSplitOptions.None);

            if (parts.Length < 2)
            {
                continue;
            }

            var start = TimeSpan.Zero;
            var end = TimeSpan.Zero;
            var textIndex = parts.Length - 1;

            for (var i = 0; i < parts.Length; i++)
            {
                var name = i < fields.Length ? fields[i] : string.Empty;
                if (name.Equals("Start", StringComparison.OrdinalIgnoreCase))
                {
                    start = ParseAssTime(parts[i]);
                }
                else if (name.Equals("End", StringComparison.OrdinalIgnoreCase))
                {
                    end = ParseAssTime(parts[i]);
                }
                else if (name.Equals("Text", StringComparison.OrdinalIgnoreCase))
                {
                    textIndex = i;
                    break;
                }
            }

            cues.Add(new SubtitleCue(start, end, StripMarkup(parts[textIndex])));
        }

        return cues;
    }

    private static IReadOnlyList<SubtitleCue> ParseTextCues(string text, bool isVtt)
    {
        var cues = new List<SubtitleCue>();
        var lines = text.Split('\n');
        var index = 0;

        while (index < lines.Length)
        {
            var line = lines[index].Trim();

            if (line.Length == 0
                || (isVtt && (line.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase)
                              || line.StartsWith("NOTE", StringComparison.OrdinalIgnoreCase)
                              || line.StartsWith("STYLE", StringComparison.OrdinalIgnoreCase)
                              || line.StartsWith("REGION", StringComparison.OrdinalIgnoreCase))))
            {
                index++;
                continue;
            }

            var arrow = line.IndexOf("-->", StringComparison.Ordinal);
            if (arrow < 0)
            {
                // Either an SRT index line or a VTT cue identifier: the timestamp follows.
                index++;
                continue;
            }

            var start = ParseTextTime(line[..arrow]);
            var end = ParseTextTime(line[(arrow + 3)..]);

            index++;
            var body = new List<string>();
            while (index < lines.Length && lines[index].Trim().Length > 0)
            {
                body.Add(StripMarkup(lines[index]));
                index++;
            }

            cues.Add(new SubtitleCue(start, end, string.Join(" ", body)));
        }

        return cues;
    }

    private static TimeSpan ParseAssTime(string value)
    {
        // H:MM:SS.cc
        return TimeSpan.TryParseExact(value.Trim(), @"h\:mm\:ss\.ff", CultureInfo.InvariantCulture, out var result)
            || TimeSpan.TryParse(value.Trim(), CultureInfo.InvariantCulture, out result)
            ? result
            : TimeSpan.Zero;
    }

    private static TimeSpan ParseTextTime(string value)
    {
        // 00:00:01,000 -> take the first token, VTT may append cue settings after the end time.
        var token = value.Trim().Split(' ', 2)[0].Replace(',', '.');
        if (TimeSpan.TryParseExact(token, @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out var result)
            || TimeSpan.TryParseExact(token, @"hh\:mm\:ss\.ff", CultureInfo.InvariantCulture, out result)
            || TimeSpan.TryParseExact(token, @"hh\:mm\:ss\.f", CultureInfo.InvariantCulture, out result)
            || TimeSpan.TryParseExact(token, @"mm\:ss\.fff", CultureInfo.InvariantCulture, out result)
            || TimeSpan.TryParseExact(token, @"mm\:ss\.ff", CultureInfo.InvariantCulture, out result))
        {
            return result;
        }

        return TimeSpan.TryParse(token, CultureInfo.InvariantCulture, out result) ? result : TimeSpan.Zero;
    }
}
