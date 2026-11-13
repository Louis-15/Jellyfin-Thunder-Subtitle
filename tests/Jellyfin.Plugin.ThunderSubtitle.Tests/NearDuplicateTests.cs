using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.ThunderSubtitle.Core;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

/// <summary>
/// 近似去重要解决的是「同一份机翻的不同转码占满送审名额」——
/// 实测 MIUM-1457 出现过 610 条 3205 字与 610 条 3200 字两份几乎一样的候选。
/// </summary>
public class NearDuplicateTests
{
    private static readonly string[] Lines =
    [
        "顶到了。",
        "是不是有什么顶着你？",
        "嗯，有东西顶着我。",
        "是你那儿顶着我了，我最喜欢了。",
        "胀得都快撑破了。",
        "我也出了好多汗。",
        "没关系的。",
        "我又不嫌弃。",
        "都变成这样了吗？",
        "受不了了。"
    ];

    private static string Srt(Func<int, string> line, int count = 40)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            var start = TimeSpan.FromSeconds(i * 3);
            var end = start.Add(TimeSpan.FromSeconds(2.5));
            builder.Append(i + 1).Append('\n')
                .Append(start.ToString(@"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture)).Append(" --> ")
                .Append(end.ToString(@"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture)).Append('\n')
                .Append(line(i)).Append('\n')
                .Append('\n');
        }

        return builder.ToString();
    }

    private static SubtitlePick Pick(string name, string text) => new(name, ".srt", text, "utf-8");

    private static string BaseLine(int index) => Lines[index % Lines.Length];

    [Fact]
    public void PunctuationVariantsAreMerged()
    {
        // 同一份字幕，另一个站点把句号换成逗号、去掉了几处空格。
        var original = Pick("a.srt", Srt(BaseLine));
        var retyped = Pick("b.srt", Srt(index => BaseLine(index).Replace("，", ",").Replace("。", "．")));

        var (kept, merged) = NearDuplicate.Collapse([original, retyped]);

        Assert.Equal(1, merged);
        Assert.Single(kept);
        Assert.Equal("a.srt", kept[0].CandidateName);
    }

    [Fact]
    public void DifferentTranslationsAreKeptApart()
    {
        // 同一段剧情、不同译法：这里不能合并，否则等于替用户做了选择。
        var humanLike = Pick("human.srt", Srt(index => "这是我自己的译法" + index + "。"));
        var machineLike = Pick("mt.srt", Srt(index => "这个完全是另一种说法" + index + "。"));

        var (kept, merged) = NearDuplicate.Collapse([humanLike, machineLike]);

        Assert.Equal(0, merged);
        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public void SimplifiedAndTraditionalAreNotMerged()
    {
        // 简体版和繁体版是「不同的版本」，用户可能更想要其中一份。
        var simplified = Pick("chs.srt", Srt(index => "这是简体字幕第" + index + "句"));
        var traditional = Pick("cht.srt", Srt(index => "這是繁體字幕第" + index + "句"));

        var (kept, merged) = NearDuplicate.Collapse([simplified, traditional]);

        Assert.Equal(0, merged);
        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public void RankedOrderDecidesWhichOneSurvives()
    {
        var better = Pick("better.srt", Srt(BaseLine));
        var duplicate = Pick("duplicate.srt", Srt(BaseLine));

        var (kept, _) = NearDuplicate.Collapse([better, duplicate]);

        Assert.Single(kept);
        Assert.Equal("better.srt", kept[0].CandidateName);
    }

    [Fact]
    public void ThresholdAtOneDisablesMerging()
    {
        var original = Pick("a.srt", Srt(BaseLine));
        var retyped = Pick("b.srt", Srt(index => BaseLine(index).Replace("。", "．")));

        var (kept, merged) = NearDuplicate.Collapse([original, retyped], 1.0);

        Assert.Equal(0, merged);
        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public void VeryDifferentVolumesAreNeverCompared()
    {
        // 「完整版 vs 删减版」不该合并：条数差太多时，即使内容相似也保留两份。
        var full = Pick("full.srt", Srt(BaseLine, 100));
        var cut = Pick("cut.srt", Srt(BaseLine, 50));

        var (kept, merged) = NearDuplicate.Collapse([full, cut]);

        Assert.Equal(0, merged);
        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public void SingleCandidateIsReturnedUnchanged()
    {
        var (kept, merged) = NearDuplicate.Collapse([Pick("a.srt", Srt(BaseLine))]);

        Assert.Equal(0, merged);
        Assert.Single(kept);
    }

    [Fact]
    public void EmptyInputIsHandled()
    {
        var (kept, merged) = NearDuplicate.Collapse(Array.Empty<SubtitlePick>());

        Assert.Equal(0, merged);
        Assert.Empty(kept);
    }

    /// <summary>生成一段与其它种子完全不同的随机台词，保证 3-gram 差异远超阈值。</summary>
    private static string NoiseSrt(int seed, int cueCount, int charsPerCue)
    {
        var random = new Random(seed + 1);
        var builder = new StringBuilder();
        for (var i = 0; i < cueCount; i++)
        {
            var start = TimeSpan.FromSeconds(i * 3);
            var end = start.Add(TimeSpan.FromSeconds(2.5));
            builder.Append(i + 1).Append('\n')
                .Append(start.ToString(@"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture)).Append(" --> ")
                .Append(end.ToString(@"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture)).Append('\n');

            for (var c = 0; c < charsPerCue; c++)
            {
                builder.Append((char)('a' + random.Next(26)));
            }

            builder.Append('\n').Append('\n');
        }

        return builder.ToString();
    }

    [Fact]
    public void LargeBatchStaysFast()
    {
        // 100 份互不相同的候选，每份 200 句：两两比较会是 4950 次 × 数千个 3-gram。
        // 粗筛必须把配对压到「每份最多 5 次」，否则这一步比下载还慢。
        var picks = new List<SubtitlePick>();
        for (var i = 0; i < 100; i++)
        {
            picks.Add(Pick("p" + i + ".srt", NoiseSrt(i, 200, 12)));
        }

        var watch = Stopwatch.StartNew();
        var (kept, merged) = NearDuplicate.Collapse(picks);
        watch.Stop();

        Assert.True(watch.ElapsedMilliseconds < 1500, $"近似去重用了 {watch.ElapsedMilliseconds} ms，太慢");
        Assert.Equal(0, merged);
        Assert.Equal(100, kept.Count);
    }
}
