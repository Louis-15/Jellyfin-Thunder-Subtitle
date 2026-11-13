using System;
using Jellyfin.Plugin.ThunderSubtitle.Providers;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

/// <summary>
/// 字幕 id 的编解码。这个 id 要在 Jellyfin 的对话框里往返一次，中间会被拼前缀、
/// 被当 URL 编码、被存进 HTML 属性，所以往返一致性和容错是重点。
/// </summary>
public class SubtitleIdTests
{
    [Fact]
    public void PlaceholderIdIsRecognised()
    {
        var id = SubtitleId.EncodePlaceholder("上游暂无字幕");

        Assert.True(SubtitleId.IsPlaceholder(id));
        Assert.Equal("上游暂无字幕", SubtitleId.DecodePlaceholder(id));

        // 提示条目和真实候选的标记不同，客户端脚本据此不给它加预览按钮。
        Assert.False(SubtitleId.IsPlaceholder(SubtitleId.Encode(new Uri("https://subtitle.v.geilijiasu.com/x.srt"), ".srt", "x.srt", null)));
    }

    [Fact]
    public void SubtitleIdRoundTrips()
    {
        var uri = new Uri("https://subtitle.v.geilijiasu.com/DF/60/DF60BCC7.srt");
        var id = SubtitleId.Encode(uri, ".srt", "ABF-363.海南鸡字幕.srt", "/media/strm/日本大姐姐/ABF-363.strm");

        Assert.True(SubtitleId.TryDecode(id, out var decoded, out var name, out var mediaPath));
        Assert.Equal(uri, decoded);
        Assert.Equal("ABF-363.海南鸡字幕.srt", name);
        Assert.Equal("/media/strm/日本大姐姐/ABF-363.strm", mediaPath);

        // Jellyfin splits "<providerId>_<ourId>" with a limit of 2, so underscores are safe.
        Assert.DoesNotContain("+", id);
        Assert.DoesNotContain("/", id);
    }

    [Fact]
    public void SubtitleIdToleratesAMissingMediaPath()
    {
        var uri = new Uri("https://subtitle.v.geilijiasu.com/DF/60/DF60BCC7.srt");
        var id = SubtitleId.Encode(uri, ".srt", "ABF-363.srt", null);

        Assert.True(SubtitleId.TryDecode(id, out _, out _, out var mediaPath));
        Assert.Equal(string.Empty, mediaPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64!!")]
    [InlineData("YWJj")] // "abc", no separators
    public void SubtitleIdRejectsGarbage(string id)
    {
        Assert.False(SubtitleId.TryDecode(id, out _, out _, out _));
    }
}
