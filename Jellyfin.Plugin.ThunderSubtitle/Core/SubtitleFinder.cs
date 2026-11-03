using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>Everything one search produced, so callers can log or display it.</summary>
/// <param name="Candidates">Raw candidates reported by the API.</param>
/// <param name="Accepted">How many were eligible for download.</param>
/// <param name="Ranked">Chinese candidates, best first. Already re-ordered by the AI when enabled.</param>
/// <param name="Verdicts">AI verdicts keyed by 1-based shortlist id, when a judgement ran.</param>
/// <param name="DroppedLanguages">Candidates dropped because they were not Chinese.</param>
/// <param name="SkippedDownloads">Candidates abandoned because the time budget ran out.</param>
/// <param name="DownloadSeconds">Wall clock spent downloading.</param>
public sealed record FindOutcome(
    IReadOnlyList<ThunderCandidate> Candidates,
    int Accepted,
    IReadOnlyList<SubtitlePick> Ranked,
    IReadOnlyDictionary<int, AiVerdict>? Verdicts,
    IReadOnlyDictionary<SubtitleLanguage, int> DroppedLanguages,
    int SkippedDownloads,
    double DownloadSeconds)
{
    /// <summary>Gets the candidate to use, or <c>null</c> when nothing usable was found.</summary>
    public SubtitlePick? Best => Ranked.Count > 0 ? Ranked[0] : null;

    /// <summary>Gets a value indicating whether any candidate survived.</summary>
    public bool HasResults => Ranked.Count > 0;

    /// <summary>Gets the shortlist that was sent to the AI, in ranking order.</summary>
    public IReadOnlyList<SubtitlePick> Shortlist { get; init; } = Array.Empty<SubtitlePick>();

    /// <summary>
    /// Gets the AI verdict for one candidate. Verdict ids index <see cref="Shortlist"/> in the
    /// order it was sent, while <see cref="Ranked"/> is re-ordered by those very verdicts, so a
    /// positional lookup would attach the wrong score to every row the AI moved.
    /// </summary>
    /// <param name="pick">The candidate to look up, matched by instance.</param>
    /// <returns>The verdict, or <c>null</c> when the candidate was not judged.</returns>
    public AiVerdict? VerdictOf(SubtitlePick pick)
    {
        if (Verdicts is null)
        {
            return null;
        }

        for (var index = 0; index < Shortlist.Count; index++)
        {
            if (ReferenceEquals(Shortlist[index], pick))
            {
                return Verdicts.TryGetValue(index + 1, out var verdict) ? verdict : null;
            }
        }

        return null;
    }
}

/// <summary>Shared "search, download, judge" pipeline.</summary>
public interface ISubtitleFinder
{
    /// <summary>Searches and ranks the candidates for a release code.</summary>
    /// <param name="code">The code to search, e.g. <c>ABF-363</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome, even when nothing usable was found.</returns>
    Task<FindOutcome> FindAsync(string code, CancellationToken cancellationToken);
}

/// <summary>
/// The pipeline both entry points share: the scheduled tasks and the per-item
/// "search subtitles" dialog. Keeping it in one place means the dialog shows exactly
/// what the automatic run would pick.
/// </summary>
public sealed class SubtitleFinder : ISubtitleFinder
{
    private readonly ThunderClient _thunder;
    private readonly AiSubtitleJudge _ai;
    private readonly PluginLog _log;
    private DateTimeOffset _lastSearchAt = DateTimeOffset.MinValue;

    /// <summary>Initializes a new instance of the <see cref="SubtitleFinder"/> class.</summary>
    /// <param name="thunder">The Xunlei API client.</param>
    /// <param name="ai">The optional chat-model judge.</param>
    /// <param name="logger">Logger.</param>
    public SubtitleFinder(ThunderClient thunder, AiSubtitleJudge ai, Microsoft.Extensions.Logging.ILogger<SubtitleFinder> logger)
    {
        _thunder = thunder;
        _ai = ai;
        _log = new PluginLog(logger);
    }

    /// <inheritdoc />
    public async Task<FindOutcome> FindAsync(string code, CancellationToken cancellationToken)
    {
        var config = Plugin.Current;
        config.Normalize();
        var preferred = config.ResolvePreferredLanguage();

        var candidates = await SearchCandidatesAsync(code, config, cancellationToken).ConfigureAwait(false);

        var accepted = candidates
            .Where(candidate => SubtitleText.AcceptedExtensions.Contains(
                "." + SubtitleText.NormalizeExtension(candidate.Extension),
                StringComparer.OrdinalIgnoreCase))
            .Take(config.MaxCandidates)
            .ToList();

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

                var pick = new SubtitlePick(candidate.Name, extension, decoded.Text, decoded.EncodingName, candidate.Uri);
                picks[index] = pick;
                _log.Debug($"  + {pick.CandidateName}（{pick.Language}，{pick.ContentChars} 字，{pick.Bytes} B，{pick.Extension}，{pick.EncodingName}，AI标记 {pick.AiMarkers}）");
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

        started.Stop();

        var downloaded = picks.Where(pick => pick is not null).Select(pick => pick!).ToList();
        var chinese = SubtitleRanker.KeepChineseOnly(downloaded);
        var ranked = SubtitleRanker.Rank(chinese.Kept, code, preferred).ToList();

        var outcome = new FindOutcome(candidates, accepted.Count, ranked, null, chinese.Dropped, skipped, started.Elapsed.TotalSeconds);

        if (ranked.Count == 0 || !config.IsAiUsable || ranked.Count < 2)
        {
            if (ranked.Count >= 2 && config.AiEnabled && !config.IsAiUsable)
            {
                _log.Warn($"[{code}] AI 评审已开启但缺少 API Key 或模型名，按字符数排序。");
            }

            return outcome with { Shortlist = ranked.Take(1).ToArray() };
        }

        var shortlist = SelectShortlist(ranked, config.AiPerTier);
        if (shortlist.Count > 1)
        {
            _log.Info($"[{code}] AI 评审：候选 {ranked.Count} 份按条数分档，取前 {ShortlistTiers} 档共 {shortlist.Count} 份。");
        }

        var verdicts = await _ai
            .JudgeAsync(code, shortlist, AiSettings.From(config, Guid.NewGuid().ToString("N")), preferred, cancellationToken)
            .ConfigureAwait(false);

        if (verdicts is null)
        {
            return outcome with { Shortlist = shortlist };
        }

        var judged = new HashSet<SubtitlePick>(shortlist, ReferenceEqualityComparer.Instance);
        return outcome with
        {
            Ranked = SubtitleRanker.ApplyAiVerdicts(shortlist, verdicts)
                .Concat(ranked.Where(pick => !judged.Contains(pick)))
                .ToArray(),
            Verdicts = verdicts,
            Shortlist = shortlist
        };
    }

    /// <summary>Hard cap on how many candidates one judgement may compare.</summary>
    internal const int MaxJudgedCandidates = 8;

    /// <summary>
    /// 送 AI 评审时覆盖几个「条数档位」。
    /// 3 档是用户定的：太少容易全落在同一份机翻的副本里，太多则 prompt 变长、费用变高。
    /// </summary>
    internal const int ShortlistTiers = 3;

    /// <summary>
    /// 条数相差不超过「较高者」的这个百分比，就算同一个档位。
    /// 用户举的例子：500 条和 504 条应当是同一档（差 0.8%），
    /// 而 500 / 480（差 4%）应当分成两档。
    /// </summary>
    internal const double TierTolerancePercent = 2.0;

    /// <summary>
    /// Picks what the model compares.
    ///
    /// 规则：
    /// 1. 按「条数」把候选分档，相邻差 ≤ <see cref="TierTolerancePercent"/> 算同一档；
    /// 2. 取条数最多的前 <see cref="ShortlistTiers"/> 档；
    /// 3. 每档再挑 <paramref name="perTier"/> 份（档内按质量排序取前面的）。
    ///
    /// 为什么不再只取「前 N 名」：条数最多的那一批往往是同一份机翻的不同转码，
    /// 而 AI 翻译的条目通常还更多。只按条数取前 N，会把排在后面的人翻彻底挤出评审，
    /// 于是模型只能在几份机翻里挑一份"最不烂的"。
    /// </summary>
    /// <param name="ranked">Chinese candidates, best first.</param>
    /// <param name="perTier">每档取几份。</param>
    /// <returns>按档位从高到低排好的候选，最多 <see cref="MaxJudgedCandidates"/> 份。</returns>
    internal static List<SubtitlePick> SelectShortlist(IReadOnlyList<SubtitlePick> ranked, int perTier)
    {
        if (ranked.Count == 0)
        {
            return [];
        }

        var take = Math.Clamp(perTier, 1, MaxJudgedCandidates);

        // 质量顺序：Rank 已经把「名字带番号 / 机翻标记 / 语言 / 字数 / 条数」都算进去了，
        // 档内挑代表时优先取它排在前面的那几个。
        var qualityIndex = new Dictionary<SubtitlePick, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < ranked.Count; i++)
        {
            qualityIndex[ranked[i]] = i;
        }

        // 分档只看条数 —— 这是「一份字幕完整不完整」最直观、也最不容易被文件名骗到的指标。
        var tiers = new List<List<SubtitlePick>>();
        var baseline = 0;

        foreach (var pick in ranked.OrderByDescending(pick => pick.CueCount))
        {
            if (tiers.Count == 0 || !IsSameTier(baseline, pick.CueCount))
            {
                tiers.Add([]);
                baseline = pick.CueCount;
            }

            tiers[^1].Add(pick);
        }

        var shortlist = new List<SubtitlePick>();

        foreach (var tier in tiers.Take(ShortlistTiers))
        {
            shortlist.AddRange(tier
                .OrderBy(pick => qualityIndex.TryGetValue(pick, out var index) ? index : int.MaxValue)
                .Take(take));
        }

        return shortlist.Take(MaxJudgedCandidates).ToList();
    }

    /// <summary>
    /// 判断某个条数是否还算在 <paramref name="baseline"/> 开启的那一档里。
    /// 条数为 0 的（解析失败的空壳）不参与模糊匹配，各自单独成档。
    /// </summary>
    /// <param name="baseline">该档的基准条数（档内最大值）。</param>
    /// <param name="cues">待判断的条数。</param>
    /// <returns>同一档返回 true。</returns>
    private static bool IsSameTier(int baseline, int cues)
    {
        if (baseline <= 0)
        {
            return cues == baseline;
        }

        return Math.Abs(baseline - cues) <= baseline * TierTolerancePercent / 100.0;
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

        var merged = candidates
            .Concat(secondary)
            .GroupBy(candidate => candidate.Uri.AbsoluteUri, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

        WarnIfEmpty(code, merged);
        return merged;
    }

    private async Task<IReadOnlyList<ThunderCandidate>> SearchOnceLoggedAsync(
        string code,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        await ThrottleSearchAsync(config, cancellationToken).ConfigureAwait(false);
        var search = await _thunder.SearchAsync(code, cancellationToken).ConfigureAwait(false);

        if (search.Attempts > 1)
        {
            _log.Info($"[{code}] 上游前 {search.Attempts - 1} 次搜索返回空数据，第 {search.Attempts} 次拿到 {search.Candidates.Count} 条候选。");
        }

        return search.Candidates;
    }

    private void WarnIfEmpty(string code, IReadOnlyList<ThunderCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            _log.Warn($"[{code}] 上游接口连续 {ThunderClient.SearchAttempts} 次搜索都返回空数据，本次没有任何候选。稍后重试通常就恢复。");
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
