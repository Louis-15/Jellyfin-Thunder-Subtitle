using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
public sealed partial class SubtitleJob
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IDirectoryService _directoryService;
    private readonly ISubtitleFinder _finder;
    private readonly ILibraryContext _libraryContext;
    private readonly ProvenanceStore _provenance;
    private readonly PluginLog _log;

    /// <summary>Initializes a new instance of the <see cref="SubtitleJob"/> class.</summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="providerManager">Used to refresh an item so Jellyfin sees the new sidecar.</param>
    /// <param name="directoryService">Needed to build <see cref="MetadataRefreshOptions"/>.</param>
    /// <param name="finder">The shared search pipeline.</param>
    /// <param name="libraryContext">Resolves per-library search mode and builds the search target.</param>
    /// <param name="provenance">Ledger of subtitles this plugin wrote.</param>
    /// <param name="logger">Logger.</param>
    public SubtitleJob(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IDirectoryService directoryService,
        ISubtitleFinder finder,
        ILibraryContext libraryContext,
        ProvenanceStore provenance,
        ILogger<SubtitleJob> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _directoryService = directoryService;
        _finder = finder;
        _libraryContext = libraryContext;
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
        var mode = repick ? "自动择优" : "自动补全";

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

            // 每个库可以单独关掉「自动补全」和「自动择优」：
            // 大部分影片自带字幕，用户只希望对个别库自动下载。
            var active = libraries
                .Where(id => repick ? config.IsAutoRepickEnabled(id) : config.IsAutoFillEnabled(id))
                .ToList();

            if (active.Count == 0)
            {
                _log.Info($"{mode}：所选影视库都关闭了「{(repick ? "自动择优" : "自动补全")}」，本次跳过。");
                return;
            }

            if (active.Count < libraries.Count)
            {
                _log.Info($"{mode}：{libraries.Count} 个库中有 {libraries.Count - active.Count} 个关闭了「{(repick ? "自动择优" : "自动补全")}」，本次不处理。");
            }

            var items = EnumerateItems(active);
            if (items.Count == 0)
            {
                _log.Warn($"{mode}：所选影视库里没有查到任何可处理的条目，请检查「目标影视库」是否选对。");
                return;
            }

            var queue = items
                .Where(entry => ShouldProcess(entry.Item, repick, config))
                .Select(entry => (entry.Item, entry.Mode, Kind: _libraryContext.MediaKindForItem(entry.Item)))
                .ToList();
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

                var (item, searchMode, kind) = queue[index];
                ProcessResult result;
                try
                {
                    result = await ProcessAsync(item, searchMode, kind, config, repick, cancellationToken).ConfigureAwait(false);
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

            var queue = new List<(BaseItem Item, SubtitleSearchMode Mode, MediaKind Kind)>();
            foreach (var id in itemIds)
            {
                var item = _libraryManager.GetItemById(id);
                if (item is null)
                {
                    continue;
                }

                // 已经有好字幕、或者根本读不到文件，直接跳过。
                if (!ShouldProcess(item, repick: false, config))
                {
                    continue;
                }

                // 实时入库同样受「自动补全」开关约束：关掉的库不自动下载。
                var libraryId = _libraryContext.LibraryIdForItem(item);
                if (libraryId is not null && !config.IsAutoFillEnabled(libraryId.Value))
                {
                    continue;
                }

                // 实时入库拿到的只有 Id，检索方式与影片类型都要按它所属影视库现查。
                queue.Add((item, _libraryContext.ModeForItem(item), _libraryContext.MediaKindForItem(item)));
            }

            if (queue.Count == 0)
            {
                return true;
            }

            _log.Info($"实时入库：{queue.Count} 部新片，开始补字幕。");

            int written = 0, kept = 0, empty = 0, failed = 0;

            foreach (var (item, searchMode, kind) in queue)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var result = await ProcessAsync(item, searchMode, kind, config, repick: false, cancellationToken).ConfigureAwait(false);
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

    private IReadOnlyList<(BaseItem Item, SubtitleSearchMode Mode)> EnumerateItems(IReadOnlyList<Guid> libraries)
    {
        // One query per library: ParentId + Recursive is the only filter that reliably
        // matches .strm shortcuts here (TopParentIds resolves to nothing for them).
        //
        // 两种模式取的条目类型不同：
        // - 番号库只处理电影（沿袭原行为，番号本身就是影片级标识）；
        // - 片名库额外处理剧集，因为普通影视库里电视剧/动漫/综艺才是大头，
        //   而剧集可以用「剧名 + SxxExx」查（实测命中率很好）。
        var config = Plugin.Current;
        var items = new List<(BaseItem Item, SubtitleSearchMode Mode)>();
        var seen = new HashSet<Guid>();

        foreach (var libraryId in libraries)
        {
            var mode = config.ResolveSearchMode(libraryId);
            var query = new InternalItemsQuery
            {
                ParentId = libraryId,
                Recursive = true,
                IncludeItemTypes = mode == SubtitleSearchMode.Title
                    ? [BaseItemKind.Movie, BaseItemKind.Episode]
                    : [BaseItemKind.Movie],

                // Without this a movie that was added to a box set is hidden and looks missing.
                CollapseBoxSetItems = false,
                IsVirtualItem = false,
                EnableTotalRecordCount = false
            };

            foreach (var item in _libraryManager.GetItemList(query))
            {
                if (seen.Add(item.Id))
                {
                    items.Add((item, mode));
                }
            }
        }

        return items;
    }

    private bool ShouldProcess(BaseItem item, bool repick, PluginConfiguration config)
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

        // Embedded or cloud-drive synced subtitles: the user already has something to read.
        if (userSubtitles.Count > 0)
        {
            return false;
        }

        // 内嵌字幕默认不动（见 FillDespiteEmbeddedSubtitle 的说明）：
        // 内嵌字幕往往已经能看，再补一份只会让字幕列表变长。
        var hasEmbeddedSubtitle = item.GetMediaStreams()
            .Any(stream => stream.Type == MediaStreamType.Subtitle && !stream.IsExternal);
        if (hasEmbeddedSubtitle && !config.FillDespiteEmbeddedSubtitle)
        {
            return false;
        }

        if (pluginSubtitles.Count > 0)
        {
            // 已有本插件写的字幕：补全不碰它，是否升级由「自动择优」决定。
            return false;
        }

        return true;
    }
}
