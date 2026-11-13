using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.ThunderSubtitle.Core;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

/// <summary>
/// 对齐抽样的核心约束：每个片段必须是「同一时间位置的连续几句」，
/// 而不是散落全文的孤立句子 —— 模型要靠上下文才能判断翻译有没有联系上下文。
/// </summary>
public class AlignedSamplerTests
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(60);

    private static SubtitlePick Pick(string name, string text) => new(name, ".srt", text, "utf-8");

    /// <summary>构造一份 cue 间隔固定的字幕，每条台词写着自己出现的秒数（<c>S{秒}</c>）。</summary>
    private static string Timed(int count, double interval, int startSeconds = 0, string tag = "S")
    {
        var builder = new StringBuilder(count * 40);
        for (var i = 0; i < count; i++)
        {
            var start = TimeSpan.FromSeconds(startSeconds + (i * interval));
            var end = start.Add(TimeSpan.FromSeconds(interval - 0.1));
            builder.Append(i + 1).Append('\n')
                .Append(Clock(start)).Append(" --> ").Append(Clock(end)).Append('\n')
                .Append(tag).Append((int)(startSeconds + (i * interval))).Append('\n')
                .Append('\n');
        }

        return builder.ToString();
    }

    private static string Clock(TimeSpan value)
    {
        return value.ToString(@"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture);
    }

    private static List<int> Marks(SampledCandidate candidate)
    {
        return candidate.Lines
            .Select(line => int.Parse(line.TrimStart('S'), CultureInfo.InvariantCulture))
            .ToList();
    }

    [Fact]
    public void SegmentsCarryConsecutiveCues()
    {
        var segments = AlignedSampler.Build([Pick("a.srt", Timed(200, 3.0))], 4, 3, Tolerance, 5000);

        Assert.Equal(4, segments.Count);
        foreach (var segment in segments)
        {
            var marks = Marks(segment.Candidates[0]);
            Assert.Equal(3, marks.Count);

            // 连续：相邻两条相差正好一个 cue 间隔，证明不是散点抽样。
            for (var i = 1; i < marks.Count; i++)
            {
                Assert.Equal(3, marks[i] - marks[i - 1]);
            }
        }
    }

    [Fact]
    public void DifferentCueGranularityStillLandsOnTheSameStoryPosition()
    {
        // 928 条 vs 1910 条：条数差一倍（断句粗细不同），时间轴却指向同一部片。
        // 按 cue 索引百分比取，这两份在「第 50% 条」会落到完全不同的剧情位置。
        var coarse = Pick("coarse.srt", Timed(928, 4.0));
        var fine = Pick("fine.srt", Timed(1910, 2.0));

        var segments = AlignedSampler.Build([coarse, fine], 6, 2, Tolerance, 8000);

        Assert.Equal(6, segments.Count);
        foreach (var segment in segments)
        {
            // 比较片段起点：条数少的那份断句粗，起点最多差一个粗粒度间隔。
            var coarseStart = Marks(segment.Candidates[0])[0];
            var fineStart = Marks(segment.Candidates[1])[0];
            Assert.True(
                Math.Abs(coarseStart - fineStart) <= 4,
                $"片段 {segment.Index}：{coarseStart}s vs {fineStart}s，落点偏差过大");
        }
    }

    [Fact]
    public void AnchorsAvoidTheHeadAndTheTail()
    {
        var segments = AlignedSampler.Build([Pick("a.srt", Timed(300, 3.0))], 5, 2, Tolerance, 5000);

        var marks = segments.SelectMany(segment => Marks(segment.Candidates[0])).ToList();

        // 片头是制作名单、片尾是字幕，台词稀疏；锚点区间是 12%~90%。
        Assert.DoesNotContain(0, marks);
        Assert.True(marks.Min() >= 40, $"最早落点 {marks.Min()}s 进入了片头");
        Assert.True(marks.Max() <= 820, $"最晚落点 {marks.Max()}s 越过了片尾");
    }

    [Fact]
    public void CandidateThatDoesNotCoverThePositionIsMarkedEmpty()
    {
        // 第二份缺了前 300 秒（不同版本 / 只截了后半部），时长却差不多，
        // 不会被「时长校正」分支接住，只能靠容差判定为不覆盖。
        var full = Pick("full.srt", Timed(200, 3.0));
        var tail = Pick("tail.srt", Timed(100, 3.0, startSeconds: 300));

        var segments = AlignedSampler.Build([full, tail], 5, 2, Tolerance, 5000);

        Assert.NotEmpty(segments);
        Assert.Contains(segments, segment =>
            segment.Candidates[0].Lines.Count > 0 && segment.Candidates[1].Lines.Count == 0);
    }

    [Fact]
    public void DurationMismatchFallsBackToEachCandidatesOwnTimeline()
    {
        // 第二份多了 300 秒片头（不同版本），时长差 > 5%，必须按自身时长换算锚点。
        var shortOne = Pick("short.srt", Timed(200, 3.0));
        var longOne = Pick("long.srt", Timed(300, 3.0));

        var segments = AlignedSampler.Build([shortOne, longOne], 3, 1, Tolerance, 5000);

        Assert.Equal(3, segments.Count);
        foreach (var segment in segments)
        {
            var ratio = segment.Anchor.Ticks / (double)TimeSpan.FromSeconds(900).Ticks;
            var shortMark = Marks(segment.Candidates[0])[0];
            var longMark = Marks(segment.Candidates[1])[0];

            // 各自按自己的时长取同一个比例位置。
            Assert.True(Math.Abs(shortMark - 600 * ratio) <= 3, $"短版落点 {shortMark}s 与 600s×{ratio:F2} 不符");
            Assert.True(Math.Abs(longMark - 900 * ratio) <= 3, $"长版落点 {longMark}s 与 900s×{ratio:F2} 不符");
        }
    }

    [Fact]
    public void CharacterBudgetCapsTheSample()
    {
        var segments = AlignedSampler.Build([Pick("a.srt", Timed(200, 3.0))], 6, 8, Tolerance, 40);

        var total = segments.Sum(segment => segment.Candidates[0].Lines.Sum(line => line.Length));
        Assert.True(total <= 40, $"抽样用了 {total} 字，超过 40 字预算");
        Assert.NotEmpty(segments);
    }

    [Fact]
    public void TextWithoutUsableCuesProducesNoSegments()
    {
        Assert.Empty(AlignedSampler.Build([Pick("a.srt", "这不是字幕"), Pick("b.srt", "也不是")], 6, 4, Tolerance, 5000));
    }

    [Fact]
    public void ReferenceDurationIgnoresAnOutlier()
    {
        // 有三份时长正常，一份只有 1/3 长：中位数不能被那个异常值拉低。
        var picks = new[]
        {
            Pick("a.srt", Timed(200, 3.0)),
            Pick("b.srt", Timed(200, 3.0)),
            Pick("c.srt", Timed(200, 3.2)),
            Pick("bad.srt", Timed(60, 3.0))
        };

        var segments = AlignedSampler.Build(picks, 4, 2, Tolerance, 8000);

        var anchors = segments.Select(segment => segment.Anchor.TotalSeconds).ToList();
        Assert.All(anchors, anchor => Assert.True(anchor >= 60, $"锚点 {anchor:F0}s 被异常短的那份拉低了"));
    }
}
