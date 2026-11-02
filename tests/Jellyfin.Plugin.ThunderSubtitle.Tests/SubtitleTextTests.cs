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
