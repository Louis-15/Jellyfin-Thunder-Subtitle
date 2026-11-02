using Jellyfin.Plugin.ThunderSubtitle.Core;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

public class SubtitleRankerTests
{
    private static SubtitlePick Pick(string name, string text, string extension = ".srt")
    {
        return new SubtitlePick(name, extension, text, "utf-8");
    }

    [Fact]
    public void PrefersHumanSubtitleOverBiggerMachineTranslation()
    {
        var ai = Pick("SSIS-706.[MT].ai翻译.srt", new string('a', 5000));
        var human = Pick("SSIS-706.海南鸡字幕.srt", new string('b', 4000));

        var ranked = SubtitleRanker.Rank([ai, human]);

        Assert.Equal("SSIS-706.海南鸡字幕.srt", ranked[0].CandidateName);
    }

    [Fact]
    public void PrefersBiggerSubtitleWhenNeitherIsMachineTranslated()
    {
        var small = Pick("SSIS-706.small.srt", new string('a', 1000));
        var big = Pick("SSIS-706.big.srt", new string('b', 4000));

        var ranked = SubtitleRanker.Rank([small, big]);

        Assert.Equal("SSIS-706.big.srt", ranked[0].CandidateName);
    }

    [Fact]
    public void DropsCrlfAndLfDuplicates()
    {
        var lf = "1\n00:00:01,000 --> 00:00:03,000\n你好\n";
        var crlf = lf.Replace("\n", "\r\n");

        var ranked = SubtitleRanker.Rank([Pick("a.srt", lf), Pick("b.srt", crlf)]);

        Assert.Single(ranked);
    }

    [Theory]
    [InlineData("SSIS-706.[MT].srt", 1)]
    [InlineData("SSIS-706.AI翻译.srt", 1)]
    [InlineData("SSIS-706.whisper.机翻.srt", 2)]
    [InlineData("SSIS-706.海南鸡字幕.srt", 0)]
    public void CountsMachineTranslationMarkers(string name, int expected)
    {
        Assert.Equal(expected, SubtitleRanker.CountAiMarkers(name));
    }

    [Fact]
    public void IdenticalContentIsNotAnUpgrade()
    {
        var text = "1\n00:00:01,000 --> 00:00:03,000\n你好\n";
        var pick = Pick("a.srt", text);

        Assert.False(SubtitleRanker.IsUpgrade(pick, 100, SubtitleText.Hash(text), 0));
    }

    [Fact]
    public void UnknownOriginNeedsToBeClearlyBigger()
    {
        var text = "1\n00:00:01,000 --> 00:00:03,000\n" + new string('字', 7000) + "\n";
        var pick = Pick("a.srt", text);

        Assert.False(SubtitleRanker.IsUpgrade(pick, pick.Bytes + 100, null, null));
        Assert.False(SubtitleRanker.IsUpgrade(pick, pick.Bytes - 500, null, null));
        Assert.True(SubtitleRanker.IsUpgrade(pick, pick.Bytes / 2, null, null));
    }

    [Fact]
    public void IgnoresTinySizeGains()
    {
        var text = "1\n00:00:01,000 --> 00:00:03,000\n" + new string('字', 14000) + "\n";
        var pick = Pick("NIMA-086.srt", text);

        // A re-upload that is 86 bytes longer is the same subtitle, not an upgrade.
        Assert.False(SubtitleRanker.IsUpgrade(pick, pick.Bytes - 86, null, 0));
        Assert.False(SubtitleRanker.IsUpgrade(pick, pick.Bytes - 500, null, 0));

        // A real gain is taken.
        Assert.True(SubtitleRanker.IsUpgrade(pick, pick.Bytes / 2, null, 0));
    }

    [Fact]
    public void FewerMarkersWinsOverSizeWhenOriginIsKnown()
    {
        var ai = Pick("x.[MT].srt", new string('a', 10000));
        Assert.True(SubtitleRanker.IsUpgrade(ai, 1000, null, 1));
        Assert.False(SubtitleRanker.IsUpgrade(ai, 100000, null, 0));
    }

    [Fact]
    public void RejectsCandidateForAnotherCodeEvenWhenItIsBigger()
    {
        var wrongMovie = Pick("NIMA-014-resync (1).srt", new string('a', 90000));
        var rightMovie = Pick("NIMA-086.srt", new string('b', 40000));

        var best = SubtitleRanker.SelectBest([wrongMovie, rightMovie], "NIMA-86");

        Assert.NotNull(best);
        Assert.Equal("NIMA-086.srt", best!.CandidateName);
    }

    [Fact]
    public void KeepsHashNamedCandidatesButPrefersOneThatCarriesTheCode()
    {
        var hashed = Pick("3C8E87DC13B20B71FFDF5B43FF12A171734ECAD0.srt", new string('a', 90000));
        var named = Pick("NIMA-086.srt", new string('b', 40000));

        var ranked = SubtitleRanker.Rank([hashed, named], "NIMA-86");

        // Neither is dropped, but the name that proves the release wins over a bigger hash name.
        Assert.Equal(2, ranked.Count);
        Assert.Equal(named.CandidateName, ranked[0].CandidateName);
        Assert.Equal(hashed.CandidateName, ranked[1].CandidateName);
    }

    [Fact]
    public void UsesHashNamedCandidateWhenItIsTheOnlyOption()
    {
        var hashed = Pick("3C8E87DC13B20B71FFDF5B43FF12A171734ECAD0.srt", new string('a', 90000));

        Assert.Equal(hashed.CandidateName, SubtitleRanker.SelectBest([hashed], "NIMA-86")!.CandidateName);
    }

    [Fact]
    public void ReturnsNullWhenEveryCandidateIsForAnotherCode()
    {
        var wrong = Pick("NIMA-014.srt", new string('a', 90000));

        Assert.Null(SubtitleRanker.SelectBest([wrong], "NIMA-86"));
    }

    [Fact]
    public void CodeMatchIgnoresLeadingZerosAndPrefixNoise()
    {
        Assert.True(SubtitleRanker.IsCodeOrUnknown("ABF-0363.srt", "ABF-363"));
        Assert.True(SubtitleRanker.IsCodeOrUnknown("[ThZu.Cc]ABF-363-U.zh.srt", "ABF-363"));
        Assert.True(SubtitleRanker.IsCodeOrUnknown("300MIUM-1457.zh-CN.srt", "MIUM-1457"));
        Assert.False(SubtitleRanker.IsCodeOrUnknown("ABF-364.srt", "ABF-363"));
    }
}
