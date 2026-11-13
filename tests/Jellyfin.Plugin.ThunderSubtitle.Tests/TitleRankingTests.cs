using System.Linq;
using Jellyfin.Plugin.ThunderSubtitle.Core;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

public class TitleRankingTests
{
    private static string Dialogue(int lines)
    {
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < lines; i++)
        {
            builder.Append(i + 1).Append("\n00:00:0").Append(i % 10).Append(",000 --> 00:00:0")
                .Append(i % 10).Append(",900\n第").Append(i).Append("句台词\n\n");
        }

        return builder.ToString();
    }

    private static SubtitlePick Pick(string name, int lines)
    {
        return new SubtitlePick(name, ".srt", Dialogue(lines), "utf-8");
    }

    [Fact]
    public void TitleMatchWinsOverABiggerCandidateWithoutEvidence()
    {
        var target = SearchTarget.ForTitle("Inception", 2010);
        Assert.NotNull(target);

        var ranked = SubtitleRanker.RankForTitle(
            [Pick("Unknown.Release.2010.1080p.srt", 200), Pick("Inception.2010.BluRay.srt", 50)],
            target!,
            MediaKind.Foreign);

        Assert.Equal("Inception.2010.BluRay.srt", ranked[0].CandidateName);
    }

    [Fact]
    public void StrongerEvidenceRemovesWeakerCandidates()
    {
        var target = SearchTarget.ForTitle("Inception", 2010);
        Assert.NotNull(target);

        var ranked = SubtitleRanker.RankForTitle(
            [
                Pick("Inception.2010.BluRay.srt", 40),
                Pick("Other.Movie.2010.srt", 300),
                Pick("Another.Film.2015.srt", 300)
            ],
            target!,
            MediaKind.Foreign);

        var only = Assert.Single(ranked);
        Assert.Equal("Inception.2010.BluRay.srt", only.CandidateName);
    }

    [Fact]
    public void FallsBackToEveryCandidateWhenNothingMatches()
    {
        // 中文片名查出来常常是英文命名，硬要求片名匹配会让这类库永远选不到字幕。
        var target = SearchTarget.ForTitle("流浪地球", 2019);
        Assert.NotNull(target);

        var ranked = SubtitleRanker.RankForTitle(
            [
                Pick("The.Wandering.Earth.II.2023.srt", 100),
                Pick("Another.Movie.2024.srt", 200)
            ],
            target!,
            MediaKind.Foreign);

        Assert.Equal(2, ranked.Count);
        Assert.Equal("Another.Movie.2024.srt", ranked[0].CandidateName);
    }

    [Fact]
    public void DuplicateContentIsRankedOnce()
    {
        var target = SearchTarget.ForTitle("Inception", 2010);
        Assert.NotNull(target);

        var text = Dialogue(10);
        var ranked = SubtitleRanker.RankForTitle(
            [
                new SubtitlePick("Inception.2010.a.srt", ".srt", text, "utf-8"),
                new SubtitlePick("Inception.2010.b.srt", ".srt", text, "utf-8")
            ],
            target!,
            MediaKind.Foreign);

        Assert.Single(ranked);
    }

    [Fact]
    public void ExcludesUnrelatedYearWhenNothingMatchesTheTitle()
    {
        // 兜底：所有候选都没有片名/年份证据时，名字里明确标了别的年份的候选被排除，
        // 名字里没有年份的保留。真实场景：中文片名 + 英文候选名 + 续集年份。
        var target = SearchTarget.ForTitle("流浪地球", 2019);
        Assert.NotNull(target);

        var sequel = Pick("Completely.Different.2023.srt", 200);
        var unknown = Pick("3C8E87DC13B20B71FFDF5B43FF12A171734ECAD0.srt", 100);

        var ranked = SubtitleRanker.RankForTitle([sequel, unknown], target!, MediaKind.Chinese);

        var only = Assert.Single(ranked);
        Assert.Equal(unknown.CandidateName, only.CandidateName);
    }

    [Fact]
    public void KeepsEveryCandidateWhenAllYearsConflict()
    {
        // 宁可让续集混进来交给 AI 评审，也不要把候选集清空导致这部影片永远没有字幕。
        var target = SearchTarget.ForTitle("流浪地球", 2019);
        Assert.NotNull(target);

        var ranked = SubtitleRanker.RankForTitle(
            [Pick("Movie.A.2023.srt", 100), Pick("Movie.B.2024.srt", 200)],
            target!,
            MediaKind.Chinese);

        Assert.Equal(2, ranked.Count);
    }

    [Fact]
    public void AllowsOneYearTolerance()
    {
        // 上映年、制作年、不同地区的发行年经常差一年。
        var target = SearchTarget.ForTitle("流浪地球", 2019);
        Assert.NotNull(target);

        var near = Pick("Movie.2020.srt", 100);
        var hash = Pick("3C8E87DC13B20B71FFDF5B43FF12A171734ECAD0.srt", 80);

        var ranked = SubtitleRanker.RankForTitle([near, hash], target!, MediaKind.Chinese);

        Assert.Equal(2, ranked.Count);
    }

    [Fact]
    public void ResolutionNumbersAreNotTreatedAsYears()
    {
        // 1920x1080 里的 1920 不是年份，不能当成「明确是别的年份」。
        var target = SearchTarget.ForTitle("流浪地球", 2019);
        Assert.NotNull(target);

        var resolution = Pick("Some.Movie.1920x1080.srt", 100);
        var hash = Pick("3C8E87DC13B20B71FFDF5B43FF12A171734ECAD0.srt", 80);

        var ranked = SubtitleRanker.RankForTitle([resolution, hash], target!, MediaKind.Chinese);

        Assert.Equal(2, ranked.Count);
    }
}
