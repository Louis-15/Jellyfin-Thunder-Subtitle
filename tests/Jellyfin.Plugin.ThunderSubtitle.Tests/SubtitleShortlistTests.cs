using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.ThunderSubtitle.Core;
using Jellyfin.Plugin.ThunderSubtitle.Providers;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

/// <summary>
/// 送审名额的分配规则。
///
/// 1.10 起这里只做一件事：按语言档位分组，组内保持排序链给出的质量顺序取前 N 份。
///
/// 旧版还有一层「按条数分档、逐档轮流取代表」的逻辑，已经整个删掉：
/// 它的初衷是防止名额被同一份机翻的几个转码占满，而那个目的现在由
/// <see cref="NearDuplicate"/>（合并转码版本）和
/// <see cref="SubtitleRanker.DropUnusable"/>（淘汰有硬伤的候选）承担，
/// 比「按条数分档」可靠得多。反过来，「按条数分档」本身跟字幕质量无关，
/// 实测会把排在前面的好字幕挤出送审名单。
/// </summary>
public class SubtitleShortlistTests
{
    /// <summary>一句足够长的简体台词，保证语言检测能认出是中文。</summary>
    private const string SimplifiedSample = "这是我们说过的问题";

    /// <summary>同一句的繁体写法，用来构造「繁体档位」的候选。</summary>
    private const string TraditionalSample = "這是我們說過的問題";

    [Fact]
    public void KeepsTheQualityOrderWithinOneLanguageTier()
    {
        // 一个档位里有多份候选时，取排序链排在最前面的那几份，
        // 不再按条数分组轮流挑 —— 排序链已经把质量排好了，这里不该打乱它。
        var ranked = new[]
        {
            Pick("a1", 500, 20), Pick("a2", 500, 18), Pick("a3", 500, 10),
            Pick("b1", 480, 20), Pick("b2", 480, 18), Pick("b3", 480, 10),
            Pick("c1", 420, 20), Pick("c2", 420, 18), Pick("c3", 420, 10)
        };

        var shortlist = ShortlistSelector.Select(ranked, MediaKind.JapaneseAv, 2);

        // 全是简体（同一个档位）：第一轮取 2 份，第二轮把余量补到单档上限 6 份。
        Assert.Equal(
            new[] { "a1", "a2", "a3", "b1", "b2", "b3" },
            shortlist.Select(pick => pick.CandidateName));
    }

    [Fact]
    public void SurplusQuotaGoesToTheTierThatStillHasRoom()
    {
        // 简体只有 2 份、繁体有 10 份，请求每档 5 份：
        // 简体拿完 2 份就没了，余量应当让给繁体（最多 6 份），而不是白白浪费总名额。
        // 这条规则的价值在于：纯中文台本往往只有一两份，如果余量不给其他档位，
        // 模型就只看到两三份候选，横向比较失去意义。
        var ranked = new List<SubtitlePick> { Pick("s1", 500, 20), Pick("s2", 480, 20) };
        ranked.AddRange(Enumerable.Range(0, 10)
            .Select(i => Pick("t" + i, 1600 - (i * 10), 20, TraditionalSample)));

        var shortlist = ShortlistSelector.Select(ranked, MediaKind.JapaneseAv, 5);

        Assert.Equal(8, shortlist.Count);
        Assert.Equal(2, shortlist.Count(pick => pick.Language == SubtitleLanguage.SimplifiedChinese));
        Assert.Equal(6, shortlist.Count(pick => pick.Language == SubtitleLanguage.TraditionalChinese));
    }

    [Fact]
    public void OneTierNeverTakesMoreThanItsCap()
    {
        // 只有一种语言档位、候选有 40 份：单档最多送 6 份，
        // 免得「首选项有一大堆」时把 12 个总名额全压在同一个语言上。
        var ranked = Enumerable.Range(0, 40)
            .Select(i => Pick("p" + i, 2000 - (i * 40), 20))
            .ToArray();

        var shortlist = ShortlistSelector.Select(ranked, MediaKind.JapaneseAv, 5);

        Assert.True(shortlist.Count <= ShortlistSelector.MaxJudgedCandidates, "shortlist 超过总上限");
        Assert.Equal(ShortlistSelector.MaximumPerLanguageTier, shortlist.Count);
    }

    [Fact]
    public void TwoTiersFillTheJudgedCapExactly()
    {
        // 两个档位各有 8 份、每档请求 6 份：正好占满 12 份总名额，一份不多。
        var ranked = new List<SubtitlePick>();
        ranked.AddRange(Enumerable.Range(0, 8).Select(i => Pick("s" + i, 1600 - i, 20)));
        ranked.AddRange(Enumerable.Range(0, 8).Select(i => Pick("t" + i, 1600 - i, 20, TraditionalSample)));

        var shortlist = ShortlistSelector.Select(ranked, MediaKind.JapaneseAv, 6);

        Assert.Equal(ShortlistSelector.MaxJudgedCandidates, shortlist.Count);
        Assert.Equal(6, shortlist.Count(pick => pick.Language == SubtitleLanguage.SimplifiedChinese));
        Assert.Equal(6, shortlist.Count(pick => pick.Language == SubtitleLanguage.TraditionalChinese));
    }

    [Fact]
    public void UnknownLanguageCandidatesAreExcluded()
    {
        // 条数为 0 / 内容不是中文的候选在这里就被排除了：
        // 它连「能读的字幕」都不是，没有送审的意义。
        var ranked = new[]
        {
            Pick("good", 500, 20),
            new SubtitlePick("junk", ".srt", "不是字幕", "utf-8")
        };

        var shortlist = ShortlistSelector.Select(ranked, MediaKind.JapaneseAv, 2);

        var only = Assert.Single(shortlist);
        Assert.Equal("good", only.CandidateName);
    }

    [Fact]
    public void FillStopsWhenCandidatesRunOut()
    {
        var ranked = new[] { Pick("a", 1910, 20), Pick("b", 1434, 20), Pick("c", 928, 20) };

        var shortlist = ShortlistSelector.Select(ranked, MediaKind.JapaneseAv, 2);

        Assert.Equal(3, shortlist.Count);
    }

    [Fact]
    public void EmptyRankingProducesNoShortlist()
    {
        Assert.Empty(ShortlistSelector.Select(Array.Empty<SubtitlePick>(), MediaKind.JapaneseAv, 2));
    }

    [Fact]
    public void VerdictFollowsTheCandidateAfterTheAiReordersTheList()
    {
        var first = Pick("a", 900, 20);
        var second = Pick("b", 900, 10);
        var verdicts = new Dictionary<int, AiVerdict>
        {
            [1] = new AiVerdict(1, 20, true, "直译"),
            [2] = new AiVerdict(2, 90, false, "通顺")
        };

        var outcome = new FindOutcome(
            Array.Empty<ThunderCandidate>(),
            0,
            [second, first],
            verdicts,
            new Dictionary<SubtitleLanguage, int>(),
            0,
            0.1)
        {
            Shortlist = [first, second]
        };

        Assert.Equal(90, outcome.VerdictOf(second)!.Score);
        Assert.Equal(20, outcome.VerdictOf(first)!.Score);
        Assert.Null(outcome.VerdictOf(Pick("c", 10)));
    }

    /// <summary>
    /// 构造一份候选。
    ///
    /// 时间轴固定成「每条都在第 1 秒到第 2 秒」：这样全部 cue 首尾相接、
    /// 没有任何空白，健康度扫描结果必然是「无缺陷」——
    /// 这些测试只想验证名额分配，不希望被健康度淘汰干扰。
    /// </summary>
    /// <param name="name">候选名。</param>
    /// <param name="cues">台词条数。</param>
    /// <param name="charsPerCue">每条台词的汉字数。</param>
    /// <param name="sample">用简体还是繁体写台词。</param>
    /// <returns>构造好的候选。</returns>
    private static SubtitlePick Pick(
        string name,
        int cues,
        int charsPerCue = 10,
        string sample = SimplifiedSample)
    {
        var body = charsPerCue >= sample.Length
            ? sample + new string(sample[^1], charsPerCue - sample.Length)
            : sample[..charsPerCue];

        var builder = new StringBuilder();
        for (var i = 0; i < cues; i++)
        {
            builder.Append(i + 1).Append('\n')
                .Append("00:00:01,000 --> 00:00:02,000\n")
                .Append(body).Append('\n');
        }

        return new SubtitlePick(name, ".srt", builder.ToString(), "utf-8", new Uri("https://subtitle.v.geilijiasu.com/a/b.srt"));
    }
}
