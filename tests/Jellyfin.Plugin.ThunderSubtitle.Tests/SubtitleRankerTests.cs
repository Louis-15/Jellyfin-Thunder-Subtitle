using Jellyfin.Plugin.ThunderSubtitle.Core;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

public class SubtitleRankerTests
{
    private static SubtitlePick Pick(string name, string text, string extension = ".srt")
    {
        return new SubtitlePick(name, extension, text, "utf-8");
    }

    private static string Srt(params string[] lines)
    {
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            builder.Append(i + 1).Append('\n');
            builder.Append($"00:00:{i:00},000 --> 00:00:{i + 1:00},000\n");
            builder.Append(lines[i]).Append("\n\n");
        }

        return builder.ToString();
    }

    [Fact]
    public void PrefersHumanSubtitleOverMachineTranslation()
    {
        var ai = Pick("SSIS-706.[MT].ai翻译.srt", Srt(new string('甲', 100), new string('甲', 100)));
        var human = Pick("SSIS-706.海南鸡字幕.srt", Srt(new string('乙', 100)));

        var ranked = SubtitleRanker.Rank([ai, human]);

        Assert.Equal("SSIS-706.海南鸡字幕.srt", ranked[0].CandidateName);
    }

    [Fact]
    public void PrefersMoreDialogueWhenNeitherIsMachineTranslated()
    {
        var small = Pick("SSIS-706.small.srt", Srt(new string('甲', 50)));
        var big = Pick("SSIS-706.big.srt", Srt(new string('乙', 50), new string('乙', 50), new string('乙', 50)));

        var ranked = SubtitleRanker.Rank([small, big]);

        Assert.Equal("SSIS-706.big.srt", ranked[0].CandidateName);
    }

    [Fact]
    public void AssWrapperDoesNotBeatSrtWithSameDialogue()
    {
        var ass = string.Join('\n',
            "[Script Info]",
            "ScriptType: v4.00+",
            "PlayResX: 1920",
            "PlayResY: 1080",
            "WrapStyle: 0",
            "ScaledBorderAndShadow: yes",
            "YCbCr Matrix: TV.709",
            string.Empty,
            "[V4+ Styles]",
            "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding",
            "Style: Default,华文楷体,62,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,3,1,2,30,30,40,1",
            string.Empty,
            "[Events]",
            "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text",
            "Dialogue: 0,0:00:01.00,0:00:04.00,Default,,0,0,0,,{\\pos(960,900)}第一句台词",
            "Dialogue: 0,0:00:05.00,0:00:08.00,Default,,0,0,0,,第二句\\N台词",
            "Dialogue: 0,0:00:09.00,0:00:12.00,Default,,0,0,0,,第三句台词");

        var srt = Srt("第一句台词", "第二句台词", "第三句台词");

        var assPick = Pick("x.ass", ass, ".ass");
        var srtPick = Pick("x.srt", srt, ".srt");

        Assert.True(ass.Length > srt.Length, "the ASS wrapper should be bigger in bytes");
        Assert.Equal(srtPick.ContentChars, assPick.ContentChars);
        Assert.Equal(3, assPick.CueCount);
        Assert.Equal(3, srtPick.CueCount);
    }

    [Fact]
    public void DropsCrlfAndLfDuplicates()
    {
        var lf = Srt("你好", "世界");
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
        var text = Srt("你好");
        var pick = Pick("a.srt", text);

        Assert.False(SubtitleRanker.IsUpgrade(pick, new ExistingSubtitle(pick.Stats, pick.Language, SubtitleText.Hash(text), 0), SubtitleLanguage.SimplifiedChinese));
    }

    [Fact]
    public void FewerMarkersWinsRegardlessOfVolume()
    {
        var ai = Pick("x.[MT].srt", Srt(new string('甲', 400)));
        Assert.True(SubtitleRanker.IsUpgrade(ai, new ExistingSubtitle(new SubtitleStats(1, 10), SubtitleLanguage.SimplifiedChinese, null, 1), SubtitleLanguage.SimplifiedChinese));
        Assert.False(SubtitleRanker.IsUpgrade(ai, new ExistingSubtitle(new SubtitleStats(1, 10_000), SubtitleLanguage.SimplifiedChinese, null, 0), SubtitleLanguage.SimplifiedChinese));
    }

    [Fact]
    public void IgnoresTinyDialogueGains()
    {
        var text = Srt(new string('字', 1400));
        var pick = Pick("NIMA-086.srt", text);

        Assert.False(SubtitleRanker.IsUpgrade(pick, new ExistingSubtitle(new SubtitleStats(1, 1390), SubtitleLanguage.SimplifiedChinese, null, 0), SubtitleLanguage.SimplifiedChinese));
        Assert.False(SubtitleRanker.IsUpgrade(pick, new ExistingSubtitle(new SubtitleStats(1, 1350), SubtitleLanguage.SimplifiedChinese, null, 0), SubtitleLanguage.SimplifiedChinese));
        Assert.True(SubtitleRanker.IsUpgrade(pick, new ExistingSubtitle(new SubtitleStats(1, 700), SubtitleLanguage.SimplifiedChinese, null, 0), SubtitleLanguage.SimplifiedChinese));
    }

    [Fact]
    public void RejectsCandidateForAnotherCodeEvenWhenItIsBigger()
    {
        var wrongMovie = Pick("NIMA-014-resync (1).srt", Srt(new string('甲', 900)));
        var rightMovie = Pick("NIMA-086.srt", Srt(new string('乙', 400)));

        var best = SubtitleRanker.SelectBest([wrongMovie, rightMovie], "NIMA-86");

        Assert.NotNull(best);
        Assert.Equal("NIMA-086.srt", best!.CandidateName);
    }

    [Fact]
    public void KeepsHashNamedCandidatesButPrefersOneThatCarriesTheCode()
    {
        var hashed = Pick("3C8E87DC13B20B71FFDF5B43FF12A171734ECAD0.srt", Srt(new string('甲', 900)));
        var named = Pick("NIMA-086.srt", Srt(new string('乙', 400)));

        var ranked = SubtitleRanker.Rank([hashed, named], "NIMA-86");

        Assert.Equal(2, ranked.Count);
        Assert.Equal(named.CandidateName, ranked[0].CandidateName);
        Assert.Equal(hashed.CandidateName, ranked[1].CandidateName);
    }

    [Fact]
    public void UsesHashNamedCandidateWhenItIsTheOnlyOption()
    {
        var hashed = Pick("3C8E87DC13B20B71FFDF5B43FF12A171734ECAD0.srt", Srt(new string('甲', 900)));

        Assert.Equal(hashed.CandidateName, SubtitleRanker.SelectBest([hashed], "NIMA-86")!.CandidateName);
    }

    [Fact]
    public void ReturnsNullWhenEveryCandidateIsForAnotherCode()
    {
        var wrong = Pick("NIMA-014.srt", Srt("你好"));

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

    [Fact]
    public void AiVerdictsPutMachineTranslationLastAndHigherScoreFirst()
    {
        var a = Pick("biggest.srt", Srt(new string('甲', 900)));
        var b = Pick("middle.srt", Srt(new string('乙', 600)));
        var c = Pick("smaller.srt", Srt(new string('丙', 500)));

        var verdicts = new System.Collections.Generic.Dictionary<int, AiVerdict>
        {
            [1] = new AiVerdict(1, 90, true, "机翻"),
            [2] = new AiVerdict(2, 70, false, "通顺"),
            [3] = new AiVerdict(3, 85, false, "更自然")
        };

        var reordered = SubtitleRanker.ApplyAiVerdicts([a, b, c], verdicts);

        Assert.Equal("smaller.srt", reordered[0].CandidateName);
        Assert.Equal("middle.srt", reordered[1].CandidateName);
        Assert.Equal("biggest.srt", reordered[2].CandidateName);
    }

    [Fact]
    public void AiVerdictsAreIgnoredWhenIncomplete()
    {
        var a = Pick("a.srt", Srt(new string('甲', 900)));
        var b = Pick("b.srt", Srt(new string('乙', 600)));

        var verdicts = new System.Collections.Generic.Dictionary<int, AiVerdict>
        {
            [1] = new AiVerdict(1, 10, true, "只有一条")
        };

        var reordered = SubtitleRanker.ApplyAiVerdicts([a, b], verdicts);

        Assert.Equal("a.srt", reordered[0].CandidateName);
    }
}