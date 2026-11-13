using System;
using System.Text;
using Jellyfin.Plugin.ThunderSubtitle.Core;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

/// <summary>
/// 排序链里「跟长度无关」的那几个键：
/// 缺陷数、条数偏离度、候选名质量关键词。
///
/// 这组测试守的是一条明确的立场：字数多不代表质量好。
/// 双语字幕夹着整行英文，字数天然翻倍；一份纯中文的国配台本字数少得多，
/// 却是用户手工挑中的那份。旧逻辑按 ContentChars 降序排，
/// 正好把好的排到了后面。
/// </summary>
public class SubtitleRankerHealthTests
{
    [Fact]
    public void MedianCueCountIgnoresTheOutliers()
    {
        var picks = new[] { Lines("a.srt", 100), Lines("b.srt", 200), Lines("c.srt", 3000) };

        Assert.Equal(200, SubtitleRanker.MedianCueCount(picks));
    }

    [Fact]
    public void DeviationBucketGroupsByDistanceToTheMedian()
    {
        const double median = 300.0;

        Assert.Equal(0, SubtitleRanker.DeviationBucket(Lines("a.srt", 300), median));   // 0%
        Assert.Equal(0, SubtitleRanker.DeviationBucket(Lines("b.srt", 305), median));   // 1.7%
        Assert.Equal(1, SubtitleRanker.DeviationBucket(Lines("c.srt", 315), median));   // 5%
        Assert.Equal(2, SubtitleRanker.DeviationBucket(Lines("d.srt", 330), median));   // 10%
        Assert.Equal(6, SubtitleRanker.DeviationBucket(Lines("e.srt", 390), median));   // 30%

        // 解析不出台词的候选排到最后，而不是被当成「偏离 0」混进第一档。
        var empty = new SubtitlePick("junk.srt", ".srt", "不是字幕", "utf-8");
        Assert.Equal(int.MaxValue, SubtitleRanker.DeviationBucket(empty, median));
    }

    [Fact]
    public void QualityKeywordsCountProfessionalDubMarkers()
    {
        Assert.Equal(0, SubtitleRanker.CountQualityKeywords("加勒比海盗.2003.srt"));
        Assert.Equal(1, SubtitleRanker.CountQualityKeywords("加勒比海盗 [国配导评].srt"));
        Assert.Equal(2, SubtitleRanker.CountQualityKeywords("加勒比海盗 国配 院线版.srt"));
        Assert.Equal(0, SubtitleRanker.CountQualityKeywords(null));
        Assert.Equal(0, SubtitleRanker.CountQualityKeywords("   "));
    }

    [Fact]
    public void RankPutsTheHealthierCandidateFirst()
    {
        // big：字数更多（500 条），但中间空了 10 分钟 —— 那段剧情整段没翻。
        // clean：只有 300 条，但没有任何缺陷。
        // 旧逻辑按字数降序排会把 big 放在前面；新逻辑先看缺陷数。
        var big = Lines("big.srt", 500, gapAfter: 250, gapSeconds: 600);
        var clean = Lines("clean.srt", 300);

        var ranked = SubtitleRanker.Rank([big, clean], MediaKind.JapaneseAv);

        Assert.True(big.ContentChars > clean.ContentChars, "构造前提：big 的字数必须更多");
        Assert.Equal("clean.srt", ranked[0].CandidateName);
    }

    [Fact]
    public void RankPrefersCueCountsNearTheBatchMedian()
    {
        // 三份同语言、都健康的候选。中位数是 300 条：
        // near 刚好落在中位数上，short 少了 47%（漏译），long 多了 53%（过度拆分）。
        // 旧逻辑会把 long 排在第一位 —— 它字数最多。
        var near = Lines("near.srt", 300);
        var shortOne = Lines("short.srt", 160);
        var longOne = Lines("long.srt", 460);

        var ranked = SubtitleRanker.Rank([longOne, shortOne, near], MediaKind.JapaneseAv);

        Assert.Equal("near.srt", ranked[0].CandidateName);
    }

    [Fact]
    public void QualityKeywordsOnlyBreakTiesInsideTheSameBucket()
    {
        // 条数相同时，带「国配」的那份排前面。
        // 两份必须用不同的 salt 构造：Rank 会按内容哈希去重，
        // 内容一模一样的两份会先被合并成一份，那就测不到排序了。
        var dubbed = Lines("d [国配].srt", 300, salt: "甲");
        var plain = Lines("p.srt", 300, salt: "乙");
        Assert.Equal(
            "d [国配].srt",
            SubtitleRanker.Rank([plain, dubbed], MediaKind.JapaneseAv)[0].CandidateName);

        // 条数差得远时「国配」不能翻盘：偏离中位数 47% 意味着那份字幕漏了一小半，
        // 名字写得再漂亮也不能盖过这个事实。
        var farDubbed = Lines("f [国配].srt", 160);
        var nearA = Lines("a.srt", 300, salt: "甲");
        var nearB = Lines("b.srt", 300, salt: "乙");
        Assert.Equal(
            "a.srt",
            SubtitleRanker.Rank([farDubbed, nearA, nearB], MediaKind.JapaneseAv)[0].CandidateName);
    }

    /// <summary>
    /// 构造一份简体字幕：每条 1 秒、首尾相接。
    ///
    /// 时间轴首尾相接是刻意的 —— 这样健康度扫描看不到任何空白，
    /// 缺陷数恒为 0，测试才能专注在「偏离度」和「关键词」这两个键上。
    /// </summary>
    /// <param name="name">候选名。</param>
    /// <param name="cues">台词条数。</param>
    /// <param name="gapAfter">在第几条之后插入空白，-1 表示不插。</param>
    /// <param name="gapSeconds">插入的空白秒数。</param>
    /// <param name="salt">台词尾部的标记，用来让内容不同的两份候选哈希不同。</param>
    /// <returns>构造好的候选。</returns>
    private static SubtitlePick Lines(
        string name,
        int cues,
        int gapAfter = -1,
        double gapSeconds = 0,
        string salt = "")
    {
        var builder = new StringBuilder();
        var clock = 1.0;

        for (var i = 0; i < cues; i++)
        {
            if (gapAfter >= 0 && i == gapAfter)
            {
                clock += gapSeconds;
            }

            builder.Append(i + 1).Append('\n')
                .Append(Timestamp(clock)).Append(" --> ").Append(Timestamp(clock + 1)).Append('\n')
                .Append("这是我们说过的问题").Append(salt).Append("\n\n");

            clock += 1.0;
        }

        return new SubtitlePick(name, ".srt", builder.ToString(), "utf-8");
    }

    /// <summary>秒数转成 SRT 的 <c>hh:mm:ss,fff</c>。</summary>
    private static string Timestamp(double seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        return FormattableString.Invariant(
            $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00},{span.Milliseconds:000}");
    }
}
