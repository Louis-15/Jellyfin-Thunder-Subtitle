using Jellyfin.Plugin.ThunderSubtitle.Core;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

public class SubtitleContentTests
{
    private const string SrtText =
        "1\n00:00:01,000 --> 00:00:03,000\n<i>看 这是今年的新人</i>\n\n" +
        "2\n00:00:04,000 --> 00:00:06,000\n从美国回来的\n\n";

    private static readonly string AssText = string.Join('\n',
        "[Script Info]",
        "ScriptType: v4.00+",
        string.Empty,
        "[V4+ Styles]",
        "Format: Name, Fontname, Fontsize, PrimaryColour, OutlineColour, BackColour, Bold, Italic, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding",
        "Style: Default,华文楷体,62,&H00FFFFFF,&H00000000,&H00000000,0,0,1,3,1,2,30,30,40,1",
        string.Empty,
        "[Events]",
        "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text",
        "Dialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,{\\pos(960,900)}看 这是今年的新人",
        "Dialogue: 0,0:00:04.00,0:00:06.00,Default,,0,0,0,,从美国\\N回来的");

    [Fact]
    public void ParsesSrtCues()
    {
        var cues = SubtitleContent.Parse(SrtText, ".srt");

        Assert.Equal(2, cues.Count);
        Assert.Equal("看 这是今年的新人", cues[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(1), cues[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(6), cues[1].End);
    }

    [Fact]
    public void ParsesAssCuesAndDropsOverrideTags()
    {
        var cues = SubtitleContent.Parse(AssText, ".ass");

        Assert.Equal(2, cues.Count);
        Assert.Equal("看 这是今年的新人", cues[0].Text.Trim());
        Assert.Equal("从美国 回来的", cues[1].Text.Trim());
    }

    [Fact]
    public void ParsesVttCuesAndSkipsHeader()
    {
        var vtt = "WEBVTT\n\nNOTE a comment\n\n00:00:01.000 --> 00:00:03.000 align:start\n你好\n";

        var cues = SubtitleContent.Parse(vtt, ".vtt");

        Assert.Single(cues);
        Assert.Equal("你好", cues[0].Text.Trim());
    }

    [Fact]
    public void CountsOnlyDialogueCharactersNotMarkup()
    {
        var srt = SubtitleContent.Measure(SrtText, ".srt");
        var ass = SubtitleContent.Measure(AssText, ".ass");

        // 看这是今年的新人 (8) + 从美国回来的 (6)
        Assert.Equal(14, srt.ContentChars);
        Assert.Equal(14, ass.ContentChars);
        Assert.Equal(2, srt.CueCount);
        Assert.Equal(2, ass.CueCount);
    }

    [Fact]
    public void AssHeaderIsNotCountedAsContent()
    {
        var withHugeHeader = "[Script Info]\n" + new string('X', 5000) + "\n\n[Events]\n"
                             + "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"
                             + "Dialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,你好\n";

        var stats = SubtitleContent.Measure(withHugeHeader, ".ass");

        Assert.Equal(2, stats.ContentChars);
        Assert.Equal(1, stats.CueCount);
    }

    [Fact]
    public void UnparseableTextFallsBackToVisibleCharacters()
    {
        var stats = SubtitleContent.Measure("纯文本没有时间轴", ".srt");

        Assert.Equal(0, stats.CueCount);
        Assert.Equal(8, stats.ContentChars);
    }

    [Fact]
    public void StripMarkupRemovesTagsAndBreaks()
    {
        Assert.Equal("你好 世界", SubtitleContent.StripMarkup("{\\an8}你好\\N世界").Trim());
        Assert.Equal("abc", SubtitleContent.StripMarkup("<font color=\"#fff\">abc</font>"));
    }

    [Fact]
    public void EmptyTextHasNoContent()
    {
        Assert.Equal(SubtitleStats.Empty, SubtitleContent.Measure(string.Empty, ".srt"));
    }
}
