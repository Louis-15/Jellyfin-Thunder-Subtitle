using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.ThunderSubtitle.Core;
using Jellyfin.Plugin.ThunderSubtitle.Providers;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

/// <summary>
/// The rule that decides what the model compares. It exists because the ranking cannot tell two
/// files apart that are the same source re-translated.
/// </summary>
public class SubtitleShortlistTests
{
    private static SubtitlePick Pick(string name, int cues, int charsPerCue = 10)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < cues; i++)
        {
            builder.Append(i + 1).Append('\n')
                .Append("00:00:01,000 --> 00:00:02,000\n")
                .Append('好').Append(new string('看', Math.Max(0, charsPerCue - 1))).Append('\n');
        }

        return new SubtitlePick(name, ".srt", builder.ToString(), "utf-8", new Uri("https://subtitle.v.geilijiasu.com/a/b.srt"));
    }

    [Fact]
    public void PicksTwoFromEachOfTheTopThreeVolumeTiers()
    {
        // 用户的实际场景：条数前三档分别是 500 / 480 / 420，每档 3 份。
        // 期望每档出 2 份、共 6 份 —— 而不是只看条数最多的那两三份。
        var ranked = new[]
        {
            Pick("a1", 500, 20), Pick("a2", 500, 18), Pick("a3", 500, 10),
            Pick("b1", 480, 20), Pick("b2", 480, 18), Pick("b3", 480, 10),
            Pick("c1", 420, 20), Pick("c2", 420, 18), Pick("c3", 420, 10)
        };

        var shortlist = SubtitleFinder.SelectShortlist(ranked, 2);

        Assert.Equal(
            new[] { "a1", "a2", "b1", "b2", "c1", "c2" },
            shortlist.Select(pick => pick.CandidateName));
    }

    [Fact]
    public void CueCountsWithinTwoPercentShareOneTier()
    {
        // 504 与 500 差 0.8%，算同一档；480 差 4.8%，自己成一档。
        var ranked = new[] { Pick("x", 504, 20), Pick("y", 500, 18), Pick("z", 480, 20) };

        var shortlist = SubtitleFinder.SelectShortlist(ranked, 2);

        Assert.Equal(new[] { "x", "y", "z" }, shortlist.Select(pick => pick.CandidateName));
    }

    [Fact]
    public void CandidatesBeyondTheThirdTierAreDropped()
    {
        var ranked = new[]
        {
            Pick("a", 900, 20), Pick("b", 800, 20),
            Pick("c", 700, 20), Pick("d", 600, 20),
            Pick("e", 500, 20)
        };

        var shortlist = SubtitleFinder.SelectShortlist(ranked, 2);

        Assert.Equal(new[] { "a", "b", "c" }, shortlist.Select(pick => pick.CandidateName));
    }

    [Fact]
    public void ShortlistNeverExceedsTheJudgedCap()
    {
        // 4 档 × 3 份、每档要 3 份 —— 必须被 MaxJudgedCandidates 截住。
        var ranked = Enumerable.Range(0, 12)
            .Select(i => Pick("p" + i, 900 - ((i / 3) * 100), 20))
            .ToArray();

        var shortlist = SubtitleFinder.SelectShortlist(ranked, 3);

        Assert.Equal(SubtitleFinder.MaxJudgedCandidates, shortlist.Count);
    }

    [Fact]
    public void ZeroCueCountsNeverJoinARealTier()
    {
        // 条数为 0（内容根本不是字幕）不参与模糊匹配，自己单独一档。
        var ranked = new[]
        {
            Pick("good", 500, 20),
            new SubtitlePick("junk", ".srt", "不是字幕", "utf-8")
        };

        var shortlist = SubtitleFinder.SelectShortlist(ranked, 2);

        Assert.Equal(new[] { "good", "junk" }, shortlist.Select(pick => pick.CandidateName));
    }

    [Fact]
    public void EmptyRankingProducesNoShortlist()
    {
        Assert.Empty(SubtitleFinder.SelectShortlist(Array.Empty<SubtitlePick>(), 2));
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
}
