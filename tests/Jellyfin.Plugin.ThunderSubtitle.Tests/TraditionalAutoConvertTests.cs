using System;
using System.Collections.Generic;
using System.Text;
using Jellyfin.Plugin.ThunderSubtitle.Core;
using Jellyfin.Plugin.ThunderSubtitle.Providers;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

/// <summary>
/// 「自动补全时自动繁转简」与 id 字形前缀。
///
/// 两件事必须一起测：排序决定「选繁体那份」，id 前缀决定「浏览器知道它是繁体」
/// 与「下载时要不要转」。断在任何一环，用户看到的就是一份没转的繁体字幕。
/// </summary>
public class TraditionalAutoConvertTests
{
    private const string Simplified = "这是我们的问题吗我们说过这个问题了不是吗";
    private const string Traditional = "這是我們的問題嗎我們說過這個問題了不是嗎";

    private static SubtitlePick Pick(string name, string line, int cues = 30)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < cues; i++)
        {
            builder.Append(i + 1).Append('\n')
                .Append("00:00:").Append((i % 60).ToString("D2")).Append(",000 --> 00:00:")
                .Append((i % 60).ToString("D2")).Append(",800\n")
                .Append(line).Append("\n\n");
        }

        return new SubtitlePick(name, ".srt", builder.ToString(), "utf-8",
            new Uri("https://subtitle.v.geilijiasu.com/a/b.srt"));
    }

    [Fact]
    public void DetectIsExposedOnTheCandidate()
    {
        Assert.Equal(ChineseScript.Simplified, Pick("s.srt", Simplified).Script);
        Assert.Equal(ChineseScript.Traditional, Pick("t.srt", Traditional).Script);
    }

    [Fact]
    public void AutoConvertPicksTheBetterTraditionalAndMarksIt()
    {
        // 简体 60 分、繁体 75 分，差 15 ≥ 门槛 8：改用繁体，并标记写入时转简。
        var simplified = Pick("s.srt", Simplified);
        var traditional = Pick("t.srt", Traditional);

        var ranked = SubtitleRanker.ApplyAiVerdicts(
            [simplified, traditional],
            new Dictionary<int, AiVerdict>
            {
                [1] = new AiVerdict(1, 60, false, "机翻"),
                [2] = new AiVerdict(2, 75, false, "台配台本")
            },
            MediaKind.JapaneseAv,
            simplifiedAcceptScore: 0,
            tierOverrideGap: 8,
            autoConvertTraditional: true);

        Assert.Equal("t.srt", ranked[0].CandidateName);
        Assert.True(ranked[0].Simplify, "选中的繁体必须标记「写入时转简体」");
        Assert.False(ranked[1].Simplify, "没被选中的简体不该带转换标记");
    }

    [Fact]
    public void AutoConvertLeavesSimplifiedAloneWhenTheGapIsSmall()
    {
        // 只高 3 分，不够推翻「简体优先」。
        var simplified = Pick("s.srt", Simplified);
        var traditional = Pick("t.srt", Traditional);

        var ranked = SubtitleRanker.ApplyAiVerdicts(
            [simplified, traditional],
            new Dictionary<int, AiVerdict>
            {
                [1] = new AiVerdict(1, 72, false, "还行"),
                [2] = new AiVerdict(2, 75, false, "稍好")
            },
            MediaKind.JapaneseAv,
            tierOverrideGap: 8,
            autoConvertTraditional: true);

        Assert.Equal("s.srt", ranked[0].CandidateName);
        Assert.False(ranked[0].Simplify);
    }

    [Fact]
    public void AutoConvertIsOffByDefault()
    {
        // 不传 autoConvertTraditional 时行为不变：繁体再高也仍按档位排。
        var simplified = Pick("s.srt", Simplified);
        var traditional = Pick("t.srt", Traditional);

        var ranked = SubtitleRanker.ApplyAiVerdicts(
            [simplified, traditional],
            new Dictionary<int, AiVerdict>
            {
                [1] = new AiVerdict(1, 40, false, "机翻"),
                [2] = new AiVerdict(2, 95, false, "台配台本")
            },
            MediaKind.JapaneseAv,
            simplifiedAcceptScore: 0,
            tierOverrideGap: 8);

        Assert.Equal("s.srt", ranked[0].CandidateName);
    }

    [Fact]
    public void AutoConvertDoesNotApplyToForeignMedia()
    {
        // 非中文影视有自己的跨档择优（无转换），不该被这个开关影响。
        var bilingual = Pick("b.srt", Simplified);
        var traditional = Pick("t.srt", Traditional);

        var ranked = SubtitleRanker.ApplyAiVerdicts(
            [bilingual, traditional],
            new Dictionary<int, AiVerdict>
            {
                [1] = new AiVerdict(1, 70, false, "双语"),
                [2] = new AiVerdict(2, 95, false, "繁体")
            },
            MediaKind.Foreign,
            tierOverrideGap: 8,
            autoConvertTraditional: true);

        Assert.False(ranked[0].Simplify, "非中文影视不该被标成「写入时转简」");
    }

    [Theory]
    [InlineData(ChineseScript.Simplified, "ts1-")]
    [InlineData(ChineseScript.Traditional, "tt1-")]
    [InlineData(ChineseScript.Mixed, "tm1-")]
    public void IdCarriesTheScriptSoTheBrowserCanDrawThePill(ChineseScript script, string expectedMarker)
    {
        var id = SubtitleId.Encode(new Uri("https://x.geilijiasu.com/a.srt"), "srt", "a.srt", null, script);

        Assert.StartsWith(expectedMarker, id, StringComparison.Ordinal);
    }

    [Fact]
    public void SimplifyMarkerSwapsOnlyTheThirdCharacter()
    {
        // 前端点「简」时做的是纯字符串替换，不重新编码 base64。
        Assert.Equal("ts2-", SubtitleId.ToSimplifyMarker("ts1-"));
        Assert.Equal("tt2-", SubtitleId.ToSimplifyMarker("tt1-"));
        Assert.Equal("tm2-", SubtitleId.ToSimplifyMarker("tm1-"));
        Assert.Null(SubtitleId.ToSimplifyMarker("nt1-"));
    }

    [Fact]
    public void RoundTripsTheSimplifyFlag()
    {
        var id = SubtitleId.Encode(new Uri("https://x.geilijiasu.com/a.srt"), "srt", "a.srt", "/media/a.strm")
            .Replace("ts1-", "ts2-", StringComparison.Ordinal);

        Assert.True(SubtitleId.TryDecode(id, out var uri, out var name, out var path, out var simplify));
        Assert.NotNull(uri);
        Assert.Equal("a.srt", name);
        Assert.Equal("/media/a.strm", path);
        Assert.True(simplify);
    }

    [Fact]
    public void NormalCandidatesAreNotSimplified()
    {
        var id = SubtitleId.Encode(new Uri("https://x.geilijiasu.com/a.srt"), "srt", "a.srt", null);

        Assert.True(SubtitleId.TryDecode(id, out _, out _, out _, out var simplify));
        Assert.False(simplify);
    }

    [Theory]
    [InlineData(ChineseScript.Simplified, "ts2-")]
    [InlineData(ChineseScript.Traditional, "tt2-")]
    [InlineData(ChineseScript.Mixed, "tm2-")]
    public void EveryScriptRoundTripsTheSimplifyFlag(ChineseScript script, string expectedMarker)
    {
        // 回归测试。曾经「是否要转简」是 StartsWith("ts2-")，只有简体那一支能命中，
        // 于是 tt2- / tm2- 解码出来 simplify=false，「点了简、下载到的还是繁体」。
        // 而真实场景里需要转简的恰恰全是这两种 —— 简体根本不需要转。
        var plain = SubtitleId.Encode(new Uri("https://x.geilijiasu.com/a.srt"), "srt", "a.srt", null, script);
        var marker = SubtitleId.ToSimplifyMarker(plain[..4]);

        Assert.Equal(expectedMarker, marker);

        var id = marker + plain[4..];
        Assert.True(SubtitleId.TryDecode(id, out _, out var name, out _, out var simplify));
        Assert.Equal("a.srt", name);
        Assert.True(simplify);
    }

    [Fact]
    public void MixedScriptAlsoGetsAnIdThatAsksForConversion()
    {
        // 用户要求「部分是繁体部分简体也要触发」。
        var id = SubtitleId.Encode(new Uri("https://x.geilijiasu.com/a.srt"), "srt", "a.srt", null, ChineseScript.Mixed);

        Assert.StartsWith("tm1-", id, StringComparison.Ordinal);
        Assert.Equal("tm2-", SubtitleId.ToSimplifyMarker(id[..4]));
    }
}
