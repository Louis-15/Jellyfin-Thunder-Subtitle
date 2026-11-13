using Jellyfin.Plugin.ThunderSubtitle.Core;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

public class TitleMatchTests
{
    [Theory]
    [InlineData("Inception.2010.1080p.BluRay.x264.srt", "Inception", 2010, TitleMatchLevel.Title)]
    [InlineData("The.Matrix.1999.BluRay.1080p.srt", "The Matrix", 1999, TitleMatchLevel.Title)]
    [InlineData("[盗梦空间-花絮].Inception.2010.720p.chn.srt", "Inception", 2010, TitleMatchLevel.Title)]
    [InlineData("流浪地球.srt", "流浪地球", 2019, TitleMatchLevel.Title)]
    [InlineData("The.Wandering.Earth.2019.CHINESE.1080p.srt", "流浪地球", 2019, TitleMatchLevel.Year)]
    [InlineData("Some.Other.Movie.2015.srt", "Inception", 2010, TitleMatchLevel.None)]
    public void MovieEvidence(string candidate, string title, int year, TitleMatchLevel expected)
    {
        var target = SearchTarget.ForTitle(title, year);

        Assert.NotNull(target);
        Assert.Equal(expected, TitleMatch.Level(candidate, target));
    }

    [Theory]
    [InlineData("Breaking.Bad.S01E02.720p.srt", 1, 2, true)]
    [InlineData("breaking bad s01 e02 hdtv.srt", 1, 2, true)]
    [InlineData("Breaking Bad.1x02.WEB-DL.srt", 1, 2, true)]
    [InlineData("Breaking.Bad.S01E03.720p.srt", 1, 2, false)]
    public void EpisodeEvidence(string candidate, int season, int episode, bool expected)
    {
        var target = SearchTarget.ForTitle("Breaking Bad", year: null, season: season, episode: episode);

        Assert.NotNull(target);
        var level = TitleMatch.Level(candidate, target);
        Assert.Equal(expected ? TitleMatchLevel.Title : TitleMatchLevel.None, level);
    }

    [Fact]
    public void ChineseSeriesTitleMatchesEnglishCandidateByEpisodeTag()
    {
        // 实测：用中文剧名查，返回的多半是英文命名的候选。
        var target = SearchTarget.ForTitle("权力的游戏", year: null, season: 1, episode: 1);

        Assert.NotNull(target);
        Assert.Equal(TitleMatchLevel.Title, TitleMatch.Level("Game.of.Thrones.S01E01.1080p.BluRay.srt", target));
    }

    [Fact]
    public void CodeTargetsNeverProduceTitleEvidence()
    {
        var target = SearchTarget.ForCode("ABF-363");

        Assert.Equal(TitleMatchLevel.None, TitleMatch.Level("ABF-363.srt", target));
    }

    [Fact]
    public void NormalizeRemovesTagsAndSeparators()
    {
        Assert.Equal(
            "the shawshank redemption 1994 chs",
            TitleMatch.Normalize("[肖申克的救赎].The.Shawshank.Redemption.1994.Chs.srt"));
    }

    [Theory]
    [InlineData("Inception", 2010, "Inception 2010")]
    [InlineData("Inception", null, "Inception")]
    [InlineData("[Group] The Matrix", 1999, "The Matrix 1999")]
    public void QueryIncludesYearOnlyWhenKnown(string title, int? year, string expected)
    {
        var target = SearchTarget.ForTitle(title, year);

        Assert.NotNull(target);
        Assert.Equal(expected, target.Query);
    }

    [Fact]
    public void EmptyTitleProducesNoTarget()
    {
        Assert.Null(SearchTarget.ForTitle("   "));
        Assert.Null(SearchTarget.ForTitle(null));
    }
}
