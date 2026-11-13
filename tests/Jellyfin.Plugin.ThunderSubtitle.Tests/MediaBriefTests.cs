using System;
using Jellyfin.Plugin.ThunderSubtitle.Core;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

/// <summary>
/// 影片元数据卡：模型必须先知道「这是 1970 年代的黑帮片」，
/// 才能判断「绝绝子」这种词出不出戏。
/// </summary>
public class MediaBriefTests
{
    [Fact]
    public void FromItemCollectsTheFactsThatAffectWording()
    {
        var movie = new Movie
        {
            Name = "盗梦空间",
            OriginalTitle = "Inception",
            ProductionYear = 2010,
            Genres = ["Science Fiction", "Action"],
            ProductionLocations = ["US"],
            Overview = "一个能潜入他人梦境的窃贼接到最后一单。",
            RunTimeTicks = TimeSpan.FromMinutes(148).Ticks
        };

        var brief = MediaBriefFactory.FromItem(movie);
        var text = brief.ToPromptText();

        Assert.Equal("盗梦空间", brief.Title);
        Assert.Equal("Inception", brief.OriginalTitle);
        Assert.Equal(2010, brief.Year);
        Assert.Equal("电影", brief.ContentType);
        Assert.Equal(148, brief.RuntimeMinutes);

        Assert.Contains("影片：《盗梦空间》 Inception (2010)", text);
        Assert.Contains("Science Fiction / Action", text);
        Assert.Contains("美国", text);
        Assert.Contains("简介：一个能潜入他人梦境的窃贼", text);
    }

    [Fact]
    public void EpisodeIsLabelledAsSeries()
    {
        var episode = new Episode { Name = "第一集", ProductionYear = 2020, Genres = ["Drama"] };

        Assert.Equal("剧集", MediaBriefFactory.FromItem(episode).ContentType);
    }

    [Fact]
    public void AnimationIsLabelledSeparately()
    {
        var movie = new Movie { Name = "千与千寻", Genres = ["Animation", "Fantasy"] };

        Assert.Equal("动画", MediaBriefFactory.FromItem(movie).ContentType);
    }

    [Fact]
    public void OriginalTitleIsDroppedWhenItMatchesTheDisplayTitle()
    {
        var movie = new Movie { Name = "Dune", OriginalTitle = "Dune" };

        Assert.Null(MediaBriefFactory.FromItem(movie).OriginalTitle);
    }

    [Fact]
    public void OverviewIsTrimmedBecauseOnlyTheGistMatters()
    {
        var movie = new Movie { Name = "X", Overview = new string('长', 400) };

        var brief = MediaBriefFactory.FromItem(movie);

        Assert.NotNull(brief.Overview);
        Assert.True(brief.Overview!.Length <= MediaBrief.MaxOverviewChars + 1, $"简介有 {brief.Overview.Length} 字");
        Assert.EndsWith("…", brief.Overview);
    }

    [Fact]
    public void GenresAreCappedAtFive()
    {
        var movie = new Movie { Name = "X", Genres = ["A", "B", "C", "D", "E", "F", "G"] };

        Assert.Equal(5, MediaBriefFactory.FromItem(movie).Genres.Count);
    }

    [Fact]
    public void UnknownCountryFallsBackToTheRawCode()
    {
        var movie = new Movie { Name = "X", ProductionLocations = ["ZZ"] };

        // 未收录的地区原样输出，不影响模型判断（ZZ 比「未知」更不容易误导）。
        Assert.Contains("ZZ", MediaBriefFactory.FromItem(movie).ToPromptText());
    }

    [Fact]
    public void EmptyBriefProducesNoPromptText()
    {
        Assert.Equal(string.Empty, MediaBrief.Empty.ToPromptText());
        Assert.Equal(string.Empty, MediaBriefFactory.FromItem(null).ToPromptText());
    }
}
