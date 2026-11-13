using System.Collections.Generic;
using Jellyfin.Plugin.ThunderSubtitle.Core;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

/// <summary>AI 送审请求的组装、回复解析与 evidence 核对。</summary>
public class AiSubtitleJudgeTests
{
    private const string SrtA =
        "1\n00:00:10,000 --> 00:00:13,000\n我从来没像现在这样害怕过\n\n" +
        "2\n00:00:20,000 --> 00:00:23,000\n他不会明白的\n\n" +
        "3\n00:01:40,000 --> 00:01:43,000\n那么没头没脑的新人\n\n";

    private const string SrtB =
        "1\n00:00:10,000 --> 00:00:13,000\n我从来没有像现在一样恐惧\n\n" +
        "2\n00:00:20,000 --> 00:00:23,000\n他是不会明白的\n\n" +
        "3\n00:01:40,000 --> 00:01:43,000\n那样没头没脑的新人\n\n";

    private static SubtitlePick Pick(string name, string text) => new(name, ".srt", text, "utf-8");

    private static AiSettings Settings(int segments = 4, int cues = 2)
    {
        return new AiSettings("https://example.test/v1", "key", "test-model", segments, cues, 3000, 30);
    }

    private static MediaBrief Brief()
    {
        return new MediaBrief(
            "盗梦空间",
            "Inception",
            2010,
            "电影",
            ["Science Fiction", "Action"],
            ["US"],
            "一个能潜入他人梦境的窃贼接到最后一单。",
            148);
    }

    private static SubtitlePick[] TwoPicks() => [Pick("a.srt", SrtA), Pick("b.srt", SrtB)];

    [Fact]
    public void RequestUsesSettingsAndCarriesTheSystemPromptForTheKind()
    {
        var request = AiSubtitleJudge.BuildRequest(MediaKind.Foreign, Brief(), TwoPicks(), Settings(), null);

        Assert.NotNull(request);
        Assert.Equal("test-model", request!.Model);
        Assert.Equal(0, request.Temperature);
        Assert.Equal(2, request.Messages.Count);
        Assert.Equal("system", request.Messages[0].Role);
        Assert.Equal(AiPrompts.System(MediaKind.Foreign), request.Messages[0].Content);
    }

    [Fact]
    public void SystemPromptFollowsTheMediaKind()
    {
        var foreign = AiSubtitleJudge.BuildRequest(MediaKind.Foreign, null, TwoPicks(), Settings(), null)!;
        var chinese = AiSubtitleJudge.BuildRequest(MediaKind.Chinese, null, TwoPicks(), Settings(), null)!;

        Assert.NotEqual(foreign.Messages[0].Content, chinese.Messages[0].Content);
        Assert.Equal(AiPrompts.System(MediaKind.Chinese), chinese.Messages[0].Content);
    }

    [Fact]
    public void UserPromptStartsWithTheFilmBrief()
    {
        var user = AiSubtitleJudge.BuildRequest(MediaKind.Foreign, Brief(), TwoPicks(), Settings(), null)!
            .Messages[1].Content;

        Assert.StartsWith("影片：《盗梦空间》 Inception (2010)", user);
        Assert.Contains("类型：电影", user);
        Assert.Contains("美国", user);
        Assert.Contains("简介：一个能潜入他人梦境的窃贼", user);
    }

    [Fact]
    public void UserPromptNumbersCandidatesAndCarriesDialogueOnly()
    {
        var user = AiSubtitleJudge.BuildRequest(MediaKind.Foreign, null, TwoPicks(), Settings(), null)!
            .Messages[1].Content;

        Assert.Contains("【候选 1】", user);
        Assert.Contains("【候选 2】", user);
        Assert.Contains("我从来没像现在这样害怕过", user);
        Assert.Contains("我从来没有像现在一样恐惧", user);

        // 时间轴本身对模型没用，只会占 token。
        Assert.DoesNotContain("-->", user);
        Assert.DoesNotContain(",000", user);
    }

    [Fact]
    public void ExtraInstructionIsAppendedWithAGuardAgainstFormatDrift()
    {
        var user = AiSubtitleJudge.BuildRequest(MediaKind.Foreign, null, TwoPicks(), Settings(), "请偏向书面语")!
            .Messages[1].Content;

        Assert.Contains("请偏向书面语", user);
        Assert.Contains("不得改变输出格式", user);
    }

    [Fact]
    public void PromptIsShrunkWhenItExceedsTheBudget()
    {
        var longText = BuildLongSrt(600, "拖长台词的填充内容");
        var settings = Settings(segments: 8, cues: 8) with { MaxPromptChars = 4000 };

        var request = AiSubtitleJudge.BuildRequest(
            MediaKind.Chinese,
            null,
            [Pick("a.srt", longText), Pick("b.srt", longText)],
            settings,
            null);

        Assert.NotNull(request);
        Assert.True(
            request!.Messages[1].Content.Length <= 4000,
            $"prompt 有 {request.Messages[1].Content.Length} 字符，超过 4000 上限");
    }

    private static string BuildLongSrt(int count, string filler)
    {
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < count; i++)
        {
            var start = System.TimeSpan.FromSeconds(i * 3);
            var end = start.Add(System.TimeSpan.FromSeconds(2.5));
            builder.Append(i + 1).Append('\n')
                .Append(start.ToString(@"hh\:mm\:ss\,fff")).Append(" --> ")
                .Append(end.ToString(@"hh\:mm\:ss\,fff")).Append('\n')
                .Append(filler).Append(i).Append('\n').Append('\n');
        }

        return builder.ToString();
    }
}
