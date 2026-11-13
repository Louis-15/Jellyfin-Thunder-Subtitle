using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThunderSubtitle.Core;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.ThunderSubtitle.Providers;

/// <summary>
/// 手动搜索这一段：把 Jellyfin 的搜索请求翻译成一次管线调用，再把结果翻译回
/// <see cref="RemoteSubtitleInfo"/>。
///
/// 与「下载选中的那一份」分开：那条路径处理的是 id 解码、地址校验与写盘，
/// 两条路径唯一的交集只有 <see cref="SubtitleId"/>。
/// </summary>
public sealed partial class ThunderSubtitleProvider
{
    /// <inheritdoc />
    public async Task<IEnumerable<RemoteSubtitleInfo>> Search(
        SubtitleSearchRequest request,
        CancellationToken cancellationToken)
    {
        _log.Info(
            $"手动搜索请求：Name=「{request.Name}」，MediaPath=「{request.MediaPath}」，"
            + $"Language={Show(request.Language)}，TwoLetter={Show(request.TwoLetterISOLanguageName)}，"
            + $"ContentType={request.ContentType}。");

        if (!IsChineseRequested(request))
        {
            _log.Info($"跳过：请求的语言不是中文（Language={Show(request.Language)}，TwoLetter={Show(request.TwoLetterISOLanguageName)}）。");
            return Array.Empty<RemoteSubtitleInfo>();
        }

        // 用哪个模式取决于条目所在影视库的配置：番号库按番号查，普通影视库按片名查。
        var mode = _libraryContext.ModeForPath(request.MediaPath);
        var target = _libraryContext.FromRequest(request, mode);
        if (target is null)
        {
            _log.Info($"手动搜索：无法从「{request.Name ?? request.MediaPath}」识别"
                      + (mode == SubtitleSearchMode.Code ? "番号" : "片名") + "。");
            return Array.Empty<RemoteSubtitleInfo>();
        }

        // 影片类型决定语言档位和 AI 评审侧重：番号库是 AV，其余看刮削的语言/地区。
        var kind = _libraryContext.MediaKindForPath(request.MediaPath);

        _log.Info($"手动搜索：影视库检索方式 {(mode == SubtitleSearchMode.Code ? "番号" : "片名")}，"
                  + $"影片类型 {MediaKindResolver.DisplayName(kind)}，查询「{target.Label}」。");

        FindOutcome outcome;
        try
        {
            // 附加提示词按「条目 Id」暂存：前端从详情页 URL 拿到的是带连字符的 Guid，
            // 这里从路径反查条目再归一，两边最终落到同一个 key。
            // 对不上（条目还没入库 / 用户没填）就取到 null，本次按默认尺度评审。
            var hint = _hints.Take(_libraryContext.ItemIdForPath(request.MediaPath) ?? Guid.Empty);
            if (hint is not null)
            {
                _log.Info($"手动搜索 [{target.Label}]：本次使用附加提示词「{hint}」。");
            }

            outcome = await _finder
                .FindAsync(target, kind, _libraryContext.BriefForPath(request.MediaPath), hint, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"手动搜索 [{target.Label}] 失败：{ex.Message}");
            return Array.Empty<RemoteSubtitleInfo>();
        }

        _log.Info($"手动搜索 [{target.Label}]：候选 {outcome.Candidates.Count} 条，中文 {outcome.Ranked.Count} 条。");

        if (outcome.Ranked.Count == 0)
        {
            return [Notice(target.Label, outcome)];
        }

        return outcome.Ranked
            .Where(pick => pick.Uri is not null)
            .Select((pick, index) => ToInfo(pick, index + 1, outcome, request.MediaPath))
            .ToArray();
    }

    /// <summary>
    /// A row that explains why the list is otherwise empty. Jellyfin renders an empty result
    /// as "no subtitles found", which hides the difference between "the API was empty" and
    /// "the release genuinely has none", and the user cannot tell it should be retried.
    /// </summary>
    /// <param name="label">The code or title that was searched.</param>
    /// <param name="outcome">What the pipeline produced.</param>
    /// <returns>The notice row. Its id cannot be downloaded.</returns>
    private static RemoteSubtitleInfo Notice(string label, FindOutcome outcome)
    {
        var reason = outcome.Candidates.Count == 0
            ? $"上游接口连续 {ThunderClient.SearchAttempts} 次都返回空数据"
            : $"上游 {outcome.Candidates.Count} 条候选里没有可用的中文字幕";

        var detail = outcome.Candidates.Count == 0
            ? "这是上游临时故障，过一会儿重试即可"
            : string.Join('，', outcome.DroppedLanguages.Select(pair => $"{Show(pair.Key)} {pair.Value} 条")) + "，本次已按语言过滤丢弃";

        return new RemoteSubtitleInfo
        {
            Id = SubtitleId.EncodePlaceholder(reason),
            ProviderName = DisplayName,
            Name = $"（{label}）{reason}",
            Format = "srt",
            Comment = detail,
            ThreeLetterISOLanguageName = SavedLanguage,
            IsHashMatch = false,
            Forced = false,
            HearingImpaired = false
        };
    }

    private static RemoteSubtitleInfo ToInfo(
        SubtitlePick pick,
        int rank,
        FindOutcome outcome,
        string? mediaPath)
    {
        var language = SubtitleLanguageDetector.DisplayName(pick.Language);
        var size = pick.Bytes >= 1024
            ? string.Create(CultureInfo.InvariantCulture, $"{pick.Bytes / 1024.0:F1} KB")
            : $"{pick.Bytes} B";
        var comment = $"{language} · {pick.ContentChars} 字 / {pick.CueCount} 条 · {size}";

        float? rating = null;
        bool? machine = pick.AiMarkers > 0 ? true : null;

        var verdict = outcome.VerdictOf(pick);
        if (verdict is not null)
        {
            // On its own line: Jellyfin renders the comment with white-space: pre-line, and mixing
            // a long reason into the measurements made the row unreadable.
            comment += "\n" + string.Create(CultureInfo.InvariantCulture, $"AI {verdict.Score:F0} 分");
            if (!string.IsNullOrWhiteSpace(verdict.Reason))
            {
                comment += $"（{verdict.Reason}）";
            }

            rating = (float)Math.Round(verdict.Score / 20.0, 2);
            machine = verdict.MachineTranslated;
        }

        return new RemoteSubtitleInfo
        {
            Id = SubtitleId.Encode(pick.Uri!, pick.Extension, pick.CandidateName, mediaPath),
            ProviderName = DisplayName,
            Name = $"{rank}. {pick.CandidateName}",
            Format = pick.Extension.TrimStart('.'),
            Comment = comment,
            ThreeLetterISOLanguageName = SavedLanguage,
            CommunityRating = rating,
            MachineTranslated = machine,
            IsHashMatch = false,
            Forced = false,
            HearingImpaired = false
        };
    }
}
