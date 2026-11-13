using System.Text;
using Jellyfin.Plugin.ThunderSubtitle.Core;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

public class SubtitleTextTests
{
    private const string SrtBody = "1\n00:00:01,000 --> 00:00:03,000\n你好，世界。\n\n2\n00:00:04,000 --> 00:00:06,000\n这是第二句字幕。\n";

    [Fact]
    public void DecodesUtf8()
    {
        var decoded = SubtitleText.TryDecode(Encoding.UTF8.GetBytes(SrtBody));
        Assert.NotNull(decoded);
        Assert.Equal("utf-8", decoded!.EncodingName);
        Assert.Contains("你好", decoded.Text);
    }

    [Fact]
    public void DecodesUtf8WithBom()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(SrtBody)).ToArray();
        var decoded = SubtitleText.TryDecode(bytes);
        Assert.NotNull(decoded);
        Assert.Equal("utf-8-sig", decoded!.EncodingName);
        Assert.StartsWith("1", decoded.Text);
    }

    [Fact]
    public void DecodesGb18030()
    {
        SubtitleText.RegisterEncodings();
        var gb = Encoding.GetEncoding("gb18030");
        var bytes = gb.GetBytes(SrtBody);

        var decoded = SubtitleText.TryDecode(bytes);
        Assert.NotNull(decoded);
        Assert.Equal("gb18030", decoded!.EncodingName);
        Assert.Contains("你好", decoded.Text);
    }

    [Fact]
    public void DecodesBig5()
    {
        SubtitleText.RegisterEncodings();
        var big5 = Encoding.GetEncoding("big5");
        var traditional = "1\r\n00:00:01,000 --> 00:00:03,000\r\n繁體中文字幕測試\r\n";
        var decoded = SubtitleText.TryDecode(big5.GetBytes(traditional));

        Assert.NotNull(decoded);
        Assert.Contains("繁體", decoded!.Text);
    }

    [Theory]
    [InlineData(SrtBody, "srt")]
    [InlineData("WEBVTT\n\n00:00:01.000 --> 00:00:03.000\nhello\n", "vtt")]
    [InlineData("[Script Info]\nScriptType: v4.00+\n\n[V4+ Styles]\nFormat: Name\n\n[Events]\nFormat: Layer, Start, End\nDialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,hello\n", "ass")]
    [InlineData("[Script Info]\nScriptType: v4.00\n\n[V4 Styles]\nFormat: Name\n\n[Events]\nFormat: Layer, Start, End\nDialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,hello\n", "ssa")]
    public void DetectsFormatFromContent(string text, string expected)
    {
        Assert.Equal("." + expected, SubtitleText.DetectExtension(text, null));
    }

    [Fact]
    public void RejectsHtmlErrorPage()
    {
        var html = "<!DOCTYPE html><html><head><title>404</title></head><body>not found</body></html>";
        Assert.Null(SubtitleText.DetectExtension(html, "srt"));
    }

    [Fact]
    public void RejectsHtmlPageWhoseCommentsLookLikeSrtArrows()
    {
        // 真实案例：上游 CDN 返回字幕站的下载页（79 KB），HTML 注释里的 "-->" 与
        // SRT 的时间轴箭头一模一样，旧实现会把它当成 .ass 并参与排序。
        var html = "<!DOCTYPE html>\n<html lang=\"zh-CN\">\n<head><title>字幕库</title></head>\n"
                   + "<body><script>\nvar x = 1; <!-- 1 --> 2 --> 3;\n</script>\n"
                   + "<p>流浪地球 字幕下载</p>\n</body></html>";
        Assert.Null(SubtitleText.DetectExtension(html, "ass"));
    }

    [Fact]
    public void RejectsTruncatedAssWithoutEvents()
    {
        // 真实案例：245 KB 的 ASS 只剩 [Script Info] 和 [V4+ Styles]，
        // 没有 [Events] 也没有 Dialogue 行，解析不出任何台词。
        var truncated = "[Script Info]\nScriptType: v4.00+\nTitle: 奶飞版修复字幕\n\n"
                        + "[V4+ Styles]\nFormat: Name, Fontname, Fontsize\nStyle: Default,Arial,62\n";
        Assert.Null(SubtitleText.DetectExtension(truncated, "ass"));
    }

    [Fact]
    public void AcceptsAssWithDialogueEvenWithoutEventsHeader()
    {
        // 反向保护：有 [Script Info] 且有 Dialogue 行的文件仍然算 ASS，
        // 不能因为缺少 [Events] 标题就一律拒绝。
        var ass = "[Script Info]\nScriptType: v4.00+\n\n"
                  + "Dialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,你好\n";
        Assert.Equal(".ass", SubtitleText.DetectExtension(ass, "ass"));
    }

    [Fact]
    public void RejectsAssDanmakuExport()
    {
        // 真实案例：B 站视频的弹幕导出成 ASS 后混进字幕库，8000 条全是 \move 滚动，
        // 字符数比任何真字幕都多，不排除会直接胜出。
        var builder = new StringBuilder();
        builder.Append("[Script Info]\nScriptType: v4.00+\n\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize\n");
        builder.Append("Style: AcplayDefault,Arial,60\n\n[Events]\n");
        builder.Append("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n");
        for (var i = 0; i < 60; i++)
        {
            builder.Append($"Dialogue: 3,0:00:01.{i:D2},0:00:09.00,AcplayDefault,,0,0,0,,{{\\move(2400, 64, -480, 64)}}弹幕第{i}条\n");
        }

        Assert.Null(SubtitleText.DetectExtension(builder.ToString(), "ass"));
    }

    [Fact]
    public void AcceptsNormalAssThatUsesMoveForSigns()
    {
        // 反向保护：正常字幕偶尔用 \move 做标牌翻译，占比很低，不能误判成弹幕。
        var builder = new StringBuilder();
        builder.Append("[Script Info]\nScriptType: v4.00+\n\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize\n");
        builder.Append("Style: Default,Arial,60\n\n[Events]\n");
        builder.Append("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n");
        for (var i = 0; i < 60; i++)
        {
            var effect = i < 2 ? "{\\move(100, 100, 200, 200)}" : string.Empty;
            builder.Append($"Dialogue: 0,0:00:01.{i:D2},0:00:09.00,Default,,0,0,0,,{effect}第{i}句台词\n");
        }

        Assert.Equal(".ass", SubtitleText.DetectExtension(builder.ToString(), "ass"));
    }

    [Fact]
    public void RejectsSmi()
    {
        var smi = "<SAMI><BODY><SYNC Start=1000><P Class=KRCC>你好</SYNC></BODY></SAMI>";
        Assert.Null(SubtitleText.DetectExtension(smi, "smi"));
    }

    [Fact]
    public void CrlfAndLfCopiesHashTheSame()
    {
        var lf = SrtBody.Replace("\r\n", "\n");
        var crlf = lf.Replace("\n", "\r\n");
        Assert.Equal(SubtitleText.Hash(lf), SubtitleText.Hash(crlf));
    }
}
