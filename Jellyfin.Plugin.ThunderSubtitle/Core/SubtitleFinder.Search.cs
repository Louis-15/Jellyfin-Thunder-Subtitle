using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>
/// 检索阶段：把「要查什么」变成「一批候选」。
///
/// 番号模式和片名模式的补查策略完全不同（前导零变体 / 去掉年份再查一次），
/// 但它们和「下载 → 排序 → 送审」那条主链路没有交集，分开写反而更容易看懂。
/// </summary>
public sealed partial class SubtitleFinder
{
    /// <summary>
    /// Searches the API for the target. 番号模式沿用原来的逻辑：候选里没有一条带这个番号时，
    /// 换一个前导零写法再查一次（索引把 <c>NIMA-086</c> 和 <c>NIMA-86</c> 当成两次不同的搜索）。
    /// 片名模式则先按证据收窄，收窄不到就退一步用纯片名再查。
    /// </summary>
    private async Task<IReadOnlyList<ThunderCandidate>> SearchCandidatesAsync(
        SearchTarget target,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        return target.Mode == SubtitleSearchMode.Title
            ? await SearchByTitleAsync(target, config, cancellationToken).ConfigureAwait(false)
            : await SearchByCodeAsync(target.Code!, config, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ThunderCandidate>> SearchByCodeAsync(
        string code,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var candidates = await SearchOnceLoggedAsync(code, config, cancellationToken).ConfigureAwait(false);

        if (candidates.Any(candidate => SubtitleRanker.HasCode(candidate.Name, code)))
        {
            return candidates;
        }

        var alternative = ReleaseCode.AlternativeSpelling(code);
        if (alternative is null)
        {
            WarnIfEmpty(code, candidates);
            return candidates;
        }

        _log.Debug($"[{code}] {candidates.Count} 条候选里没有番号匹配的，改用「{alternative}」再查一次。");
        var secondary = await SearchOnceLoggedAsync(alternative, config, cancellationToken).ConfigureAwait(false);

        var merged = Merge(candidates, secondary);

        WarnIfEmpty(code, merged);
        return merged;
    }

    /// <summary>
    /// 片名检索两步走：
    /// 1. 先查「片名 + 年份」或「剧名 + SxxExx」；
    /// 2. 如果一条证据（片名 / 季集 / 年份）都对不上，去掉年份用纯片名再查一次，
    ///    因为年份填错（或 Jellyfin 里挂了首映年而不是上映年）时第一次会全军覆没。
    /// 两次都拿不到证据时保留全部候选：中文片名查出来往往是英文命名，
    /// 硬要求片名匹配会让这类库永远选不到字幕。
    /// </summary>
    private async Task<IReadOnlyList<ThunderCandidate>> SearchByTitleAsync(
        SearchTarget target,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var candidates = await SearchOnceLoggedAsync(target.Query, config, cancellationToken).ConfigureAwait(false);
        var narrowed = NarrowByTitle(candidates, target);
        if (narrowed.Count > 0)
        {
            return narrowed;
        }

        // 剧集退化成「只查剧名」也有用：季包候选名字里带 SxxExx，仍能被证据匹配挑出来。
        var fallbackQuery = target.Title!;

        if (!string.Equals(target.Query, fallbackQuery, StringComparison.Ordinal))
        {
            _log.Debug($"[{target.Label}] 候选里没有片名/季集/年份匹配的，改用「{fallbackQuery}」再查一次。");
            var secondary = await SearchOnceLoggedAsync(fallbackQuery, config, cancellationToken).ConfigureAwait(false);
            narrowed = NarrowByTitle(secondary, target);
            if (narrowed.Count > 0)
            {
                return narrowed;
            }

            candidates = Merge(candidates, secondary);
        }

        WarnIfEmpty(target.Label, candidates);

        if (candidates.Count > 0)
        {
            _log.Warn($"[{target.Label}] {candidates.Count} 条候选里没有任何一条能用片名核对（常见于中文片名 + 英文命名候选），按质量择优，请留意是否对片。");
        }

        return candidates;
    }

    /// <summary>
    /// 按证据强度收窄候选。只保留本次最强的那一档；一条都没有匹配时返回空，
    /// 由调用方决定是否换个查询词重试。
    /// </summary>
    private static IReadOnlyList<ThunderCandidate> NarrowByTitle(
        IReadOnlyList<ThunderCandidate> candidates,
        SearchTarget target)
    {
        if (candidates.Count == 0)
        {
            return candidates;
        }

        var scored = candidates
            .Select(candidate => (Candidate: candidate, Level: TitleMatch.Level(candidate.Name, target)))
            .ToList();

        var best = scored.Max(entry => entry.Level);
        if (best == TitleMatchLevel.None)
        {
            return Array.Empty<ThunderCandidate>();
        }

        return scored
            .Where(entry => entry.Level == best)
            .Select(entry => entry.Candidate)
            .ToArray();
    }

    private static IReadOnlyList<ThunderCandidate> Merge(
        IReadOnlyList<ThunderCandidate> first,
        IReadOnlyList<ThunderCandidate> second)
    {
        return first
            .Concat(second)
            .GroupBy(candidate => candidate.Uri.AbsoluteUri, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    private async Task<IReadOnlyList<ThunderCandidate>> SearchOnceLoggedAsync(
        string query,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        await ThrottleSearchAsync(config, cancellationToken).ConfigureAwait(false);
        var search = await _thunder.SearchAsync(query, cancellationToken).ConfigureAwait(false);

        if (search.Attempts > 1)
        {
            _log.Info($"[{query}] 上游前 {search.Attempts - 1} 次搜索返回空数据，第 {search.Attempts} 次拿到 {search.Candidates.Count} 条候选。");
        }

        return search.Candidates;
    }

    private void WarnIfEmpty(string label, IReadOnlyList<ThunderCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            _log.Warn($"[{label}] 上游接口连续 {ThunderClient.SearchAttempts} 次搜索都返回空数据，本次没有任何候选。稍后重试通常就恢复。");
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
}
