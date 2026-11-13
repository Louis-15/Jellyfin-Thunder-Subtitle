using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>
/// 单条影片的处理：搜索 → 评审 → 与磁盘上已有字幕比较 → 写盘。
///
/// 与「跑哪些影片」的调度（<c>SubtitleJob.cs</c>）分开：
/// 这里只处理一条，所有分支都以「这条不写了」作为正常出口，
/// 调度那边只关心 <see cref="ProcessResult"/> 的统计。
/// </summary>
public sealed partial class SubtitleJob
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private async Task<ProcessResult> ProcessAsync(
        BaseItem item,
        SubtitleSearchMode mode,
        MediaKind kind,
        PluginConfiguration config,
        bool repick,
        CancellationToken cancellationToken)
    {
        var mediaPath = item.Path!;
        var searchTarget = _libraryContext.FromItem(item, mode);
        if (searchTarget is null)
        {
            _log.Debug($"{Display(item)}：{(mode == SubtitleSearchMode.Code ? "无法从文件名识别番号" : "取不到可用的片名")}，跳过。");
            return ProcessResult.Skipped;
        }

        _log.Debug($"{Display(item)}：影片类型 {MediaKindResolver.DisplayName(kind)}，按{LanguagePolicy.TierName(kind, 0)}优先。");

        // 番号模式下 label 就是番号，片名模式下是「片名 + 年份 / SxxExx」；
        // 日志和 provenance 都用它，读日志时一眼能看出这条是按哪种方式查的。
        var code = searchTarget.Label;

        var existing = Sidecar.FindPluginSubtitles(mediaPath).FirstOrDefault();
        if (existing is not null && !repick)
        {
            // ShouldProcess 已经挡掉这种情况，这里只是双保险：
            // 补全任务永远不覆盖已有插件字幕，升级走「自动择优」。
            _log.Debug($"{Display(item)} [{code}]：已有插件字幕，补全任务不覆盖（升级请用自动择优）。");
            return ProcessResult.Skipped;
        }

        // One shared pipeline with the manual "search subtitles" dialog, so both agree
        // on what the best candidate is.
        FindOutcome outcome;
        try
        {
            outcome = await _finder
                .FindAsync(searchTarget, kind, _libraryContext.BriefForItem(item), null, cancellationToken)
                .ConfigureAwait(false);
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

        LogVerdicts(item, code, outcome);

        if (!outcome.HasResults)
        {
            _log.Warn($"{Display(item)} [{code}]：没有可用的中文字幕候选，本轮不写字幕。");
            return ProcessResult.NoCandidates;
        }

        var best = outcome.Best!;

        _log.Info($"{Display(item)} [{code}]：最优「{best.CandidateName}」（{Describe(best.Language)}，{best.ContentChars} 字 / {best.CueCount} 条，{best.Bytes} B，{best.Extension}，{best.EncodingName}，AI标记 {best.AiMarkers}）。");

        var bestVerdict = outcome.VerdictOf(best);

        if (kind == MediaKind.Chinese && bestVerdict is not null && bestVerdict.Typos > 0)
        {
            // 只告警不参与排序：用户明确说过中文影视的错别字是「排序参考」，不是淘汰条件。
            var rate = best.ContentChars <= 0 ? 0 : bestVerdict.Typos * 1000.0 / best.ContentChars;
            if (rate >= config.AiTypoWarnRate)
            {
                _log.Warn($"{Display(item)} [{code}]：这份字幕错别字偏多（抽样 {bestVerdict.Typos} 处，约 {rate:F1} 处/千字，阈值 {config.AiTypoWarnRate:F1}）。");
            }
        }

        if (existing is not null && !IsWorthReplacing(item, code, existing, best, bestVerdict, kind))
        {
            return ProcessResult.KeptExisting;
        }

        await WriteAsync(item, mediaPath, code, kind, best, bestVerdict, cancellationToken).ConfigureAwait(false);
        return ProcessResult.Written;
    }

    /// <summary>
    /// 把磁盘上已有的字幕和候选比一比，返回「要不要替换」。
    ///
    /// 读盘可能失败（权限、编码、文件被占用），这时按「没有旧字幕」处理：
    /// 一份读不出来的字幕对用户等于不存在，保留它没有任何意义。
    /// </summary>
    private bool IsWorthReplacing(
        BaseItem item,
        string code,
        string existing,
        SubtitlePick best,
        AiVerdict? bestVerdict,
        MediaKind kind)
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
        var matchesRecord = record is not null && string.Equals(record.File, existing, StringComparison.Ordinal);

        // 只有台账记的就是这个文件时，它的历史评分才可以拿来比较。
        // 文件被外部替换过还沿用旧分数，会让「自动择优」拿错参照物。
        var onDisk = new ExistingSubtitle(
            existingStats,
            existingLanguage,
            existingHash,
            matchesRecord ? record!.AiMarkers : null,
            matchesRecord ? record!.AiScore : null,
            matchesRecord ? record!.AiSource : string.Empty,
            matchesRecord ? record!.TypoCount : 0);

        if (!SubtitleRanker.IsUpgrade(best, bestVerdict, onDisk, kind))
        {
            _log.Info($"{Display(item)} [{code}]：现有「{Path.GetFileName(existing)}」{Describe(existingLanguage)} {existingStats.ContentChars} 字 / {existingBytes} B 不差于候选，保留。");
            return false;
        }

        _log.Info($"{Display(item)} [{code}]：升级「{Path.GetFileName(existing)}」{Describe(existingLanguage)} {existingStats.ContentChars} 字 → {Describe(best.Language)} {best.ContentChars} 字（{existingBytes} B → {best.Bytes} B）。");
        return true;
    }

    private async Task WriteAsync(
        BaseItem item,
        string mediaPath,
        string code,
        MediaKind kind,
        SubtitlePick best,
        AiVerdict? bestVerdict,
        CancellationToken cancellationToken)
    {
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
            DateTimeOffset.UtcNow,
            kind.ToString(),
            best.Language.ToString(),
            bestVerdict?.Score,
            bestVerdict?.EffectiveSource ?? string.Empty,
            bestVerdict?.Typos ?? 0,
            AiPrompts.Version));

        await VerifyAsync(item, target, cancellationToken).ConfigureAwait(false);
    }
}
