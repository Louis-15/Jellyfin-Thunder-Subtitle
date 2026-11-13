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
            target!);

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
            target!);

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
            target!);

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
            target!);

        Assert.Single(ranked);
    }
}
