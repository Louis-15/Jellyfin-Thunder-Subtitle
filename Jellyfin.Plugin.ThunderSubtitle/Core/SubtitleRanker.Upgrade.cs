using System;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>
/// 「自动择优」的升级判定。
///
/// 三类影片对「更好」的定义不同，所以判定链按 <see cref="MediaKind"/> 分叉：
/// 非中文影视看翻译质量、中文影视看错别字、AV 看质量并参考机翻标记。
/// 与排序共用同一套语言档位（<see cref="LanguagePolicy"/>），保证「排第一的」和「值得写入的」是同一个判断标准。
/// </summary>
public static partial class SubtitleRanker
{
    /// <summary>AI 分数至少高出这么多才算「更好」。差 1~2 分是模型的抖动，不是质量差异。</summary>
    private const double UpgradeScoreGap = 5;

    /// <summary>错别字率要降到原来的 70% 以下才值得换一份。</summary>
    private const double TypoDropRatio = 0.7;

    /// <summary>
    /// 判断新候选是否值得覆盖现有字幕。
    /// </summary>
    /// <param name="candidate">The freshly ranked best candidate.</param>
    /// <param name="verdict">AI 对这个候选的评分，没评审过就传 <c>null</c>。</param>
    /// <param name="existing">What is on disk: volume, language, hash, marker count and the ledger's AI score.</param>
    /// <param name="kind">The media kind, which decides the language tier order.</param>
    /// <returns><c>true</c> when the candidate is an upgrade.</returns>
    public static bool IsUpgrade(
        SubtitlePick candidate,
        AiVerdict? verdict,
        ExistingSubtitle existing,
        MediaKind kind)
    {
        if (existing.Hash is not null && string.Equals(existing.Hash, candidate.Hash, StringComparison.Ordinal))
        {
            return false;
        }

        // A file in the wrong language is not a subtitle the user can read: replace it
        // even when it is larger. This is what repairs an English file written by an
        // earlier version.
        if (!SubtitleLanguageDetector.IsChinese(existing.Language))
        {
            return true;
        }

        // 语言档位提升是无条件替换：用户明确要求「有双语就用双语 / 有简体就用简体」，
        // 这比字数多几十个更硬。反过来，档位下降永远不替换，
        // 避免「自动择优」把双语换回单语、把简体换成繁体。
        var candidateTier = LanguagePolicy.TierOf(kind, candidate.Language);
        var existingTier = LanguagePolicy.TierOf(kind, existing.Language);

        if (candidateTier < existingTier)
        {
            return true;
        }

        if (candidateTier > existingTier)
        {
            return false;
        }

        if (kind == MediaKind.Chinese)
        {
            // 中文影视的字幕就是台词本身，没有「翻译质量」可比；
            // 但错别字明显更少时值得换一份。用每千字错别字率而不是绝对数量，
            // 避免「长字幕错字多」被系统性判差。
            if (verdict is not null
                && existing.TypoCount > 0
                && TypoRate(verdict.Typos, candidate.ContentChars)
                <= TypoRate(existing.TypoCount, existing.Stats.ContentChars) * TypoDropRatio)
            {
                return true;
            }
        }
        else if (IsScoreHigher(verdict, existing.AiScore))
        {
            // 分数先于来源：目的是换到翻译质量更高的那份，
            // 而不是换到一个「人工翻译」的标签（通顺的机翻也可以有 80 分）。
            return true;
        }
        else if (IsScoreClose(verdict, existing.AiScore)
                 && SourceRank(verdict!.EffectiveSource) < SourceRank(existing.AiSource))
        {
            // 分数接近时才看来源：ai/asr → human/mixed。
            return true;
        }

        // 机翻标记减少只对日本AV 有意义：那是番号字幕圈里最可靠的质量信号。
        // 非中文影视的候选名里也常带 [MT]，但那里的判断以 AI 分数为准。
        if (kind == MediaKind.JapaneseAv
            && existing.AiMarkers.HasValue
            && candidate.AiMarkers < existing.AiMarkers.Value)
        {
            return true;
        }

        // Otherwise a re-upload with a slightly longer line is churn, not an upgrade.
        // Compare dialogue characters so an ASS-to-SRT switch does not look like growth.
        var threshold = Math.Max(MinimumGrowthChars, (int)(existing.Stats.ContentChars * MinimumGrowthRatio));
        return candidate.ContentChars > existing.Stats.ContentChars + threshold;
    }

    private static bool IsScoreHigher(AiVerdict? verdict, double? existingScore)
    {
        return verdict is not null
               && existingScore is not null
               && verdict.Score >= existingScore.Value + UpgradeScoreGap;
    }

    private static bool IsScoreClose(AiVerdict? verdict, double? existingScore)
    {
        return verdict is not null
               && existingScore is not null
               && verdict.Score > existingScore.Value - UpgradeScoreGap;
    }

    /// <summary>每千字错别字数。</summary>
    private static double TypoRate(int typos, int contentChars)
    {
        return contentChars <= 0 ? 0 : typos * 1000.0 / contentChars;
    }
}
