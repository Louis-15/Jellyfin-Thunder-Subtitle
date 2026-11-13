using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>一份候选在某个片段里的落点。</summary>
/// <param name="PickIndex">1-based id used in the prompt.</param>
/// <param name="ActualStart">该候选实际取到的 cue 时间；没有台词时为 <see cref="TimeSpan.Zero"/>。</param>
/// <param name="OffsetSeconds">与锚点的偏差，用于日志核对；没有台词时为 NaN。</param>
/// <param name="Lines">连续台词文本，已剥离时间轴与样式标记；可能为空。</param>
public sealed record SampledCandidate(
    int PickIndex,
    TimeSpan ActualStart,
    double OffsetSeconds,
    IReadOnlyList<string> Lines);

/// <summary>一个时间锚点，以及所有候选在该位置的台词。</summary>
/// <param name="Index">1-based segment number.</param>
/// <param name="Anchor">参考时间轴上的绝对位置。</param>
/// <param name="Candidates">Every candidate, in shortlist order.</param>
public sealed record SampleSegment(
    int Index,
    TimeSpan Anchor,
    IReadOnlyList<SampledCandidate> Candidates);

/// <summary>
/// 对齐抽样：按时间轴取若干「连续台词片段」，让模型能横向对比同一段剧情的不同译法。
///
/// 为什么不用 cue 索引百分比：同一部影片的候选条数可能差一倍（928 vs 1910，
/// 差的是断句粗细），「第 50% 条」在两份字幕里对应的剧情位置完全不同。
/// 时间轴是字幕与视频的契约 —— 同一时刻说的就是同一句话。
///
/// 为什么不用「等距抽 24 条孤立句子」：模型看不到句与句之间的关系，
/// 就没法判断「这句是不是联系上下文翻的」，而这是非中文影视的核心诉求。
/// </summary>
internal static class AlignedSampler
{
    /// <summary>锚点区间：避开片头制作名单与片尾字幕，这两处台词稀疏。</summary>
    private const double FirstAnchorRatio = 0.12;

    private const double LastAnchorRatio = 0.90;

    /// <summary>
    /// 构建对齐片段。
    /// </summary>
    /// <param name="picks">The shortlist, in the order it will be numbered in the prompt.</param>
    /// <param name="segmentCount">How many segments to sample.</param>
    /// <param name="cuesPerSegment">How many consecutive cues each segment carries.</param>
    /// <param name="tolerance">
    /// 最近一条 cue 与锚点的最大允许偏差。超过它说明这份候选的时间轴不覆盖该位置
    /// （不同版本、缺片头、字幕不全），输出「—」而不是硬取一段无关的台词。
    /// </param>
    /// <param name="maxCharsPerCandidate">
    /// 每份候选在全部片段里的台词字数上限。中文影视的抽样更密（错别字是稀疏事件），
    /// 靠这个参数把它限制在可接受的 token 预算内。
    /// </param>
    /// <returns>片段列表；一个都取不到时返回空。</returns>
    internal static IReadOnlyList<SampleSegment> Build(
        IReadOnlyList<SubtitlePick> picks,
        int segmentCount,
        int cuesPerSegment,
        TimeSpan tolerance,
        int maxCharsPerCandidate)
    {
        if (picks.Count == 0 || segmentCount <= 0 || cuesPerSegment <= 0)
        {
            return [];
        }

        var durations = picks
            .Select(pick => LastEnd(pick.Cues()))
            .Where(duration => duration > TimeSpan.Zero)
            .OrderBy(duration => duration)
            .ToArray();

        if (durations.Length == 0)
        {
            return [];
        }

        // 参考时长取中位数：某个异常短的候选（预告片字幕、只截了半部）不该把锚点整体拉低。
        var reference = durations[durations.Length / 2];
        if (reference <= TimeSpan.Zero)
        {
            return [];
        }

        var segments = new List<SampleSegment>(segmentCount);
        var usedChars = new int[picks.Count];

        for (var i = 0; i < segmentCount; i++)
        {
            var ratio = RatioAt(i, segmentCount);
            var anchor = TimeSpan.FromTicks((long)(reference.Ticks * ratio));
            var sampled = new List<SampledCandidate>(picks.Count);

            for (var p = 0; p < picks.Count; p++)
            {
                var budget = Math.Max(0, maxCharsPerCandidate - usedChars[p]);
                var candidate = SampleOne(picks[p], p + 1, ratio, anchor, reference, cuesPerSegment, tolerance, budget);
                usedChars[p] += candidate.Lines.Sum(line => line.Length);
                sampled.Add(candidate);
            }

            // 所有候选在这个位置都没有台词：整段跳过，别让 prompt 里出现一堆「—」。
            if (sampled.All(candidate => candidate.Lines.Count == 0))
            {
                continue;
            }

            segments.Add(new SampleSegment(segments.Count + 1, anchor, sampled));
        }

        return segments;
    }

    private static SampledCandidate SampleOne(
        SubtitlePick pick,
        int pickIndex,
        double ratio,
        TimeSpan anchor,
        TimeSpan reference,
        int cuesPerSegment,
        TimeSpan tolerance,
        int charBudget)
    {
        var cues = pick.Cues();
        if (cues.Count == 0 || charBudget <= 0)
        {
            return new SampledCandidate(pickIndex, TimeSpan.Zero, double.NaN, Array.Empty<string>());
        }

        var duration = LastEnd(cues);
        var target = duration > TimeSpan.Zero
                     && Math.Abs(duration.Ticks - reference.Ticks) > reference.Ticks * 0.05
            ? TimeSpan.FromTicks((long)(duration.Ticks * ratio))
            : anchor;

        var index = FindClosest(cues, target, out var distance);
        if (index < 0 || distance > tolerance)
        {
            return new SampledCandidate(pickIndex, TimeSpan.Zero, distance.TotalSeconds, Array.Empty<string>());
        }

        var lines = new List<string>(cuesPerSegment);
        var chars = 0;

        for (var c = index; c < cues.Count && lines.Count < cuesPerSegment; c++)
        {
            var text = cues[c].Text.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (chars + text.Length > charBudget)
            {
                break;
            }

            lines.Add(text);
            chars += text.Length;
        }

        var actualStart = cues[index].Start;
        return new SampledCandidate(pickIndex, actualStart, (actualStart - target).TotalSeconds, lines);
    }

    /// <summary>第 <paramref name="index"/> 个锚点在 12%～90% 区间里的比例。</summary>
    private static double RatioAt(int index, int count)
    {
        return count <= 1
            ? (FirstAnchorRatio + LastAnchorRatio) / 2
            : FirstAnchorRatio + ((LastAnchorRatio - FirstAnchorRatio) * index / (count - 1));
    }

    private static TimeSpan LastEnd(IReadOnlyList<SubtitleCue> cues)
    {
        for (var i = cues.Count - 1; i >= 0; i--)
        {
            if (cues[i].End > TimeSpan.Zero)
            {
                return cues[i].End;
            }
        }

        return TimeSpan.Zero;
    }

    /// <summary>
    /// 二分查找 Start 最接近 <paramref name="target"/> 的 cue。
    /// 正常字幕的 cue 按时间递增；万一遇到乱序文件，二分只能保证「大概率正确」，
    /// 但相邻 cue 的时间差通常在几秒内，不会影响片段对齐。
    /// </summary>
    private static int FindClosest(IReadOnlyList<SubtitleCue> cues, TimeSpan target, out TimeSpan distance)
    {
        int low = 0, high = cues.Count - 1, best = -1;
        distance = TimeSpan.MaxValue;

        while (low <= high)
        {
            var mid = (low + high) / 2;
            var start = cues[mid].Start;
            var delta = (start - target).Duration();

            if (delta < distance)
            {
                distance = delta;
                best = mid;
            }

            if (start < target)
            {
                low = mid + 1;
            }
            else if (start > target)
            {
                high = mid - 1;
            }
            else
            {
                break;
            }
        }

        return best;
    }
}
