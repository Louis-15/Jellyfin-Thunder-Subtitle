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
    /// <summary>Searches and ranks the candidates for one target (code or title).</summary>
    /// <param name="target">What to search for.</param>
    /// <param name="kind">The media kind, which decides the language tier order and the AI prompt.</param>
    /// <param name="brief">影片元数据卡，拼进 AI 提示词；可为空。</param>
    /// <param name="extraInstruction">手动搜索时用户输入的额外要求；定时任务传空。</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome, even when nothing usable was found.</returns>
    Task<FindOutcome> FindAsync(
        SearchTarget target,
        MediaKind kind,
        MediaBrief? brief,
        string? extraInstruction,
        CancellationToken cancellationToken);
}

/// <summary>
/// The pipeline both entry points share: the scheduled tasks and the per-item
/// "search subtitles" dialog. Keeping it in one place means the dialog shows exactly
/// what the automatic run would pick.
/// </summary>
public sealed partial class SubtitleFinder : ISubtitleFinder
{
    private readonly ThunderClient _thunder;
    private readonly AiSubtitleJudge _ai;
    private readonly AiScoreCache _cache;
    private readonly PluginLog _log;
    private DateTimeOffset _lastSearchAt = DateTimeOffset.MinValue;

    /// <summary>Initializes a new instance of the <see cref="SubtitleFinder"/> class.</summary>
    /// <param name="thunder">The Xunlei API client.</param>
    /// <param name="ai">The optional chat-model judge.</param>
    /// <param name="cache">AI 评分缓存，避免自动补全与自动择优重复计费。</param>
    /// <param name="logger">Logger.</param>
    public SubtitleFinder(
        ThunderClient thunder,
        AiSubtitleJudge ai,
        AiScoreCache cache,
        Microsoft.Extensions.Logging.ILogger<SubtitleFinder> logger)
    {
        _thunder = thunder;
        _ai = ai;
        _cache = cache;
        _log = new PluginLog(logger);
    }

    /// <inheritdoc />
    public async Task<FindOutcome> FindAsync(
        SearchTarget target,
        MediaKind kind,
        MediaBrief? brief,
        string? extraInstruction,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Current;
        config.Normalize();
        var label = target.Label;

        var candidates = await SearchCandidatesAsync(target, config, cancellationToken).ConfigureAwait(false);

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
        var acceptable = SubtitleRanker.KeepAcceptable(kind, downloaded);

        // 健康度淘汰：含中文率过低（其实是外挂英文字幕）、满屏未译标注、
        // 中间有大段空白（整段剧情没翻）、断句粗到没法用（多句挤一条）——
        // 这几类毛病在本地就能数出来，不必浪费送审名额和 token。
        // 注意它只能淘汰，不能排名：时间轴整体错位这类错误只有读了台词才发现。
        var healthy = SubtitleRanker.DropUnusable(acceptable.Kept);
        foreach (var entry in healthy.Dropped)
        {
            _log.Info($"[{label}] 健康度淘汰「{entry.Pick.CandidateName}」：{entry.Reason}。");
        }

        var ranked = target.Mode == SubtitleSearchMode.Code
            ? SubtitleRanker.Rank(healthy.Kept, kind, target.Code).ToList()
            : SubtitleRanker.RankForTitle(healthy.Kept, target, kind).ToList();

        // 近似去重：排在排序之后，因为「保留哪一份」由质量顺序决定。
        // 放在短名单之前，因为它的目的就是把被转码版本挤占的送审名额还回来。
        var collapse = NearDuplicate.Collapse(ranked, config.NearDuplicateThreshold);
        if (collapse.Merged > 0)
        {
            _log.Info($"[{label}] 近似去重：{ranked.Count} 份候选合并为 {collapse.Kept.Count} 份"
                      + $"（{collapse.Merged} 份与已有候选相似度 ≥ {config.NearDuplicateThreshold:P0}）。");
            ranked = collapse.Kept;
        }

        var outcome = new FindOutcome(candidates, healthy.Kept.Count, ranked, null, acceptable.Dropped, skipped, started.Elapsed.TotalSeconds);

        if (ranked.Count == 0 || !config.IsAiUsable || ranked.Count < 2)
        {
            if (ranked.Count >= 2 && config.AiEnabled && !config.IsAiUsable)
            {
                _log.Warn($"[{label}] AI 评审已开启但缺少 API Key 或模型名，按字符数排序。");
            }

            return outcome with { Shortlist = ranked.Take(1).ToArray() };
        }

        var shortlist = ShortlistSelector.Select(ranked, kind, config.AiPerTier);
        if (shortlist.Count > 1)
        {
            var detail = string.Join(
                " / ",
                shortlist
                    .GroupBy(pick => LanguagePolicy.TierOf(kind, pick.Language))
                    .OrderBy(group => group.Key)
                    .Select(group => $"Tier{group.Key} {LanguagePolicy.TierName(kind, group.Key)} {group.Count()} 份"));
            _log.Info($"[{label}] AI 评审：候选 {ranked.Count} 份，选出 {shortlist.Count} 份送审（{detail}）。");
        }

        var keys = shortlist
            .Select(pick => AiScoreCache.Key(pick, kind, extraInstruction, config.AiModel))
            .ToArray();

        IReadOnlyDictionary<int, AiVerdict>? verdicts = null;

        // 只有「整批都命中」才用缓存。
        //
        // 部分命中时不能混用：模型的分数是相对同一批候选给出的锚点，
        // 拿缓存的 4 份分数和现场评的 2 份分数一起排序，等于把两把不同的尺子接起来。
        if (config.AiCacheEnabled)
        {
            var cached = keys.Select(key => _cache.Get(key, config.AiCacheDays)).ToArray();
            if (cached.All(verdict => verdict is not null))
            {
                verdicts = cached
                    .Select((verdict, index) => verdict! with { Id = index + 1 })
                    .ToDictionary(verdict => verdict.Id);

                _log.Info($"[{label}] AI 缓存命中 {shortlist.Count} 份（{config.AiCacheDays} 天内评过同一批候选），本次不调用接口。");
            }
        }

        if (verdicts is null)
        {
            verdicts = await _ai
                .JudgeAsync(
                    kind,
                    brief,
                    shortlist,
                    AiSettings.From(config, Guid.NewGuid().ToString("N")),
                    extraInstruction,
                    cancellationToken)
                .ConfigureAwait(false);

            if (verdicts is not null && config.AiCacheEnabled)
            {
                foreach (var verdict in verdicts.Values)
                {
                    if (verdict.Id >= 1 && verdict.Id <= keys.Length)
                    {
                        _cache.Set(keys[verdict.Id - 1], verdict);
                    }
                }
            }
        }

        if (verdicts is null)
        {
            return outcome with { Shortlist = shortlist };
        }

        var judged = new HashSet<SubtitlePick>(shortlist, ReferenceEqualityComparer.Instance);
        return outcome with
        {
            Ranked = SubtitleRanker.ApplyAiVerdicts(
                    shortlist,
                    verdicts,
                    kind,
                    config.AiSimplifiedAcceptScore,
                    config.AiTierOverrideGap)
                .Concat(ranked.Where(pick => !judged.Contains(pick)))
                .ToArray(),
            Verdicts = verdicts,
            Shortlist = shortlist
        };
    }
}
