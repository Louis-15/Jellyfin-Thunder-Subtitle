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
    private readonly ThunderClient _thunder;
    private readonly ProvenanceStore _provenance;
    private readonly AiSubtitleJudge _ai;
    private readonly PluginLog _log;

    private DateTimeOffset _lastSearchAt = DateTimeOffset.MinValue;

    /// <summary>Initializes a new instance of the <see cref="SubtitleJob"/> class.</summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="providerManager">Used to refresh an item so Jellyfin sees the new sidecar.</param>
    /// <param name="directoryService">Needed to build <see cref="MetadataRefreshOptions"/>.</param>
    /// <param name="thunder">The Xunlei API client.</param>
    /// <param name="provenance">Ledger of subtitles this plugin wrote.</param>
    /// <param name="ai">The optional chat-model judge.</param>
    /// <param name="logger">Logger.</param>
    public SubtitleJob(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IDirectoryService directoryService,
        ThunderClient thunder,
        ProvenanceStore provenance,
        AiSubtitleJudge ai,
        ILogger<SubtitleJob> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _directoryService = directoryService;
        _thunder = thunder;
        _provenance = provenance;
        _ai = ai;
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
        var code = JavCode.FromPath(mediaPath) ?? JavCode.FromText(item.Name);
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

        // SearchCandidatesAsync throttles before every request it makes.
        IReadOnlyList<ThunderCandidate> candidates;
        try
        {
            candidates = await SearchCandidatesAsync(code, config, Display(item), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is ThunderException or HttpRequestException or OperationCanceledException)
        {
            _log.Warn($"{Display(item)} [{code}]：查询迅雷字幕失败 - {ex.Message}");
            return ProcessResult.Failed;
        }

        if (candidates.Count == 0)
        {
            _log.Info($"{Display(item)} [{code}]：迅雷没有候选字幕。");
            return ProcessResult.NoCandidates;
        }

        var accepted = candidates
            .Where(candidate => SubtitleText.AcceptedExtensions.Contains(
                "." + SubtitleText.NormalizeExtension(candidate.Extension),
                StringComparer.OrdinalIgnoreCase))
            .Take(config.MaxCandidates)
            .ToList();

        _log.Info($"{Display(item)} [{code}]：候选 {candidates.Count} 条，可下载 {accepted.Count} 条（已丢弃不支持或超出上限的 {candidates.Count - accepted.Count} 条）。");

        // Download in parallel with a hard budget for the whole item. Sequentially, one
        // slow CDN edge per candidate could stall a run for tens of minutes.
        var picks = new SubtitlePick?[accepted.Count];
        var skipped = 0;
        var started = Stopwatch.StartNew();

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(config.ItemBudgetSeconds));
        using var gate = new SemaphoreSlim(Math.Clamp(config.DownloadConcurrency, 1, 8));

        await Task.WhenAll(accepted.Select(async (candidate, index) =>
        {
            try
            {
                await gate.WaitAsync(budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref skipped);
                return;
            }

            try
            {
                var bytes = await _thunder.DownloadAsync(candidate.Uri, budget.Token).ConfigureAwait(false);
                var decoded = SubtitleText.TryDecode(bytes);
                if (decoded is null)
                {
                    _log.Debug($"  - 丢弃（无法解码）：{candidate.Name}");
                    return;
                }

                var extension = SubtitleText.DetectExtension(decoded.Text, candidate.Extension);
                if (extension is null)
                {
                    _log.Debug($"  - 丢弃（内容不是字幕）：{candidate.Name}");
                    return;
                }

                var pick = new SubtitlePick(candidate.Name, extension, decoded.Text, decoded.EncodingName);
                picks[index] = pick;
                _log.Debug($"  + {pick.CandidateName}（{Describe(pick.Language)}，{pick.ContentChars} 字，{pick.Bytes} B，{pick.Extension}，{pick.EncodingName}，AI标记 {pick.AiMarkers}）");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref skipped);
            }
            catch (Exception ex)
            {
                _log.Debug($"  - 下载失败：{candidate.Name} - {ex.Message}");
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        var downloaded = picks.Where(pick => pick is not null).Select(pick => pick!).ToList();
        _log.Info($"{Display(item)} [{code}]：下载 {accepted.Count} 条候选耗时 {started.Elapsed.TotalSeconds:F1} 秒（并发 {config.DownloadConcurrency}）"
                  + (skipped > 0 ? $"，预算 {config.ItemBudgetSeconds} 秒用尽/超时跳过 {skipped} 条" : string.Empty) + "。");

        if (downloaded.Count == 0)
        {
            _log.Warn($"{Display(item)} [{code}]：候选全部不可用。");
            return ProcessResult.NoCandidates;
        }

        // Only Simplified and Traditional Chinese may enter the comparison. English and
        // Japanese candidates are dropped here, before any size or score is looked at.
        var preferred = config.ResolvePreferredLanguage();
        var chinese = SubtitleRanker.KeepChineseOnly(downloaded);
        if (chinese.Dropped.Count > 0)
        {
            var detail = string.Join(" / ", chinese.Dropped.Select(pair => $"{Describe(pair.Key)} {pair.Value}"));
            _log.Info($"{Display(item)} [{code}]：丢弃非中文候选 {chinese.Dropped.Values.Sum()} 条（{detail}）。");
        }

        if (chinese.Kept.Count == 0)
        {
            _log.Warn($"{Display(item)} [{code}]：下载到的 {downloaded.Count} 条候选都不是简体/繁体中文，本轮不写字幕。");
            return ProcessResult.NoCandidates;
        }

        var ranked = SubtitleRanker.Rank(chinese.Kept, code, preferred);
        if (ranked.Count == 0)
        {
            _log.Warn($"{Display(item)} [{code}]：{chinese.Kept.Count} 条中文候选全部属于其它番号，已全部丢弃。");
            return ProcessResult.NoCandidates;
        }

        var best = await PickBestAsync(item, code, ranked, config, preferred, cancellationToken).ConfigureAwait(false);

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
    /// Chooses the candidate to write. The deterministic ranking decides first; when AI
    /// review is enabled the largest finalists are then compared as text, because a
    /// machine translation is frequently the <em>longest</em> file — every filler word and
    /// every grunt gets translated, and the result is verbose and wrong.
    /// </summary>
    private async Task<SubtitlePick> PickBestAsync(
        BaseItem item,
        string code,
        IReadOnlyList<SubtitlePick> ranked,
        PluginConfiguration config,
        SubtitleLanguage preferred,
        CancellationToken cancellationToken)
    {
        var best = ranked[0];
        if (!config.IsAiUsable || ranked.Count < 2)
        {
            if (config.AiEnabled && !config.IsAiUsable)
            {
                _log.Warn($"{Display(item)} [{code}]：AI 评审已开启但缺少 Base URL 或模型名，按字符数排序。");
            }

            return best;
        }

        var shortlist = ranked.Take(Math.Min(config.AiTopCandidates, ranked.Count)).ToList();
        _log.Info($"{Display(item)} [{code}]：中文候选 {ranked.Count} 条，字符数前 {shortlist.Count} 名："
                  + string.Join(" / ", shortlist.Select((pick, index) => $"#{index + 1} {pick.CandidateName}（{Describe(pick.Language)} {pick.ContentChars} 字）")));

        var verdicts = await _ai
            .JudgeAsync(code, shortlist, AiSettings.From(config), preferred, cancellationToken)
            .ConfigureAwait(false);

        if (verdicts is null)
        {
            _log.Info($"{Display(item)} [{code}]：AI 评审未返回结果，按字符数排序选「{best.CandidateName}」。");
            return best;
        }

        foreach (var verdict in verdicts.Values.OrderBy(value => value.Id))
        {
            var pick = shortlist[verdict.Id - 1];
            var flag = verdict.MachineTranslated ? " [机翻]" : string.Empty;
            _log.Info($"  AI #{verdict.Id}「{pick.CandidateName}」{verdict.Score:F0} 分{flag} {verdict.Reason}");
        }

        var reordered = SubtitleRanker.ApplyAiVerdicts(shortlist, verdicts);
        var winner = reordered[0];
        if (!ReferenceEquals(winner, best))
        {
            _log.Info($"{Display(item)} [{code}]：AI 改选「{winner.CandidateName}」（原字符数第一为「{best.CandidateName}」）。");
        }

        return winner;
    }

    /// <summary>
    /// Searches the API and, when no candidate carries the code, retries with the other
    /// spelling of the number. The index treats <c>NIMA-086</c> and <c>NIMA-86</c> as
    /// different searches: one returns the release, the other returns unrelated movies,
    /// and which one works differs per studio.
    /// </summary>
    private async Task<IReadOnlyList<ThunderCandidate>> SearchCandidatesAsync(
        string code,
        PluginConfiguration config,
        string display,
        CancellationToken cancellationToken)
    {
        await ThrottleSearchAsync(config, cancellationToken).ConfigureAwait(false);
        var candidates = await _thunder.SearchAsync(code, cancellationToken).ConfigureAwait(false);

        if (candidates.Any(candidate => SubtitleRanker.HasCode(candidate.Name, code)))
        {
            return candidates;
        }

        var alternative = JavCode.AlternativeSpelling(code);
        if (alternative is null)
        {
            return candidates;
        }

        _log.Debug($"{display} [{code}]：{candidates.Count} 条候选里没有番号匹配的，改用「{alternative}」再查一次。");
        await ThrottleSearchAsync(config, cancellationToken).ConfigureAwait(false);
        var secondary = await _thunder.SearchAsync(alternative, cancellationToken).ConfigureAwait(false);
        _log.Info($"{display} [{code}]：改用「{alternative}」补查，得到 {secondary.Count} 条候选。");

        return candidates
            .Concat(secondary)
            .GroupBy(candidate => candidate.Uri.AbsoluteUri, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    /// <summary>
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

    private async Task ThrottleSearchAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        if (config.RequestIntervalMs <= 0 || _lastSearchAt == DateTimeOffset.MinValue)
        {
            _lastSearchAt = DateTimeOffset.UtcNow;
            return;
        }

        var wait = _lastSearchAt.AddMilliseconds(config.RequestIntervalMs) - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }

        _lastSearchAt = DateTimeOffset.UtcNow;
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
