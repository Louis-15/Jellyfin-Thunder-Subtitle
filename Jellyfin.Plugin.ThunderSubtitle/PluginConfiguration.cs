using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ThunderSubtitle;

/// <summary>
/// Plugin settings, surfaced on the plugin configuration page.
///
/// AI 相关的字段在 <c>PluginConfiguration.Ai.cs</c>：配置页本来就是把它们放在另一个
/// fieldset 里，混在同一个文件只会让人翻不到想改的那一项。
/// </summary>
public partial class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets a value indicating whether the scheduled tasks may download anything.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the ids of the libraries to scan.</summary>
    public string[] LibraryIds { get; set; } = Array.Empty<string>();

    /// <summary>
    /// 每个影视库各自的设置：检索方式（番号 / 片名）、是否自动补全、是否自动择优。
    ///
    /// 与 <see cref="LibraryIds"/> 分开存，是为了兼容升级前的配置：
    /// 老配置里只有 LibraryIds，没有对应条目的库按「番号 + 自动补全 + 自动择优」处理，
    /// 行为与升级前完全一致。
    /// </summary>
    public LibrarySetting[] LibrarySettings { get; set; } = Array.Empty<LibrarySetting>();

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
    /// 已废弃：旧版用它让「自动补全」也重新比较已有 <c>.chi.*</c>。
    /// 现在由每个影视库的「自动择优」开关 + 「迅雷字幕 · 自动择优」任务承担，
    /// 保留字段只为让旧配置文件里的同名节点能被正常解析（不保留也不影响解析，但会制造噪音）。
    /// </summary>
    public bool AllowOverwritePluginSubtitle { get; set; }

    /// <summary>
    /// 新片入库时立即补字幕（默认开启）。
    ///
    /// 开启后插件每 30 秒检查一次「最近 6 小时入库、已刮削完成」的影片，
    /// 只对这些片子跑一次字幕搜索，不扫全库。
    /// 原来的定时任务仍然保留，作为漏网条目的兜底。
    /// </summary>
    public bool RealtimeFill { get; set; } = true;

    /// <summary>
    /// 视频本身带内嵌字幕时，自动补全是否仍然下载外挂中文字幕。
    ///
    /// 默认关闭：内嵌字幕通常已经能看，再补一份只会让字幕列表变长。
    /// 但内嵌的 PGS 图形字幕不能搜索、不能调样式、不能单独导出，
    /// 也确实存在「能看但不够好」的情况 —— 想在这种片子上换一份外挂字幕，把这项打开即可。
    ///
    /// 手动搜索不受此限制：用户点「搜索字幕」本来就是「我想换一份」的意思。
    /// </summary>
    public bool FillDespiteEmbeddedSubtitle { get; set; }

    /// <summary>Gets or sets the log level written to the Jellyfin log.</summary>
    public string LogLevel { get; set; } = "Information";

    /// <summary>
    /// 已废弃：1.10 起语言偏好按影片类型和语言档位（<see cref="Core.LanguagePolicy"/>）推导，
    /// 不再读这个值。保留字段只为兼容旧 XML 与配置页的历史记录，改它不会有任何效果。
    /// </summary>
    public string PreferredLanguage { get; set; } = "Simplified";

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

    /// <summary>检索方式：按番号。</summary>
    public const string SearchModeCode = "Code";

    /// <summary>检索方式：按片名。</summary>
    public const string SearchModeTitle = "Title";

    /// <summary>判断某个影视库是否在目标列表里。</summary>
    /// <param name="libraryId">The library id.</param>
    /// <returns><c>true</c> when the library is selected.</returns>
    public bool IsLibraryConfigured(Guid libraryId)
    {
        return ResolveLibraryIds().Contains(libraryId);
    }

    /// <summary>
    /// 取某个影视库**显式配置**的检索方式。
    ///
    /// 与 <see cref="ResolveSearchMode"/> 的区别：库没有配置条目时返回 <c>false</c>，
    /// 而不是猜一个默认值。影片类型判定必须区分「明确是番号库」和「没配置过」——
    /// 只有前者才是日本 AV。
    /// </summary>
    /// <param name="libraryId">The library id.</param>
    /// <param name="mode">The configured mode, when there is one.</param>
    /// <returns><c>true</c> when the library has a saved setting.</returns>
    public bool TryResolveSearchMode(Guid libraryId, out Core.SubtitleSearchMode mode)
    {
        var entry = FindSetting(libraryId);
        if (entry is null)
        {
            mode = Core.SubtitleSearchMode.Title;
            return false;
        }

        mode = string.Equals(entry.Mode, SearchModeTitle, StringComparison.OrdinalIgnoreCase)
            ? Core.SubtitleSearchMode.Title
            : Core.SubtitleSearchMode.Code;
        return true;
    }

    /// <summary>
    /// 取某个影视库配置的检索方式。
    /// 没有配置条目时返回 <see cref="Core.SubtitleSearchMode.Code"/>：
    /// 升级前建的配置没有这一项，必须保持原来的番号搜索行为。
    /// 需要区分「没配置」与「明确是番号库」时请用 <see cref="TryResolveSearchMode"/>。
    /// </summary>
    /// <param name="libraryId">The library id.</param>
    /// <returns>The configured mode.</returns>
    public Core.SubtitleSearchMode ResolveSearchMode(Guid libraryId)
    {
        return TryResolveSearchMode(libraryId, out var mode)
            ? mode
            : Core.SubtitleSearchMode.Code;
    }

    /// <summary>
    /// 该影视库是否参与「自动补全」定时任务 / 实时补字幕。
    /// 默认开启：升级前的配置没有这一项，必须保持原来的行为。
    /// </summary>
    /// <param name="libraryId">The library id.</param>
    /// <returns><c>true</c> when automatic filling is enabled.</returns>
    public bool IsAutoFillEnabled(Guid libraryId)
    {
        return FindSetting(libraryId)?.AutoFill ?? true;
    }

    /// <summary>
    /// 该影视库是否参与「自动择优」定时任务。
    /// 默认开启；自动补全关掉的库不会参与择优（UI 上择优只在自动补全为「是」时才出现）。
    /// </summary>
    /// <param name="libraryId">The library id.</param>
    /// <returns><c>true</c> when automatic re-picking is enabled.</returns>
    public bool IsAutoRepickEnabled(Guid libraryId)
    {
        return IsAutoFillEnabled(libraryId) && (FindSetting(libraryId)?.AutoRepick ?? true);
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

        // 只保留合法条目，并把 Mode 归一成两个已知值：
        // 配置文件可能被手改过，也可能来自还没写这一项的老版本。
        // AutoFill / AutoRepick 是布尔值，缺省即为 true，正好等于升级前的行为。
        LibrarySettings = (LibrarySettings ?? Array.Empty<LibrarySetting>())
            .Where(entry => entry is not null && Guid.TryParse(entry.LibraryId, out _))
            .Select(entry => new LibrarySetting
            {
                LibraryId = entry.LibraryId,
                Mode = string.Equals(entry.Mode, SearchModeTitle, StringComparison.OrdinalIgnoreCase)
                    ? SearchModeTitle
                    : SearchModeCode,
                AutoFill = entry.AutoFill,
                AutoRepick = entry.AutoRepick
            })
            .ToArray();
        LogLevel ??= "Information";
        PreferredLanguage ??= "Simplified";

        NormalizeAi();
    }

    private LibrarySetting? FindSetting(Guid libraryId)
    {
        if (LibrarySettings is null)
        {
            return null;
        }

        foreach (var entry in LibrarySettings)
        {
            if (entry is not null && Guid.TryParse(entry.LibraryId, out var id) && id == libraryId)
            {
                return entry;
            }
        }

        return null;
    }
}

/// <summary>
/// 一个影视库的设置：检索方式 + 是否自动补全 + 是否自动择优。
/// 用「库 Id + 几个扁平字段」的结构而不是字典：
/// Jellyfin 用 XmlSerializer 存插件配置，Guid 作键的字典在那里不可靠。
/// </summary>
public class LibrarySetting
{
    /// <summary>Gets or sets the library id (a GUID written as a string).</summary>
    public string LibraryId { get; set; } = string.Empty;

    /// <summary>Gets or sets the mode: <c>Code</c> or <c>Title</c>.</summary>
    public string Mode { get; set; } = PluginConfiguration.SearchModeCode;

    /// <summary>Gets or sets a value indicating whether the scheduled tasks may download subtitles for this library.</summary>
    public bool AutoFill { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the "自动择优" task may replace existing subtitles in this library.</summary>
    public bool AutoRepick { get; set; } = true;
}
