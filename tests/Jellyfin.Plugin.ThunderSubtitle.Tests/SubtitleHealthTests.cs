using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.ThunderSubtitle.Core;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

/// <summary>
/// 健康度扫描的单元测试。
///
/// 这套指标的作用只有一个：在送 AI 之前把「明显有硬伤」的候选淘汰掉。
/// 它不负责挑出最好的那份 —— 那要读台词才知道，任何本地统计都做不到，
/// 所以这里的用例只验证「什么样的情况该被识别成缺陷」，不验证排名。
/// </summary>
public class SubtitleHealthTests
{
    [Fact]
    public void AHealthySubtitleHasNoDefect()
    {
        var pick = Build(
            "ok.srt",
            (1, 3, "这是我们说过的问题"),
            (3, 5, "你到底想说什么"),
            (5, 7, "我不想再谈了"));

        Assert.Equal(3, pick.Health.CueCount);
        Assert.Equal(3, pick.Health.ChineseCues);
        Assert.Equal(1.0, pick.Health.ChineseRatio);
        Assert.Equal(0, pick.Health.Defects);
        Assert.Equal(string.Empty, pick.Health.DefectReason);
        Assert.True(pick.Health.IsUsable);
    }

    [Fact]
    public void MostlyEnglishCuesAreNotAChineseSubtitle()
    {
        // 一整批全是英文行的「中文字幕」：这其实是外挂英文字幕，
        // 只在末尾零星嵌了一条中文。送审它纯属浪费名额。
        var cues = Enumerable.Range(0, 10)
            .Select(i => ((double)i, (double)i + 0.8, "This line carries no Chinese at all"))
            .Append((10.0, 10.8, "这一条才有中文"))
            .ToArray();

        var pick = Build("english.srt", cues);

        Assert.Equal(1, pick.Health.Defects);
        Assert.Contains("含中文", pick.Health.DefectReason);
        Assert.False(pick.Health.IsUsable);
    }

    [Fact]
    public void UntranslatedBracketAnnotationsAreCounted()
    {
        // 「[Jack sighs]」这种音效提示没做本地化。超过 20 条就说明整份字幕
        // 的音效/动作提示都原样保留了。
        var annotated = Enumerable.Range(0, 25)
            .Select(i => ((double)i, (double)i + 0.8, "[Jack sighs]"))
            .ToArray();
        var normal = Enumerable.Range(25, 25)
            .Select(i => ((double)i, (double)i + 0.8, "这是正常的中文台词"))
            .ToArray();

        var pick = Build("bracket.srt", annotated.Concat(normal).ToArray());

        Assert.Equal(25, pick.Health.UntranslatedCues);

        // 两个缺陷：括号标注超标，且含中文率被那 25 条英文拉到 50%。
        Assert.Equal(2, pick.Health.Defects);
        Assert.Contains("未译标注", pick.Health.DefectReason);
    }

    [Fact]
    public void ALongSilenceIsFlagged()
    {
        // 前一句在 3 秒结束，下一句 603 秒才开始：中间空了整整 10 分钟，
        // 那段剧情整段没有字幕。
        var pick = Build(
            "gap.srt",
            (1, 3, "这是第一句"),
            (603, 605, "这是十分钟后的第二句"));

        Assert.Equal(600, Math.Round(pick.Health.MaxGapSeconds));
        Assert.Contains("空白", pick.Health.DefectReason);
        Assert.False(pick.Health.IsUsable);
    }

    [Fact]
    public void CuesHoldingSeveralLinesAreFlagged()
    {
        // 每条挂 15 秒：这是把五六句台词塞进同一条 cue，
        // 播放时一句话挂十几秒，断句粗到没法跟读。
        var cues = Enumerable.Range(0, 10)
            .Select(i => (i * 20.0, (i * 20.0) + 15.0, "一二三四五句话全部挤在同一条字幕里"))
            .ToArray();

        var pick = Build("long.srt", cues);

        Assert.True(pick.Health.AverageCueSeconds > 10, $"均长算错了：{pick.Health.AverageCueSeconds}");
        Assert.Contains("均长", pick.Health.DefectReason);
    }

    [Fact]
    public void AnUnparseableSubtitleIsUnusable()
    {
        var pick = new SubtitlePick("junk.srt", ".srt", "不是字幕", "utf-8");

        Assert.Equal(0, pick.Health.CueCount);
        Assert.Equal(1, pick.Health.Defects);
        Assert.False(pick.Health.IsUsable);
    }

    [Fact]
    public void HalfWidthPunctuationIsCountedButNeverRejects()
    {
        // 国配台本这类专业译制稿反而半角标点偏多（照抄台本的排版），
        // 所以这个数字只写进日志，绝不参与淘汰 —— 否则会把最好的那份筛掉。
        var pick = Build(
            "half.srt",
            (1, 3, "这是第一句,后面用半角逗号."),
            (3, 5, "这是第二句,同样半角."));

        Assert.Equal(2, pick.Health.HalfWidthPunctuation);
        Assert.Equal(0, pick.Health.Defects);
        Assert.True(pick.Health.IsUsable);
    }

    [Fact]
    public void DropUnusableRemovesOnlyTheBrokenOnes()
    {
        var good = Build(
            "good.srt",
            (1, 3, "这是正常的中文台词"),
            (3, 5, "这也是正常的中文台词"));

        var bad = Build(
            "bad.srt",
            (1, 1.8, "only english here"),
            (2, 2.8, "still english only"),
            (3, 3.8, "nothing in chinese"));

        var result = SubtitleRanker.DropUnusable([good, bad]);

        Assert.Same(good, Assert.Single(result.Kept));

        var dropped = Assert.Single(result.Dropped);
        Assert.Same(bad, dropped.Pick);
        Assert.Contains("含中文", dropped.Reason);
    }

    [Fact]
    public void DropUnusableKeepsEverythingWhenNothingPasses()
    {
        // 整批都不合格时必须退回全部：四条红线是启发式的，
        // 一部冷门片只有几家站点转载同一份机翻时会同时触发。
        // 这时「从差的里面挑一份相对最好的」永远比「什么都不给」有用。
        var first = Build("a.srt", (1, 1.8, "english only"), (2, 2.8, "english again"));
        var second = Build("b.srt", (1, 1.8, "another english"), (2, 2.8, "nothing here"));

        var result = SubtitleRanker.DropUnusable([first, second]);

        Assert.Equal(2, result.Kept.Count);
        Assert.Empty(result.Dropped);
    }

    /// <summary>把「(开始秒, 结束秒, 台词)」拼成一份 SRT。</summary>
    private static SubtitlePick Build(
        string name,
        params (double Start, double End, string Text)[] cues)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < cues.Length; i++)
        {
            var (start, end, text) = cues[i];
            builder.Append(i + 1).Append('\n')
                .Append(Timestamp(start)).Append(" --> ").Append(Timestamp(end)).Append('\n')
                .Append(text).Append("\n\n");
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
