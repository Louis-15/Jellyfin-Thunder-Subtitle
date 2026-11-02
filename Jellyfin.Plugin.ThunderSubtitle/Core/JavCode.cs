using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>
/// Extracts the JAV code (番号) from a file name, e.g.
/// <c>ABF-363-U.strm</c> or <c>[ThZu.Cc]SSIS-706-C.4K.strm</c> both reduce to a code the
/// Xunlei API understands (<c>ABF-363</c>, <c>SSIS-706</c>).
/// </summary>
public static class JavCode
{
    // Codes with a non standard shape: FC2-PPV-1234567, HEYZO-1234.
    private static readonly Regex SpecialCode = new(
        @"(?<![A-Za-z0-9])(?:(?<pre>FC2)[-_ ]?PPV[-_ ]?(?<num>\d{5,8})|(?<pre>HEYZO)[-_ ]?(?<num>\d{3,5}))(?![0-9])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // The ordinary <label>-<number> shape used by every studio.
    private static readonly Regex StandardCode = new(
        @"(?<![A-Za-z0-9])(?<pre>[A-Za-z]{2,6})(?<sep>[-_ ])(?<num>\d{2,5})(?![0-9])",
        RegexOptions.Compiled);

    private static readonly Regex Bracketed = new(@"\[[^\]]*\]|【[^】]*】|\([^)]*\)", RegexOptions.Compiled);

    /// <summary>Extracts a code from a media file path.</summary>
    /// <param name="path">The media file path (usually a <c>.strm</c>).</param>
    /// <returns>The normalized code, or <c>null</c> when the name carries none.</returns>
    public static string? FromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return FromText(Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>Extracts a code from arbitrary text (file name or item name).</summary>
    /// <param name="text">The text to scan.</param>
    /// <returns>The normalized code, or <c>null</c>.</returns>
    public static string? FromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = NormalizeWidth(text);
        var stripped = Bracketed.Replace(normalized, " ");

        var special = SpecialCode.Match(stripped);
        if (special.Success)
        {
            var prefix = special.Groups["pre"].Value.ToUpperInvariant();
            var digits = special.Groups["num"].Value;
            return prefix is "FC2" ? $"FC2-PPV-{digits}" : $"{prefix}-{digits}";
        }

        var standard = StandardCode.Match(stripped);
        while (standard.Success)
        {
            var prefix = standard.Groups["pre"].Value;
            var separator = standard.Groups["sep"].Value[0];
            var digits = standard.Groups["num"].Value;

            // "The Matrix 1999" must not turn into MATRIX-1999: a space separated code is
            // only accepted when the label is written in capitals the way releases are.
            var plausible = separator != ' ' || prefix.All(char.IsUpper);

            if (plausible && digits.Length >= 2)
            {
                return $"{prefix.ToUpperInvariant()}-{digits}";
            }

            standard = standard.NextMatch();
        }

        return null;
    }

    /// <summary>
    /// Comparison form of a code: leading zeros dropped, so <c>NIMA-086</c> and
    /// <c>NIMA-86</c> are the same release. Never used for the API query itself: the
    /// Xunlei index treats the two spellings as different searches.
    /// </summary>
    /// <param name="code">A code such as <c>ABF-0363</c>.</param>
    /// <returns>The comparison key, e.g. <c>ABF-363</c>.</returns>
    public static string CompareKey(string code)
    {
        var index = code.LastIndexOf('-');
        if (index < 0 || index == code.Length - 1)
        {
            return code.ToUpperInvariant();
        }

        var digits = code[(index + 1)..];
        if (!digits.All(char.IsAsciiDigit))
        {
            return code.ToUpperInvariant();
        }

        return $"{code[..index].ToUpperInvariant()}-{TrimLeadingZeros(digits)}";
    }

    /// <summary>
    /// The other spelling of a code, when its number has leading zeros: <c>NIMA-086</c>
    /// gives <c>NIMA-86</c> and the other way round. The Xunlei index only answers one
    /// of the two, and which one differs per studio, so both are tried.
    /// </summary>
    /// <param name="code">A code such as <c>NIMA-086</c>.</param>
    /// <returns>The alternative spelling, or <c>null</c> when there is nothing to try.</returns>
    public static string? AlternativeSpelling(string code)
    {
        var key = CompareKey(code);
        return string.Equals(key, code, StringComparison.OrdinalIgnoreCase) ? null : key;
    }

    private static string TrimLeadingZeros(string digits)
    {
        var trimmed = digits.TrimStart('0');
        return trimmed.Length == 0 ? "0" : trimmed;
    }

    /// <summary>Maps full width ASCII characters back so <c>ＡＢＦ－３６３</c> still parses.</summary>
    private static string NormalizeWidth(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch >= '\uFF01' && ch <= '\uFF5E')
            {
                builder.Append((char)(ch - 0xFEE0));
            }
            else if (ch == '\u3000')
            {
                builder.Append(' ');
            }
            else
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }
}
