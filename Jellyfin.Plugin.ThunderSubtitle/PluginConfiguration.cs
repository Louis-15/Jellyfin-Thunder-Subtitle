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
    public int MaxItemsPerRun { get; set; } = 5;

    /// <summary>Gets or sets how many candidates are downloaded per movie.</summary>
    public int MaxCandidates { get; set; } = 25;

    /// <summary>Gets or sets the pause between two requests to the Xunlei API, in milliseconds.</summary>
    public int RequestIntervalMs { get; set; } = 500;

    /// <summary>Gets or sets how many candidates are downloaded at the same time.</summary>
    public int DownloadConcurrency { get; set; } = 4;

    /// <summary>
    /// Gets or sets the total time budget for one item's downloads. Whatever arrived by
    /// then is ranked; the rest is abandoned instead of stalling the whole run.
    /// </summary>
    public int ItemBudgetSeconds { get; set; } = 90;

    /// <summary>
    /// Gets or sets a value indicating whether the fill task may replace a
    /// <c>&lt;name&gt;.chi.*</c> subtitle it (or the old python script) wrote earlier.
    /// Files synced from 115 (<c>&lt;name&gt;.srt</c>) are never touched.
    /// </summary>
    public bool AllowOverwritePluginSubtitle { get; set; }

    /// <summary>Gets or sets the log level written to the Jellyfin log.</summary>
    public string LogLevel { get; set; } = "Information";

    /// <summary>
    /// Gets or sets the Chinese form to prefer: <c>Simplified</c> (default) or
    /// <c>Traditional</c>. Both are always acceptable; this only breaks the tie.
    /// </summary>
    public string PreferredLanguage { get; set; } = "Simplified";

    /// <summary>
    /// Gets or sets a value indicating whether the largest finalists are sent to a chat
    /// model so it can tell a real translation from verbose machine output.
    /// </summary>
    public bool AiEnabled { get; set; }

    /// <summary>Gets or sets the OpenAI-compatible API root, e.g. <c>https://api.deepseek.com/v1</c>.</summary>
    public string AiBaseUrl { get; set; } = "https://api.deepseek.com/v1";

    /// <summary>Gets or sets the API key. Sent as a bearer token, never logged.</summary>
    public string AiApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the model name.</summary>
    public string AiModel { get; set; } = "deepseek-chat";

    /// <summary>Gets or sets how many of the largest candidates are compared by the model.</summary>
    public int AiTopCandidates { get; set; } = 3;

    /// <summary>Gets or sets how many cue lines are sampled from each candidate.</summary>
    public int AiSampleCues { get; set; } = 24;

    /// <summary>Gets or sets the hard cap on sampled characters per candidate.</summary>
    public int AiMaxCharsPerCandidate { get; set; } = 3000;

    /// <summary>Gets or sets the per-request timeout.</summary>
    public int AiTimeoutSeconds { get; set; } = 60;

    /// <summary>Gets a value indicating whether the AI check has everything it needs.</summary>
    public bool IsAiUsable => AiEnabled
                              && !string.IsNullOrWhiteSpace(AiBaseUrl)
                              && !string.IsNullOrWhiteSpace(AiModel);

    /// <summary>Parses <see cref="LogLevel"/> without throwing on a hand-edited config file.</summary>
    /// <returns>The configured level, or <see cref="Core.PluginLogLevel.Information"/>.</returns>
    public Core.PluginLogLevel ResolveLogLevel()
    {
        return Enum.TryParse<Core.PluginLogLevel>(LogLevel, ignoreCase: true, out var level)
            ? level
            : Core.PluginLogLevel.Information;
    }

    /// <summary>Parses <see cref="PreferredLanguage"/> into the enum used by the ranker.</summary>
    /// <returns>The preferred Chinese form.</returns>
    public Core.SubtitleLanguage ResolvePreferredLanguage()
    {
        return string.Equals(PreferredLanguage, "Traditional", StringComparison.OrdinalIgnoreCase)
            ? Core.SubtitleLanguage.TraditionalChinese
            : Core.SubtitleLanguage.SimplifiedChinese;
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
            MaxItemsPerRun = 5;
        }

        if (MaxCandidates <= 0)
        {
            MaxCandidates = 25;
        }

        MaxCandidates = Math.Min(MaxCandidates, 100);
        RequestIntervalMs = Math.Clamp(RequestIntervalMs, 0, 10_000);
        DownloadConcurrency = Math.Clamp(DownloadConcurrency, 1, 8);
        ItemBudgetSeconds = Math.Clamp(ItemBudgetSeconds, 20, 600);
        LibraryIds ??= Array.Empty<string>();
        LogLevel ??= "Information";
        PreferredLanguage ??= "Simplified";

        AiBaseUrl ??= string.Empty;
        AiApiKey ??= string.Empty;
        AiModel ??= string.Empty;
        AiTopCandidates = Math.Clamp(AiTopCandidates, 2, 5);
        AiSampleCues = Math.Clamp(AiSampleCues, 4, 80);
        AiMaxCharsPerCandidate = Math.Clamp(AiMaxCharsPerCandidate, 500, 20_000);
        AiTimeoutSeconds = Math.Clamp(AiTimeoutSeconds, 10, 300);
    }
}
