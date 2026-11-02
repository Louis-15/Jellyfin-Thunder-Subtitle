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
}

/// <summary>Shared "search, download, judge" pipeline.</summary>
public interface ISubtitleFinder
{
    /// <summary>Searches and ranks the candidates for a JAV code.</summary>
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

        var shortlist = ranked.Take(Math.Min(config.AiTopCandidates, ranked.Count)).ToList();
        var verdicts = await _ai
            .JudgeAsync(code, shortlist, AiSettings.From(config, Guid.NewGuid().ToString("N")), preferred, cancellationToken)
            .ConfigureAwait(false);

        if (verdicts is null)
        {
            return outcome with { Shortlist = shortlist };
        }

        return outcome with
        {
            Ranked = SubtitleRanker.ApplyAiVerdicts(shortlist, verdicts).Concat(ranked.Skip(shortlist.Count)).ToArray(),
            Verdicts = verdicts,
            Shortlist = shortlist
        };
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

        _log.Debug($"[{code}] {candidates.Count} 条候选里没有番号匹配的，改用「{alternative}」再查一次。");
        await ThrottleSearchAsync(config, cancellationToken).ConfigureAwait(false);
        var secondary = await _thunder.SearchAsync(alternative, cancellationToken).ConfigureAwait(false);

        return candidates
            .Concat(secondary)
            .GroupBy(candidate => candidate.Uri.AbsoluteUri, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
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
