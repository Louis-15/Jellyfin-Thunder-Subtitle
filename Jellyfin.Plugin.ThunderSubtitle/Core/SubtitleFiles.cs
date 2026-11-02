using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>
/// Locates the external Chinese subtitles that live next to a video. Jellyfin names the file
/// it writes after the provider response (<c>&lt;name&gt;.chi.srt</c>) and, when that name is
/// taken, appends a counter (<c>&lt;name&gt;.chi.0.srt</c>) instead of replacing it. Picking a
/// subtitle by hand must not leave a pile of tracks behind, so the old ones are cleared first.
/// </summary>
public static class SubtitleFiles
{
    /// <summary>Extensions Jellyfin treats as subtitles.</summary>
    public static readonly string[] Extensions = [".srt", ".ass", ".ssa", ".vtt", ".sub", ".smi", ".idx"];

    /// <summary>Language tags this plugin and Jellyfin use for Chinese.</summary>
    private static readonly string[] Tags = ["chi", "zh", "zho", "zh-cn", "zh-hans", "zh-hant"];

    /// <summary>
    /// Finds every <c>&lt;base&gt;.&lt;chinese tag&gt;[.n].&lt;subtitle ext&gt;</c> next to the video.
    /// </summary>
    /// <param name="mediaPath">Path of the video (.strm) file.</param>
    /// <returns>Full paths, ordered, never <c>null</c>.</returns>
    public static IReadOnlyList<string> FindChinese(string mediaPath)
    {
        if (string.IsNullOrWhiteSpace(mediaPath))
        {
            return Array.Empty<string>();
        }

        string directory;
        string baseName;
        try
        {
            directory = Path.GetDirectoryName(mediaPath) ?? string.Empty;
            baseName = Path.GetFileNameWithoutExtension(mediaPath);
        }
        catch (ArgumentException)
        {
            return Array.Empty<string>();
        }

        if (directory.Length == 0 || baseName.Length == 0 || !Directory.Exists(directory))
        {
            return Array.Empty<string>();
        }

        var prefix = baseName + ".";
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileName(file);
            var extension = Path.GetExtension(name);
            if (!Extensions.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // <name>.<tag>[.<n>].<ext> -> tag is the first segment after the base name.
            var tail = name[prefix.Length..];
            var dot = tail.IndexOf('.', StringComparison.Ordinal);
            var tag = dot < 0 ? tail : tail[..dot];
            if (Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
            {
                found.Add(file);
            }
        }

        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }
}
