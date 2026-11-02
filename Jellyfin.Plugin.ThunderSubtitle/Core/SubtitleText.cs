using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>Result of decoding a downloaded subtitle payload.</summary>
/// <param name="Text">The decoded text, BOM stripped.</param>
/// <param name="EncodingName">The encoding that produced the text, for the log.</param>
public sealed record DecodedText(string Text, string EncodingName);

/// <summary>
/// Decoding and format sniffing for subtitle payloads. Xunlei serves a mix of UTF-8,
/// UTF-8 with BOM and GB18030, and the CDN happily returns an error page instead of a
/// subtitle, so every candidate is validated before it is written to disk.
/// </summary>
public static class SubtitleText
{
    /// <summary>Extensions Jellyfin can attach as an external subtitle.</summary>
    public static readonly string[] AcceptedExtensions = [".srt", ".ass", ".ssa", ".vtt"];

    /// <summary>Extensions that count as "this movie already has a subtitle".</summary>
    public static readonly string[] RecognisedExtensions = [".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".smi", ".sup"];

    /// <summary>Upper bound for a single downloaded subtitle.</summary>
    public const long MaxBytes = 8 * 1024 * 1024;

    private static readonly string[] EncodingCandidates = ["utf-8", "gb18030", "big5", "shift_jis", "windows-1252"];

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly Regex SrtTimestamp = new(
        @"^[ \t]*(?:\d+[ \t]*\r?\n[ \t]*)?\d{1,2}:\d{2}:\d{2}[,.]\d{1,3}[ \t]*-->[ \t]*\d{1,2}:\d{2}:\d{2}[,.]\d{1,3}",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static int _encodingsRegistered;

    /// <summary>Registers the code page encodings (GB18030, Big5, Shift-JIS) exactly once.</summary>
    public static void RegisterEncodings()
    {
        if (Interlocked.Exchange(ref _encodingsRegistered, 1) == 0)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
    }

    /// <summary>Decodes a payload, picking the encoding whose text looks most like a Chinese subtitle.</summary>
    /// <param name="bytes">The raw payload.</param>
    /// <returns>The decoded text, or <c>null</c> when the payload is not text at all.</returns>
    public static DecodedText? TryDecode(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return null;
        }

        RegisterEncodings();

        // A BOM is authoritative; guessing would only corrupt a file that told us.
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return DecodeChecked(StrictUtf8, bytes.AsSpan(3), "utf-8-sig") is { } text
                ? new DecodedText(text, "utf-8-sig")
                : null;
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return DecodeChecked(Encoding.Unicode, bytes.AsSpan(2), "utf-16-le") is { } text
                ? new DecodedText(text, "utf-16-le")
                : null;
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return DecodeChecked(Encoding.BigEndianUnicode, bytes.AsSpan(2), "utf-16-be") is { } text
                ? new DecodedText(text, "utf-16-be")
                : null;
        }

        DecodedText? best = null;
        var bestScore = int.MinValue;

        foreach (var name in EncodingCandidates)
        {
            var encoding = TryGetStrictEncoding(name);
            if (encoding is null)
            {
                continue;
            }

            var text = DecodeChecked(encoding, bytes, name);
            if (text is null)
            {
                continue;
            }

            var score = Score(text);
            if (score > bestScore)
            {
                bestScore = score;
                best = new DecodedText(text, name);
            }
        }

        if (best is null)
        {
            return null;
        }

        // Nothing decodable as text (every candidate threw) or the bytes are binary noise.
        return bestScore <= -64 ? null : best;
    }

    /// <summary>
    /// Works out which extension the content really is. The declared extension from the
    /// API is not trusted: <c>.smi</c> files are rejected entirely and <c>.srt</c>s that
    /// contain ASS are renamed.
    /// </summary>
    /// <param name="text">The decoded subtitle text.</param>
    /// <param name="declaredExtension">The extension the API reported (without the dot), if any.</param>
    /// <returns>A whitelisted extension including the dot, or <c>null</c> when unsupported.</returns>
    public static string? DetectExtension(string text, string? declaredExtension)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 16)
        {
            return null;
        }

        var head = text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');

        if (head.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase))
        {
            return ".vtt";
        }

        var hasScriptInfo = text.Contains("[Script Info]", StringComparison.OrdinalIgnoreCase);
        var hasEvents = text.Contains("[Events]", StringComparison.OrdinalIgnoreCase);
        var hasDialogue = Regex.IsMatch(text, @"^[ \t]*Dialogue[ \t]*:", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (hasScriptInfo || (hasEvents && hasDialogue))
        {
            var isV4 = text.Contains("[V4 Styles]", StringComparison.OrdinalIgnoreCase)
                       && !text.Contains("[V4+ Styles]", StringComparison.OrdinalIgnoreCase);
            return isV4 ? ".ssa" : ".ass";
        }

        if (SrtTimestamp.IsMatch(text))
        {
            return ".srt";
        }

        // Last resort: a declared extension we accept and a payload that at least carries
        // a timestamp arrow. Anything else (cd'd HTML, JSON, plain text) is dropped.
        if (!string.IsNullOrEmpty(declaredExtension)
            && AcceptedExtensions.Contains("." + NormalizeExtension(declaredExtension), StringComparer.OrdinalIgnoreCase)
            && text.Contains("-->", StringComparison.Ordinal))
        {
            return "." + NormalizeExtension(declaredExtension);
        }

        return null;
    }

    /// <summary>Lower cases an extension and drops any leading dots.</summary>
    /// <param name="extension">The extension, with or without the dot.</param>
    /// <returns>The bare extension, or an empty string.</returns>
    public static string NormalizeExtension(string? extension)
    {
        return (extension ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
    }

    /// <summary>Normalizes line endings so CRLF/LF duplicates hash the same.</summary>
    /// <param name="text">The subtitle text.</param>
    /// <returns>The normalized text.</returns>
    public static string Normalize(string text)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim('\uFEFF', ' ', '\t', '\r', '\n');
    }

    /// <summary>Hashes the normalized text; CRLF and LF copies of one subtitle collide on purpose.</summary>
    /// <param name="text">The subtitle text.</param>
    /// <returns>A hex MD5 digest.</returns>
    public static string Hash(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(Normalize(text));
        return Convert.ToHexString(MD5.HashData(bytes));
    }

    /// <summary>Counts characters that suggest the decode produced mojibake.</summary>
    /// <param name="text">The decoded text.</param>
    /// <returns>CJK characters *3 minus suspicious characters.</returns>
    public static int Score(string text)
    {
        var cjk = 0;
        var latin1 = 0;
        var bad = 0;

        foreach (var ch in text)
        {
            if (IsCjk(ch))
            {
                cjk++;
            }
            else if (ch >= '\u0080' && ch <= '\u00FF')
            {
                latin1++;
            }
            else if (ch == '\uFFFD'
                     || (ch < ' ' && ch != '\r' && ch != '\n' && ch != '\t')
                     || (ch >= '\u0080' && ch <= '\u009F')
                     || (ch >= '\uE000' && ch <= '\uF8FF')

                     // Half-width katakana is what Shift-JIS produces when it decodes
                     // GB18030 bytes; a Chinese subtitle never contains it.
                     || (ch >= '\uFF61' && ch <= '\uFF9F'))
            {
                bad++;
            }
        }

        return (cjk * 3) - (latin1 * 2) - (bad * 8);
    }

    private static bool IsCjk(char ch)
    {
        if (ch >= '\uFF61' && ch <= '\uFF9F')
        {
            // Half-width katakana: never part of a Chinese subtitle, always mojibake here.
            return false;
        }

        return (ch >= '\u3000' && ch <= '\u303F')
               || (ch >= '\u3040' && ch <= '\u30FF')
               || (ch >= '\u3400' && ch <= '\u4DBF')
               || (ch >= '\u4E00' && ch <= '\u9FFF')
               || (ch >= '\uF900' && ch <= '\uFAFF')
               || (ch >= '\uFF00' && ch <= '\uFF60');
    }

    private static string? DecodeChecked(Encoding encoding, ReadOnlySpan<byte> bytes, string name)
    {
        try
        {
            var text = encoding.GetString(bytes);
            return text.Length == 0 ? null : text;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static Encoding? TryGetStrictEncoding(string name)
    {
        if (string.Equals(name, "utf-8", StringComparison.Ordinal))
        {
            return StrictUtf8;
        }

        try
        {
            return Encoding.GetEncoding(name, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
