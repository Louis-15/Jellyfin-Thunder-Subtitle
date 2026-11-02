using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>
/// Path rules for sidecar subtitles. Two naming families live next to a media file:
/// <c>&lt;name&gt;.srt</c> / <c>&lt;name&gt;.zh.srt</c> (synced from 115, never touched)
/// and <c>&lt;name&gt;.chi.srt</c> (written by this plugin or the old python script).
/// </summary>
public static class Sidecar
{
    /// <summary>Gets the language tag written into every sidecar this plugin creates.</summary>
    public const string LanguageTag = "chi";

    /// <summary>Media file name without its extension; subtitles are keyed off this.</summary>
    /// <param name="mediaPath">Path to the media file.</param>
    /// <returns>The bare file name.</returns>
    public static string BaseName(string mediaPath) => Path.GetFileNameWithoutExtension(mediaPath);

    /// <summary>Directory the media file lives in.</summary>
    /// <param name="mediaPath">Path to the media file.</param>
    /// <returns>The directory, or an empty string.</returns>
    public static string Directory(string mediaPath) => Path.GetDirectoryName(mediaPath) ?? string.Empty;

    /// <summary>Builds the sidecar path for an extension, e.g. <c>ABF-363-U.chi.srt</c>.</summary>
    /// <param name="mediaPath">Path to the media file.</param>
    /// <param name="extension">Extension including the dot, or bare.</param>
    /// <returns>The sidecar path.</returns>
    public static string TargetPath(string mediaPath, string extension)
    {
        var ext = SubtitleText.NormalizeExtension(extension);
        return Path.Combine(Directory(mediaPath), $"{BaseName(mediaPath)}.{LanguageTag}.{ext}");
    }

    /// <summary>
    /// Tells whether a path is a plugin owned sidecar (<c>&lt;name&gt;.chi.&lt;ext&gt;</c>).
    /// Jellyfin's conflict copies (<c>&lt;name&gt;.chi.0.srt</c>) are deliberately excluded.
    /// </summary>
    /// <param name="mediaPath">Path to the media file.</param>
    /// <param name="path">Candidate subtitle path.</param>
    /// <returns><c>true</c> when the plugin owns the file.</returns>
    public static bool IsPluginSubtitle(string mediaPath, string path)
    {
        var rest = Remainder(mediaPath, path);
        if (rest is null)
        {
            return false;
        }

        return rest.StartsWith(LanguageTag + ".", StringComparison.OrdinalIgnoreCase)
               && rest.IndexOf('.', LanguageTag.Length + 1) < 0
               && SubtitleText.RecognisedExtensions.Contains(
                   "." + SubtitleText.NormalizeExtension(rest[(LanguageTag.Length + 1)..]),
                   StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Lists every sidecar-ish subtitle next to the media file.</summary>
    /// <param name="mediaPath">Path to the media file.</param>
    /// <returns>Full paths, sorted for stable logs.</returns>
    public static IReadOnlyList<string> FindSubtitles(string mediaPath)
    {
        var directory = Directory(mediaPath);
        if (directory.Length == 0 || !System.IO.Directory.Exists(directory))
        {
            return Array.Empty<string>();
        }

        try
        {
            return System.IO.Directory.EnumerateFiles(directory)
                .Where(path => Remainder(mediaPath, path) is not null
                               && SubtitleText.RecognisedExtensions.Contains(
                                   Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }
        catch (DirectoryNotFoundException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Lists the <c>&lt;name&gt;.chi.&lt;ext&gt;</c> files written by this plugin.</summary>
    /// <param name="mediaPath">Path to the media file.</param>
    /// <returns>Full paths, sorted for stable logs.</returns>
    public static IReadOnlyList<string> FindPluginSubtitles(string mediaPath)
    {
        return FindSubtitles(mediaPath).Where(path => IsPluginSubtitle(mediaPath, path)).ToArray();
    }

    /// <summary>
    /// Deletes plugin owned sidecars that are not <paramref name="keep"/>, so switching
    /// from <c>.chi.srt</c> to <c>.chi.ass</c> does not leave two subtitles behind.
    /// </summary>
    /// <param name="mediaPath">Path to the media file.</param>
    /// <param name="keep">The file that is about to be written.</param>
    /// <returns>The paths that were removed.</returns>
    public static IReadOnlyList<string> RemoveStalePluginSubtitles(string mediaPath, string keep)
    {
        var removed = new List<string>();
        foreach (var path in FindPluginSubtitles(mediaPath))
        {
            if (string.Equals(path, keep, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                File.Delete(path);
                removed.Add(path);
            }
            catch (IOException)
            {
                // Leaving a stale file behind is better than failing the whole item.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    /// <summary>Writes the file atomically so a crash never leaves a truncated subtitle.</summary>
    /// <param name="path">Destination path.</param>
    /// <param name="bytes">Content.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
        {
            System.IO.Directory.CreateDirectory(directory);
        }

        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>Returns <c>&lt;ext&gt;.&lt;ext&gt;</c> when the file is a sidecar of the media file.</summary>
    private static string? Remainder(string mediaPath, string path)
    {
        var baseName = BaseName(mediaPath);
        var name = Path.GetFileName(path);
        var prefix = baseName + ".";
        if (name.Length <= prefix.Length || !name.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        return name[prefix.Length..];
    }
}
