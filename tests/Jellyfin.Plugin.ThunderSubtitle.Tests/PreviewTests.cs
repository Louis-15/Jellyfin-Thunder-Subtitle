using System;
using System.Linq;
using Jellyfin.Plugin.ThunderSubtitle.Core;
using Jellyfin.Plugin.ThunderSubtitle.Providers;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

/// <summary>
/// The preview button must never write next to the video, so the whole feature rests on two
/// small pieces: the text is parked in memory with a lifetime, and the ids carry a marker the
/// injected client script can recognise.
/// </summary>
public class PreviewTests
{
    private static PreviewText Sample(string name = "a.srt")
    {
        return new PreviewText(name, "srt", "utf-8", "1\n00:00:01,000 --> 00:00:02,000\n看\n", 1, 1, SubtitleLanguage.SimplifiedChinese);
    }

    [Fact]
    public void CacheReturnsWhatWasPutIn()
    {
        var cache = new PreviewTextCache();

        cache.Put("ts1-abc", Sample());

        Assert.Equal("a.srt", cache.Get("ts1-abc")!.Name);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void CacheReturnsNullForUnknownOrEmptyKeys()
    {
        var cache = new PreviewTextCache();
        cache.Put("ts1-abc", Sample());

        Assert.Null(cache.Get(null));
        Assert.Null(cache.Get(string.Empty));
        Assert.Null(cache.Get("ts1-nope"));
    }

    [Fact]
    public void EntriesExpire()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new PreviewTextCache(() => now);

        cache.Put("ts1-abc", Sample());
        Assert.NotNull(cache.Get("ts1-abc"));

        now = now.Add(PreviewTextCache.Lifetime).Add(TimeSpan.FromMinutes(1));

        Assert.Null(cache.Get("ts1-abc"));
    }

    [Fact]
    public void OldestEntriesAreDroppedOnceTheCacheIsFull()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new PreviewTextCache(() => now, capacity: 2);

        cache.Put("ts1-a", Sample("a.srt"));
        now = now.AddSeconds(5);
        cache.Put("ts1-b", Sample("b.srt"));
        now = now.AddSeconds(5);
        cache.Put("ts1-c", Sample("c.srt"));

        Assert.Equal(2, cache.Count);
        Assert.Null(cache.Get("ts1-a"));
        Assert.NotNull(cache.Get("ts1-b"));
        Assert.NotNull(cache.Get("ts1-c"));
    }

    [Fact]
    public void ClearEmptiesTheCache()
    {
        var cache = new PreviewTextCache();
        cache.Put("ts1-a", Sample());
        cache.Put("ts1-b", Sample());

        Assert.Equal(2, cache.Clear());
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void PreviewTextReportsTheWrittenSize()
    {
        var text = Sample();

        Assert.Equal(SubtitleRanker.Utf8Length(text.Text), text.Bytes);
    }

    [Fact]
    public void CandidateIdsCarryTheMarkerTheClientScriptLooksFor()
    {
        var id = SubtitleId.Encode(new Uri("https://subtitle.v.geilijiasu.com/a/b.srt"), ".srt", "ABF-363.srt", "/media/x.strm");

        Assert.StartsWith(SubtitleId.CandidateMarker, id);
        Assert.True(SubtitleId.TryDecode(id, out var uri, out var name, out var mediaPath));
        Assert.Equal("https://subtitle.v.geilijiasu.com/a/b.srt", uri!.AbsoluteUri);
        Assert.Equal("ABF-363.srt", name);
        Assert.Equal("/media/x.strm", mediaPath);
    }

    [Fact]
    public void NoticeIdsUseADifferentMarkerSoTheyGetNoPreviewButton()
    {
        var id = SubtitleId.EncodePlaceholder("上游接口连续 5 次都返回空数据");

        Assert.StartsWith(SubtitleId.NoticeMarker, id);
        Assert.NotEqual(SubtitleId.CandidateMarker, SubtitleId.NoticeMarker);
        Assert.True(SubtitleId.IsPlaceholder(id));
        Assert.False(SubtitleId.TryDecode(id, out _, out _, out _));
    }

    [Fact]
    public void IncomingIdsLoseTheJellyfinProviderPrefix()
    {
        var id = SubtitleId.Encode(new Uri("https://subtitle.v.geilijiasu.com/a/b.srt"), ".srt", "x.srt", null);
        var prefixed = "60aeb971c2d157cc70779df28d72629e_" + id;

        Assert.Equal(id, SubtitleId.NormalizeIncoming(prefixed));
        Assert.Equal(id, SubtitleId.NormalizeIncoming(id));
        Assert.Equal(string.Empty, SubtitleId.NormalizeIncoming(null));
        Assert.Equal("nonsense", SubtitleId.NormalizeIncoming("nonsense"));
    }

    [Fact]
    public void ProviderPrefixIsStrippedEvenWhenJellyfinAlreadyDidSo()
    {
        // Jellyfin splits on the first underscore before calling the provider, so GetSubtitles can
        // see either form; both must decode.
        var id = SubtitleId.Encode(new Uri("https://subtitle.v.geilijiasu.com/a/b.srt"), ".srt", "x.srt", null);

        Assert.True(SubtitleId.TryDecode(SubtitleId.NormalizeIncoming(id), out var direct, out _, out _));
        Assert.True(SubtitleId.TryDecode(SubtitleId.NormalizeIncoming("deadbeef_" + id), out var stripped, out _, out _));
        Assert.Equal(direct, stripped);
    }

    [Fact]
    public void DecodeNameFallsBackWhenTheIdIsUnreadable()
    {
        var id = SubtitleId.Encode(new Uri("https://subtitle.v.geilijiasu.com/a/b.srt"), ".srt", "ABF-363.srt", null);

        Assert.Equal("ABF-363.srt", SubtitleId.DecodeName(id));
        Assert.Equal("字幕", SubtitleId.DecodeName("garbage"));
    }

    [Fact]
    public void PreviewTextCarriesTheStatsTheOverlayShows()
    {
        const string srt = "1\n00:00:01,000 --> 00:00:02,000\n看 这是今年的新人\n\n2\n00:00:03,000 --> 00:00:04,000\n从美国回来的\n";
        var stats = SubtitleContent.Measure(srt, ".srt");

        var preview = new PreviewText("x.srt", "srt", "utf-8", srt, stats.CueCount, stats.ContentChars, SubtitleLanguage.SimplifiedChinese);

        Assert.Equal(2, preview.CueCount);
        Assert.True(preview.ContentChars > 5);
        Assert.Equal(SubtitleLanguage.SimplifiedChinese, preview.Language);
    }
}
