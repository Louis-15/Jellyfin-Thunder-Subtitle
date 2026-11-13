using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>
/// 排序链里「跟质量有关、但跟台词多少无关」的那部分：
/// 健康度淘汰、条数偏离度、候选名的质量关键词。
///
/// 单独成文件的理由：<see cref="SubtitleRanker"/> 已经 300 多行，塞进来会破 400 行警戒线；
/// 而这几个方法主题一致（都是「不看长度看质量」），拆出来能独立测试。
/// </summary>
public static partial class SubtitleRanker
{
    /// <summary>
    /// 候选名里代表「专业译制」的词。命中越多越可能是一份认真做过的字幕。
    ///
    /// 这些词之所以可信：普通字幕组不会在文件名里写「国配」「院线」，
    /// 写了的基本都是从 DVD/BD 的官方音轨或院线拷贝里扒出来的台本。
    ///
    /// 只当同分时的微调，绝不单独决定顺序 —— 文件名终究是发布者自己写的，
    /// 而真正判断质量要靠 <see cref="SubtitleHealth"/> 和 AI。
    /// </summary>
    private static readonly string[] QualityKeywords =
    [
        "国配",   // 院线国语配音台本
        "官方",   // 官方字幕
        "院线",   // 院线版
        "公映",   // 公映版
        "译制",   // 译制片
        "央视",   // 央视译制
        "正版",   // 正版光盘提取
        "台配",   // 台版配音
        "台版"    // 台版发行
    ];

    /// <summary>条数偏离中位数时，每 5% 算一个档位。</summary>
    internal const double DeviationBucketPercent = 5.0;

    /// <summary>健康度筛选的结果。</summary>
    /// <param name="Kept">可以进入比较的候选。</param>
    /// <param name="Dropped">被淘汰的候选及原因，只用于写日志。</param>
    internal sealed record HealthFilterResult(
        IReadOnlyList<SubtitlePick> Kept,
        IReadOnlyList<(SubtitlePick Pick, string Reason)> Dropped);

    /// <summary>Counts how many professional-dub keywords the candidate name carries.</summary>
    /// <param name="name">The candidate name.</param>
    /// <returns>The keyword count.</returns>
    public static int CountQualityKeywords(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return 0;
        }

        return QualityKeywords.Count(keyword => name.Contains(keyword, StringComparison.Ordinal));
    }

    /// <summary>
    /// 丢掉触了红线的候选：那些含中文率过低、满屏未译标注、
    /// 中间有大段空白或者断句粗到没法用的。
    ///
    /// 这一步排在排序之前、语言过滤之后：语言过滤回答「形式对不对」，
    /// 健康度回答「做得好不好」，两个问题互不相关，分开判断日志才读得懂。
    /// </summary>
    /// <param name="picks">语言上已经合格的候选。</param>
    /// <returns>留下的候选和被淘汰的候选（附原因）。</returns>
    internal static HealthFilterResult DropUnusable(IReadOnlyList<SubtitlePick> picks)
    {
        var kept = new List<SubtitlePick>(picks.Count);
        var dropped = new List<(SubtitlePick Pick, string Reason)>();

        foreach (var pick in picks)
        {
            if (pick.Health.IsUsable)
            {
                kept.Add(pick);
            }
            else
            {
                dropped.Add((pick, pick.Health.DefectReason));
            }
        }

        // 兜底：全都不合格时宁可全留。
        // 四条红线是启发式的，遇到整批同风格的候选（比如一部冷门片只有几家站点
        // 转载同一份机翻）时会同时触发，这时强行淘汰等于本次搜索颗粒无收 ——
        // 「从差的里面挑一份相对最好的」永远比「什么都不给」有用。
        if (kept.Count == 0)
        {
            return new HealthFilterResult(picks, []);
        }

        return new HealthFilterResult(kept, dropped);
    }

    /// <summary>
    /// 算这一批候选的条数中位数。
    ///
    /// 用中位数而不是平均数：机翻字幕常常条数异常多（一句拆成三段），
    /// 平均数会被它拉高，中位数不会被单个离群值带跑。
    /// </summary>
    /// <param name="picks">The candidates.</param>
    /// <returns>The median cue count, 0 when nothing is countable.</returns>
    internal static double MedianCueCount(IEnumerable<SubtitlePick> picks)
    {
        var counts = picks
            .Select(pick => pick.CueCount)
            .Where(count => count > 0)
            .ToList();

        if (counts.Count == 0)
        {
            return 0;
        }

        counts.Sort();
        var middle = counts.Count / 2;
        return counts.Count % 2 == 1
            ? counts[middle]
            : (counts[middle - 1] + counts[middle]) / 2.0;
    }

    /// <summary>
    /// 把「条数偏离中位数多少」换算成档位。
    ///
    /// 为什么用偏离度而不是条数：一部 143 分钟的片子正常在 1200~1800 条之间，
    /// 但不同字幕组的断句风格差别很大，绝对条数没有可比性。
    /// 真正有信息量的是「这一份跟同批其他份差多远」：
    /// 实测《加勒比海盗》里，用户挑中的国配台本偏离中位数 0.6%，
    /// 而两份漏译严重的候选偏离 -24%，这个差距比字数更能说明问题。
    ///
    /// 每 5% 一档：同档内视为「条数相当」，此时才让质量关键词起作用。
    /// </summary>
    /// <param name="pick">The candidate.</param>
    /// <param name="median">这一批的条数中位数。</param>
    /// <returns>The bucket index, lower is closer to the median.</returns>
    internal static int DeviationBucket(SubtitlePick pick, double median)
    {
        if (median <= 0 || pick.CueCount <= 0)
        {
            return int.MaxValue;
        }

        var deviation = Math.Abs(pick.CueCount - median) / median;
        return (int)Math.Round(deviation * 100.0 / DeviationBucketPercent, MidpointRounding.AwayFromZero);
    }
}
