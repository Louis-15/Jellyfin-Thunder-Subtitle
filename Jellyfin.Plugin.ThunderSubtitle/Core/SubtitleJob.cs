using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>The outcome of looking at a single movie.</summary>
public enum ProcessResult
{
    /// <summary>Nothing was searched, so the movie does not count towards the per-run limit.</summary>
    Skipped,

    /// <summary>A subtitle was written (or upgraded).</summary>
    Written,

    /// <summary>The API had no usable candidate.</summary>
    NoCandidates,

    /// <summary>The subtitle on disk was already at least as good.</summary>
    KeptExisting,

    /// <summary>The search or download failed.</summary>
    Failed
}

/// <summary>
/// The work behind both scheduled tasks: pick movies, extract the code, download every
/// candidate, rank them and write the best one as a sidecar next to the media file.
/// </summary>
public sealed class SubtitleJob
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IDirectoryService _directoryService;
    private readonly ISubtitleFinder _finder;
    private readonly ProvenanceStore _provenance;
    private readonly PluginLog _log;

    /// <summary>Initializes a new instance of the <see cref="SubtitleJob"/> class.</summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="providerManager">Used to refresh an item so Jellyfin sees the new sidecar.</param>
    /// <param name="directoryService">Needed to build <see cref="MetadataRefreshOptions"/>.</param>
    /// <param name="finder">The shared search pipeline.</param>
    /// <param name="provenance">Ledger of subtitles this plugin wrote.</param>
    /// <param name="logger">Logger.</param>
    public SubtitleJob(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IDirectoryService directoryService,
        ISubtitleFinder finder,
        ProvenanceStore provenance,
        ILogger<SubtitleJob> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _directoryService = directoryService;
        _finder = finder;
        _provenance = provenance;
        _log = new PluginLog(logger);
    }

    /// <summary>Runs one pass.</summary>
    /// <param name="repick">When true, only look at movies that already have a plugin subtitle and try to improve it.</param>
    /// <param name="progress">Progress sink (0-100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task RunAsync(bool repick, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!await Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _log.Warn("上一次运行还没结束，本次跳过。");
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var mode = repick ? "重新择优" : "自动补全";

        try
        {
            var config = Plugin.Current;
            config.Normalize();

            if (!config.Enabled)
            {
                _log.Info("插件已在配置页停用，本次跳过。");
                return;
            }

            var libraries = config.ResolveLibraryIds();
            if (libraries.Count == 0)
            {
                _log.Warn("还没有选择目标影视库（控制台 → 插件 → Thunder Subtitle），本次跳过。");
                return;
            }

            var items = EnumerateItems(libraries);
            if (items.Count == 0)
            {
                _log.Warn($"{mode}：所选影视库里没有查到任何电影条目，请检查「目标影视库」是否选对。");
                return;
            }

            var queue = items.Where(item => ShouldProcess(item, config, repick)).ToList();
            _log.Info($"{mode}开始：影视库 {libraries.Count} 个，条目 {items.Count} 条，待处理 {queue.Count} 条，单次上限 {config.MaxItemsPerRun} 条。");

            int processed = 0, written = 0, kept = 0, empty = 0, failed = 0;

            for (var index = 0; index < queue.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(queue.Count == 0 ? 100 : (100.0 * index) / queue.Count);

                if (processed >= config.MaxItemsPerRun)
                {
                    _log.Info($"已达到单次上限 {config.MaxItemsPerRun} 条，剩余 {queue.Count - index} 条留到下次。");
                    break;
                }

                var item = queue[index];
                ProcessResult result;
                try
                {
                    result = await ProcessAsync(item, config, repick, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    processed++;
                    _log.Error($"{Display(item)}：处理失败 - {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                if (result == ProcessResult.Skipped)
                {
                    continue;
                }

                processed++;
                switch (result)
                {
                    case ProcessResult.Written:
                        written++;
                        break;
                    case ProcessResult.KeptExisting:
                        kept++;
                        break;
                    case ProcessResult.NoCandidates:
                        empty++;
                        break;
                    default:
                        failed++;
                        break;
                }
            }

            progress?.Report(100);
            stopwatch.Stop();
            _log.Info($"{mode}结束：处理 {processed} 条，写入 {written} 条，保留 {kept} 条，无可用字幕 {empty} 条，失败 {failed} 条，耗时 {stopwatch.Elapsed.TotalSeconds:F1} 秒。");
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// 只处理指定的条目（实时入库用）。
    ///
    /// 与 <see cref="RunAsync"/> 的差别：不枚举影视库，只按 Id 取条目，
    /// 因此耗时只跟新片数量有关，与库大小无关。
    /// </summary>
    /// <param name="itemIds">条目 Id（Jellyfin 的影片 Guid）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns><c>true</c> 表示这批条目已处理完（或被判定为无需处理）；<c>false</c> 表示本次没跑（锁被占 / 开关关闭），调用方应稍后重试。</returns>
    public async Task<bool> ProcessItemsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken)
    {
        if (itemIds is null || itemIds.Count == 0)
        {
            return true;
        }

        // 与全量任务共用同一把锁，但**不等待**：全量任务正在跑时，
        // 让实时这批直接跳过，调用方下一轮再试。
        if (!await Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var config = Plugin.Current;
            config.Normalize();

            if (!config.Enabled || !config.RealtimeFill)
            {
                return false;
            }

            var queue = new List<BaseItem>();
            foreach (var id in itemIds)
            {
                var item = _libraryManager.GetItemById(id);
                if (item is null)
                {
                    continue;
                }

                // 已经有好字幕、或者根本读不到文件，直接跳过。
                if (!ShouldProcess(item, config, repick: false))
                {
                    continue;
                }

                queue.Add(item);
            }

            if (queue.Count == 0)
            {
                return true;
            }

            _log.Info($"实时入库：{queue.Count} 部新片，开始补字幕。");

            int written = 0, kept = 0, empty = 0, failed = 0;

            foreach (var item in queue)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var result = await ProcessAsync(item, config, repick: false, cancellationToken).ConfigureAwait(false);
                    switch (result)
                    {
                        case ProcessResult.Written:
                            written++;
                            break;
                        case ProcessResult.KeptExisting:
                            kept++;
                            break;
                        case ProcessResult.NoCandidates:
                            empty++;
                            break;
                        case ProcessResult.Skipped:
                            break;
                        default:
                            failed++;
                            break;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    _log.Error($"{Display(item)}：实时补字幕失败 - {ex.GetType().Name}: {ex.Message}");
                }
            }

            stopwatch.Stop();
            _log.Info($"实时补字幕结束：写入 {written}，保留 {kept}，无可用字幕 {empty}，失败 {failed}，耗时 {stopwatch.Elapsed.TotalSeconds:F1} 秒。");
            return true;
        }
        finally
        {
            Gate.Release();
        }
    }

    private IReadOnlyList<BaseItem> EnumerateItems(IReadOnlyList<Guid> libraries)
    {
        // One query per library: ParentId + Recursive is the only filter that reliably
        // matches .strm shortcuts here (TopParentIds resolves to nothing for them).
        var items = new List<BaseItem>();
        var seen = new HashSet<Guid>();

        foreach (var libraryId in libraries)
        {
            var query = new InternalItemsQuery
            {
                ParentId = libraryId,
                Recursive = true,
                IncludeItemTypes = [BaseItemKind.Movie],

                // Without this a movie that was added to a box set is hidden and looks missing.
                CollapseBoxSetItems = false,
                IsVirtualItem = false,
                EnableTotalRecordCount = false
            };

            foreach (var item in _libraryManager.GetItemList(query))
            {
                if (seen.Add(item.Id))
                {
                    items.Add(item);
                }
            }
        }

        return items;
    }

    private bool ShouldProcess(BaseItem item, PluginConfiguration config, bool repick)
    {
        var mediaPath = item.Path;
        if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath))
        {
            return false;
        }

        var sidecars = Sidecar.FindSubtitles(mediaPath);
        var pluginSubtitles = sidecars.Where(path => Sidecar.IsPluginSubtitle(mediaPath, path)).ToList();
        var userSubtitles = sidecars.Where(path => !Sidecar.IsPluginSubtitle(mediaPath, path)).ToList();

        if (repick)
        {
            return pluginSubtitles.Count > 0;
        }

        // Embedded or synced-from-115 subtitles: the user already has something to read.
        if (userSubtitles.Count > 0)
        {
            return false;
        }

        var hasEmbeddedSubtitle = item.GetMediaStreams()
            .Any(stream => stream.Type == MediaStreamType.Subtitle && !stream.IsExternal);
        if (hasEmbeddedSubtitle)
        {
            return false;
        }

        if (pluginSubtitles.Count > 0)
        {
            return config.AllowOverwritePluginSubtitle;
        }

        return true;
    }

    private async Task<ProcessResult> ProcessAsync(BaseItem item, PluginConfiguration config, bool repick, CancellationToken cancellationToken)
    {
        var mediaPath = item.Path!;
        var code = ReleaseCode.FromPath(mediaPath) ?? ReleaseCode.FromText(item.Name);
        if (code is null)
        {
            _log.Debug($"{Display(item)}：无法从文件名识别番号，跳过。");
            return ProcessResult.Skipped;
        }

        var existing = Sidecar.FindPluginSubtitles(mediaPath).FirstOrDefault();
        if (existing is not null && !config.AllowOverwritePluginSubtitle && !repick)
        {
            _log.Debug($"{Display(item)} [{code}]：已有插件字幕且未允许覆盖，跳过。");
            return ProcessResult.Skipped;
        }

        // One shared pipeline with the manual "search subtitles" dialog, so both agree
        // on what the best candidate is.
        FindOutcome outcome;
        try
        {
            outcome = await _finder.FindAsync(code, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"{Display(item)} [{code}]：查询迅雷字幕失败 - {ex.Message}");
            return ProcessResult.Failed;
        }

        if (outcome.Candidates.Count == 0)
        {
            _log.Info($"{Display(item)} [{code}]：迅雷没有候选字幕。");
            return ProcessResult.NoCandidates;
        }

        _log.Info($"{Display(item)} [{code}]：候选 {outcome.Candidates.Count} 条，可下载 {outcome.Accepted} 条，下载耗时 {outcome.DownloadSeconds:F1} 秒（并发 {config.DownloadConcurrency}）"
                  + (outcome.SkippedDownloads > 0 ? $"，预算 {config.ItemBudgetSeconds} 秒用尽/超时跳过 {outcome.SkippedDownloads} 条" : string.Empty) + "。");

        if (outcome.DroppedLanguages.Count > 0)
        {
            var detail = string.Join(" / ", outcome.DroppedLanguages.Select(pair => $"{Describe(pair.Key)} {pair.Value}"));
            _log.Info($"{Display(item)} [{code}]：丢弃非中文候选 {outcome.DroppedLanguages.Values.Sum()} 条（{detail}）。");
        }

        if (outcome.Verdicts is not null)
        {
            foreach (var verdict in outcome.Verdicts.Values.OrderBy(value => value.Id))
            {
                var judged = outcome.Shortlist.Count >= verdict.Id ? outcome.Shortlist[verdict.Id - 1] : null;
                var flag = verdict.MachineTranslated ? " [机翻]" : string.Empty;
                _log.Info($"  AI #{verdict.Id}「{judged?.CandidateName}」{verdict.Score:F0} 分{flag} {verdict.Reason}");
            }
        }

        if (!outcome.HasResults)
        {
            _log.Warn($"{Display(item)} [{code}]：没有可用的简体/繁体中文候选，本轮不写字幕。");
            return ProcessResult.NoCandidates;
        }

        var preferred = config.ResolvePreferredLanguage();
        var best = outcome.Best!;

        _log.Info($"{Display(item)} [{code}]：最优「{best.CandidateName}」（{Describe(best.Language)}，{best.ContentChars} 字 / {best.CueCount} 条，{best.Bytes} B，{best.Extension}，{best.EncodingName}，AI标记 {best.AiMarkers}）。");

        if (existing is not null)
        {
            var existingBytes = new FileInfo(existing).Length;
            var existingText = TryReadText(existing);
            var existingHash = existingText is null ? null : SubtitleText.Hash(existingText);
            var existingStats = existingText is null
                ? new SubtitleStats(0, Math.Max(0, (int)(existingBytes / 3)))
                : SubtitleContent.Measure(existingText, Path.GetExtension(existing));
            var existingLanguage = existingText is null
                ? SubtitleLanguage.Unknown
                : SubtitleLanguageDetector.Detect(SubtitleContent.StripMarkup(existingText));
            var record = _provenance.Get(ItemKey(item));
            var knownMarkers = record is not null && string.Equals(record.File, existing, StringComparison.Ordinal)
                ? record.AiMarkers
                : (int?)null;

            var onDisk = new ExistingSubtitle(existingStats, existingLanguage, existingHash, knownMarkers);
            if (!SubtitleRanker.IsUpgrade(best, onDisk, preferred))
            {
                _log.Info($"{Display(item)} [{code}]：现有「{Path.GetFileName(existing)}」{Describe(existingLanguage)} {existingStats.ContentChars} 字 / {existingBytes} B 不差于候选，保留。");
                return ProcessResult.KeptExisting;
            }

            _log.Info($"{Display(item)} [{code}]：升级「{Path.GetFileName(existing)}」{Describe(existingLanguage)} {existingStats.ContentChars} 字 → {Describe(best.Language)} {best.ContentChars} 字（{existingBytes} B → {best.Bytes} B）。");
        }

        var target = Sidecar.TargetPath(mediaPath, best.Extension);
        foreach (var stale in Sidecar.RemoveStalePluginSubtitles(mediaPath, target))
        {
            _log.Info($"{Display(item)} [{code}]：删除旧扩展名字幕「{Path.GetFileName(stale)}」。");
        }

        var payload = Utf8NoBom.GetBytes(SubtitleText.Normalize(best.Text) + "\n");
        await Sidecar.WriteAtomicAsync(target, payload, cancellationToken).ConfigureAwait(false);
        _log.Info($"{Display(item)} [{code}]：写入「{Path.GetFileName(target)}」{payload.Length} B（{best.EncodingName} → utf-8）。");

        _provenance.Set(new SubtitleProvenance(
            ItemKey(item),
            code,
            target,
            best.CandidateName,
            payload.Length,
            best.AiMarkers,
            DateTimeOffset.UtcNow));

        await VerifyAsync(item, target, cancellationToken).ConfigureAwait(false);
        return ProcessResult.Written;
    }


    /// <summary>
    /// Refreshes the item so Jellyfin attaches the new sidecar, then reports what it saw.
    /// <c>ValidationOnly</c> re-probes the local files without running the library's own
    /// subtitle downloaders, which is what produces the <c>.chi.0.srt</c> duplicates.
    /// </summary>
    private async Task VerifyAsync(BaseItem item, string target, CancellationToken cancellationToken)
    {
        try
        {
            var options = new MetadataRefreshOptions(_directoryService)
            {
                MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
                ImageRefreshMode = MetadataRefreshMode.None,
                EnableRemoteContentProbe = false,
                IsAutomated = true
            };

            var fresh = _libraryManager.GetItemById(item.Id) ?? item;
            await _providerManager.RefreshSingleItem(fresh, options, cancellationToken).ConfigureAwait(false);

            var subtitleStreams = _libraryManager.GetItemById(item.Id)?.GetMediaStreams()
                .Count(stream => stream.Type == MediaStreamType.Subtitle) ?? 0;

            if (subtitleStreams > 0)
            {
                _log.Info($"{Display(item)}：刷新后识别到 {subtitleStreams} 条字幕流，「{Path.GetFileName(target)}」已生效。");
            }
            else
            {
                _log.Warn($"{Display(item)}：字幕已写入，但刷新后 Jellyfin 仍没识别到字幕流，稍后重试刷新。");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"{Display(item)}：字幕已写入，但刷新条目失败 - {ex.Message}");
        }
    }

    private static string? TryReadText(string path)

    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            return SubtitleText.TryDecode(bytes)?.Text;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ItemKey(BaseItem item) => item.Id.ToString("N");
    /// <summary>Human readable language name for the log.</summary>
    private static string Describe(SubtitleLanguage language) => language switch
    {
        SubtitleLanguage.SimplifiedChinese => "简体",
        SubtitleLanguage.TraditionalChinese => "繁体",
        SubtitleLanguage.Japanese => "日文",
        SubtitleLanguage.Korean => "韩文",
        SubtitleLanguage.English => "英文",
        SubtitleLanguage.Other => "其它语言",
        _ => "语言未知"
    };

    private static string Display(BaseItem item)
    {
        var name = Path.GetFileName(item.Path);
        return string.IsNullOrEmpty(name) ? item.Name : name;
    }
}