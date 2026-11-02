using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.ThunderSubtitle.Core;
using Jellyfin.Plugin.ThunderSubtitle.Providers;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

/// <summary>
/// The rule that decides what the model compares, and the plain-text preview the built-in
/// search dialog shows. Both exist because the ranking cannot tell two files apart that are
/// the same source re-translated.
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

    private static SubtitlePick Numbered(int cues)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < cues; i++)
        {
            builder.Append(i + 1).Append('\n')
                .Append("00:00:01,000 --> 00:00:02,000\n")
                .Append("第").Append(i).Append("句台词\n");
        }

        return new SubtitlePick("numbered", ".srt", builder.ToString(), "utf-8");
    }

    [Fact]
    public void ShortlistIsTheTopNWhenCueCountsDiffer()
    {
        var ranked = new[] { Pick("a", 900, 20), Pick("b", 800, 20), Pick("c", 700, 20), Pick("d", 600, 20) };

        var shortlist = SubtitleFinder.SelectShortlist(ranked, 2);

        Assert.Equal(new[] { "a", "b" }, shortlist.Select(pick => pick.CandidateName));
    }

    [Fact]
    public void ShortlistExpandsToEveryCandidateTiedOnCueCount()
    {
        var ranked = new[] { Pick("a", 900, 20), Pick("b", 900, 14), Pick("c", 900, 10), Pick("d", 500, 40) };

        var shortlist = SubtitleFinder.SelectShortlist(ranked, 2);

        Assert.Equal(3, shortlist.Count);
        Assert.Equal(new[] { "a", "b", "c" }, shortlist.Select(pick => pick.CandidateName));
    }

    [Fact]
    public void TieExpansionIsCapped()
    {
        var ranked = Enumerable.Range(0, 12).Select(i => Pick("p" + i, 900, 20 - i)).ToArray();

        var shortlist = SubtitleFinder.SelectShortlist(ranked, 3);

        Assert.Equal(SubtitleFinder.MaxJudgedCandidates, shortlist.Count);
    }

    [Fact]
    public void ZeroCueCountsDoNotExpandTheShortlist()
    {
        var ranked = new[]
        {
            new SubtitlePick("junk1", ".srt", "不是字幕", "utf-8"),
            new SubtitlePick("junk2", ".srt", "也不是字幕", "utf-8"),
            new SubtitlePick("junk3", ".srt", "还不是字幕", "utf-8")
        };

        var shortlist = SubtitleFinder.SelectShortlist(ranked, 2);

        Assert.Equal(2, shortlist.Count);
    }

    [Fact]
    public void EmptyRankingProducesNoShortlist()
    {
        Assert.Empty(SubtitleFinder.SelectShortlist(Array.Empty<SubtitlePick>(), 3));
    }

    [Fact]
    public void PreviewSamplesTheMiddleOfTheFileWithoutTimestamps()
    {
        var preview = ThunderSubtitleProvider.BuildPreview(Numbered(100), 3);

        var lines = preview.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Contains("第25句台词", preview);
        Assert.DoesNotContain("第0句台词", preview);
        Assert.DoesNotContain("00:00:01", preview);
    }

    [Fact]
    public void PreviewCanBeTurnedOff()
    {
        Assert.Empty(ThunderSubtitleProvider.BuildPreview(Numbered(50), 0));
    }

    [Fact]
    public void PreviewCollapsesMultiLineCues()
    {
        const string twoLines = "1\n00:00:01,000 --> 00:00:02,000\n上半句\n下半句\n";

        var preview = ThunderSubtitleProvider.BuildPreview(
            new SubtitlePick("a.srt", ".srt", twoLines, "utf-8"),
            4);

        Assert.Equal("· 上半句 下半句", preview);
    }

    [Fact]
    public void PreviewIsEmptyForUnreadableText()
    {
        Assert.Empty(ThunderSubtitleProvider.BuildPreview(new SubtitlePick("x", ".srt", "不是字幕", "utf-8"), 6));
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
