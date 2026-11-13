using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>
/// 决定把哪几份候选送给 AI 评审。
///
/// 这个类只负责一件事：按语言档位分配送审名额。
/// 「档内挑谁」完全交给 <see cref="SubtitleRanker.Rank"/> 给出的顺序 ——
/// 它已经按健康度、条数偏离度、名字关键词排过一遍，这里再套一层「条数分档」
/// 只会把排好的顺序重新打乱。
///
/// 历史包袱：1.9.x 时这里按「条数分档、逐档轮流取代表」来选，
/// 目的是防止名额被同一份机翻的几个转码占满。那个目的现在由两道更可靠的机制承担 ——
/// <see cref="NearDuplicate"/> 合并转码版本、<see cref="SubtitleRanker.DropUnusable"/>
/// 淘汰有硬伤的候选 —— 所以分档逻辑整个删掉了。
/// </summary>
internal static class ShortlistSelector
{
    /// <summary>Hard cap on how many candidates one judgement may compare.</summary>
    internal const int MaxJudgedCandidates = 12;

    /// <summary>
    /// 每个语言档位至少送几份。
    /// 这是「日本AV 的简体分不够就降级到繁体」能成立的前提：
    /// Tier 1 一份都没送审，就没有分数可以拿来比较。
    /// </summary>
    internal const int MinimumPerLanguageTier = 2;

    /// <summary>
    /// 单个语言档位最多送几份，避免名额全压在首选档。
    /// 总上限 12 份时，两个档位各 6 份刚好占满。
    /// </summary>
    internal const int MaximumPerLanguageTier = 6;

    /// <summary>
    /// Picks what the model compares.
    ///
    /// 规则（按语言档位分组，组内保持排序链给出的质量顺序）：
    /// 1. 每个非空档位先取 <paramref name="perTier"/> 份；
    /// 2. 名额还有剩就按档位顺序补，单档不超过 <see cref="MaximumPerLanguageTier"/> 份
    ///    —— 首选档凑不满时（比如纯中文字幕只有两份），
    ///    多送几份次选档给模型做横向比较，判断会比只送两三份准得多；
    /// 3. 总数不超过 <see cref="MaxJudgedCandidates"/> 份。
    ///
    /// 这里刻意不按条数二次分档。用户的原话是「字数多不代表就是精品」，
    /// 同一个档位里哪几份值得看，应该由排序链和 AI 回答，
    /// 不该由「第几档条数」这种跟内容质量无关的算术决定。
    /// </summary>
    /// <param name="ranked">Acceptable candidates, best first (already ordered by tier).</param>
    /// <param name="kind">The media kind, which decides the language tiers.</param>
    /// <param name="perTier">每个语言档位最多取几份。</param>
    /// <returns>按语言档位从高到低排好的候选，最多 <see cref="MaxJudgedCandidates"/> 份。</returns>
    internal static List<SubtitlePick> Select(
        IReadOnlyList<SubtitlePick> ranked,
        MediaKind kind,
        int perTier)
    {
        if (ranked.Count == 0)
        {
            return [];
        }

        var groups = ranked
            .GroupBy(pick => LanguagePolicy.TierOf(kind, pick.Language))
            .Where(group => group.Key != LanguagePolicy.Unacceptable)
            .OrderBy(group => group.Key)
            .Select(group => group.ToList())
            .ToList();

        if (groups.Count == 0)
        {
            return [];
        }

        var quota = Math.Clamp(perTier, MinimumPerLanguageTier, MaximumPerLanguageTier);
        var counts = new int[groups.Count];
        var taken = new HashSet<SubtitlePick>(ReferenceEqualityComparer.Instance);
        var shortlist = new List<SubtitlePick>();

        // 第一轮：每个档位按质量顺序取前 quota 份。
        for (var i = 0; i < groups.Count; i++)
        {
            for (var j = 0; j < groups[i].Count; j++)
            {
                if (counts[i] >= quota || shortlist.Count >= MaxJudgedCandidates)
                {
                    break;
                }

                if (taken.Add(groups[i][j]))
                {
                    shortlist.Add(groups[i][j]);
                    counts[i]++;
                }
            }
        }

        // 第二轮：把剩下的名额补给还没到单档上限的档位。
        for (var i = 0; i < groups.Count && shortlist.Count < MaxJudgedCandidates; i++)
        {
            for (var j = 0; j < groups[i].Count; j++)
            {
                if (counts[i] >= MaximumPerLanguageTier || shortlist.Count >= MaxJudgedCandidates)
                {
                    break;
                }

                if (taken.Add(groups[i][j]))
                {
                    shortlist.Add(groups[i][j]);
                    counts[i]++;
                }
            }
        }

        // 补进来的候选在列表尾部，这里按档位重排一次，让输出顺序与档位一致
        // （日志按档位分组展示，顺序一致才读得顺）。OrderBy 是稳定排序，
        // 档内仍然保持「先取的在前」。
        return shortlist
            .OrderBy(pick => LanguagePolicy.TierOf(kind, pick.Language))
            .ToList();
    }
}
