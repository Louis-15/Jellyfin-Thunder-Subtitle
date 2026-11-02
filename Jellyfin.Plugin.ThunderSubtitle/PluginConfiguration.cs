using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ThunderSubtitle;

/// <summary>
/// Plugin settings, surfaced on the plugin configuration page.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets a value indicating whether the scheduled tasks may download anything.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the ids of the libraries to scan.</summary>
    public string[] LibraryIds { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets how many movies a single run may process.</summary>
    public int MaxItemsPerRun { get; set; } = 20;

    /// <summary>Gets or sets how many candidates are downloaded per movie.</summary>
    public int MaxCandidates { get; set; } = 25;

    /// <summary>Gets or sets the pause between two requests to the Xunlei API, in milliseconds.</summary>
    public int RequestIntervalMs { get; set; } = 500;

    /// <summary>
    /// Gets or sets a value indicating whether the fill task may replace a
    /// <c>&lt;name&gt;.chi.*</c> subtitle it (or the old python script) wrote earlier.
    /// Files synced from 115 (<c>&lt;name&gt;.srt</c>) are never touched.
    /// </summary>
    public bool AllowOverwritePluginSubtitle { get; set; }

    /// <summary>Gets or sets the log level written to the Jellyfin log.</summary>
    public string LogLevel { get; set; } = "Information";

    /// <summary>Parses <see cref="LogLevel"/> without throwing on a hand-edited config file.</summary>
    /// <returns>The configured level, or <see cref="Core.PluginLogLevel.Information"/>.</returns>
    public Core.PluginLogLevel ResolveLogLevel()
    {
        return Enum.TryParse<Core.PluginLogLevel>(LogLevel, ignoreCase: true, out var level)
            ? level
            : Core.PluginLogLevel.Information;
    }

    /// <summary>Returns the configured library ids that are valid GUIDs.</summary>
    /// <returns>The library ids.</returns>
    public IReadOnlyList<Guid> ResolveLibraryIds()
    {
        if (LibraryIds is null || LibraryIds.Length == 0)
        {
            return Array.Empty<Guid>();
        }

        return LibraryIds
            .Select(id => Guid.TryParse(id, out var parsed) ? parsed : (Guid?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();
    }

    /// <summary>Clamps values that a hand-edited config file could have broken.</summary>
    public void Normalize()
    {
        if (MaxItemsPerRun <= 0)
        {
            MaxItemsPerRun = 20;
        }

        if (MaxCandidates <= 0)
        {
            MaxCandidates = 25;
        }

        MaxCandidates = Math.Min(MaxCandidates, 100);
        RequestIntervalMs = Math.Clamp(RequestIntervalMs, 0, 10_000);
        LibraryIds ??= Array.Empty<string>();
        LogLevel ??= "Information";
    }
}
