using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThunderSubtitle.Core;
using Jellyfin.Plugin.ThunderSubtitle.Providers;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

public class ThunderSubtitleProviderTests
{
    private const string Srt =
        "1\n00:00:01,000 --> 00:00:03,000\n看 这是今年的新人\n\n2\n00:00:20,000 --> 00:00:23,000\n从美国回来的\n";

    private static SubtitlePick Pick(string name, Uri? uri = null)
    {
        return new SubtitlePick(name, ".srt", Srt, "utf-8", uri ?? new Uri("https://subtitle.v.geilijiasu.com/AA/BB/x.srt"));
    }

    private sealed class FakeFinder : ISubtitleFinder
    {
        public List<SubtitlePick> Picks { get; } = [];

        public Dictionary<int, AiVerdict>? Verdicts { get; set; }

        public Dictionary<SubtitleLanguage, int> Dropped { get; set; } = [];

        public int RawCandidates { get; set; }

        public int Calls { get; private set; }

        public Task<FindOutcome> FindAsync(string code, CancellationToken cancellationToken)
        {
            Calls++;
            var ranked = Picks.ToArray();
            var candidates = ranked.Length > 0
                ? ranked.Select(p => new ThunderCandidate(p.Uri!, p.CandidateName, "srt")).ToArray()
                : Enumerable.Range(0, RawCandidates)
                    .Select(i => new ThunderCandidate(new Uri($"https://subtitle.v.geilijiasu.com/x{i}.srt"), $"x{i}.srt", "srt"))
                    .ToArray();

            return Task.FromResult(new FindOutcome(
                candidates,
                candidates.Length,
                ranked,
                Verdicts,
                Dropped,
                0,
                0.1) { Shortlist = ranked });
        }
    }

    private static ThunderSubtitleProvider Create(FakeFinder finder)
    {
        return new ThunderSubtitleProvider(finder, new ThunderClient(), NullLogger<ThunderSubtitleProvider>.Instance);
    }

    private static SubtitleSearchRequest Request(string language, string? mediaPath = "/media/strm/ABF-363-U.strm")
    {
        return new SubtitleSearchRequest
        {
            Language = language,
            ContentType = VideoContentType.Movie,
            MediaPath = mediaPath,
            Name = "ABF-363 Untitled"
        };
    }

    [Fact]
    public async Task SearchReturnsCandidatesWithJellyfinMetadata()
    {
        var finder = new FakeFinder();
        finder.Picks.Add(Pick("ABF-363.srt"));
        finder.Picks.Add(Pick("ABF-363.ass"));

        var results = (await Create(finder).Search(Request("Chinese (Simplified)"), CancellationToken.None)).ToArray();

        Assert.Equal(2, results.Length);
        Assert.Equal("迅雷字幕", results[0].ProviderName);
        Assert.Equal("1. ABF-363.srt", results[0].Name);
        Assert.Equal("srt", results[0].Format);
        Assert.Equal("chi", results[0].ThreeLetterISOLanguageName);
        Assert.Contains("简体", results[0].Comment);
        Assert.Contains("字", results[0].Comment);
    }

    [Fact]
    public async Task SearchDeclinesNonChineseLanguages()
    {
        var finder = new FakeFinder();
        finder.Picks.Add(Pick("ABF-363.srt"));

        foreach (var language in new[] { "English", "Japanese", "de" })
        {
            var results = await Create(finder).Search(Request(language), CancellationToken.None);
            Assert.Empty(results);
        }

        Assert.Equal(0, finder.Calls);
    }

    [Theory]
    [InlineData("Chinese (Simplified)", true)]
    [InlineData("zh-CN", true)]
    [InlineData("chi", true)]
    [InlineData("zho", true)]
    [InlineData("und", true)]
    [InlineData("iv", true)]
    [InlineData("Japanese", false)]
    [InlineData("English", false)]
    public void LanguageGate(string language, bool expected)
    {
        Assert.Equal(expected, ThunderSubtitleProvider.IsChineseRequested(Request(language)));
    }

    [Fact]
    public void UndeterminedTwoLetterHintFallsBackToLanguageName()
    {
        var request = Request("zh-CN");
        request.TwoLetterISOLanguageName = "iv";
        Assert.True(ThunderSubtitleProvider.IsChineseRequested(request));

        request.Language = "English";
        Assert.False(ThunderSubtitleProvider.IsChineseRequested(request));
    }

    [Fact]
    public void TwoLetterHintWins()
    {
        var request = Request("Chinese");
        request.TwoLetterISOLanguageName = "ja";
        Assert.False(ThunderSubtitleProvider.IsChineseRequested(request));

        request.TwoLetterISOLanguageName = "zh";
        Assert.True(ThunderSubtitleProvider.IsChineseRequested(request));
    }

    [Fact]
    public async Task SearchReturnsNothingWhenTheNameCarriesNoCode()
    {
        var finder = new FakeFinder();
        var request = Request("Chinese (Simplified)", "/media/strm/Some English Movie 1999.strm");
        request.Name = "Some English Movie";

        Assert.Empty(await Create(finder).Search(request, CancellationToken.None));
        Assert.Equal(0, finder.Calls);
    }

    [Fact]
    public async Task SearchSurfacesAiVerdicts()
    {
        var finder = new FakeFinder();
        finder.Picks.Add(Pick("ABF-363.srt"));
        finder.Picks.Add(Pick("ABF-363.[MT].srt"));
        finder.Verdicts = new Dictionary<int, AiVerdict>
        {
            [1] = new AiVerdict(1, 90, false, "通顺"),
            [2] = new AiVerdict(2, 30, true, "直译")
        };

        var results = (await Create(finder).Search(Request("Chinese (Simplified)"), CancellationToken.None)).ToArray();

        Assert.Equal(4.5f, results[0].CommunityRating);
        Assert.False(results[0].MachineTranslated);
        Assert.True(results[1].MachineTranslated);
        Assert.Contains("AI 90 分", results[0].Comment);
        Assert.Contains("直译", results[1].Comment);
    }

    [Fact]
    public async Task SearchShowsANoticeWhenTheApiReturnsNothing()
    {
        var finder = new FakeFinder();

        var results = (await Create(finder).Search(Request("Chinese (Simplified)"), CancellationToken.None)).ToArray();

        var notice = Assert.Single(results);
        Assert.Contains("ABF-363", notice.Name);
        Assert.Contains("空数据", notice.Name);
        Assert.True(SubtitleId.IsPlaceholder(notice.Id));
        Assert.False(SubtitleId.TryDecode(notice.Id, out _, out _, out _));
    }

    [Fact]
    public async Task SearchNoticeExplainsWhenOnlyForeignSubtitlesExist()
    {
        var finder = new FakeFinder();
        finder.RawCandidates = 1;
        finder.Dropped = new Dictionary<SubtitleLanguage, int> { [SubtitleLanguage.English] = 3 };

        var results = (await Create(finder).Search(Request("Chinese (Simplified)"), CancellationToken.None)).ToArray();

        var notice = Assert.Single(results);
        Assert.Contains("1 条候选", notice.Name);
        Assert.Contains("英文 3 条", notice.Comment);
    }

    [Fact]
    public async Task GetSubtitlesRejectsANotice()
    {
        var provider = Create(new FakeFinder());
        var id = SubtitleId.EncodePlaceholder("上游接口连续 3 次都返回空数据");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetSubtitles(id, CancellationToken.None));
        Assert.Contains("请稍后重新搜索", error.Message);
    }

    [Fact]
    public void PlaceholderIdIsRecognised()
    {
        var id = SubtitleId.EncodePlaceholder("上游暂无字幕");

        Assert.True(SubtitleId.IsPlaceholder(id));
        Assert.Equal("上游暂无字幕", SubtitleId.DecodePlaceholder(id));
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

    [Fact]
    public async Task GetSubtitlesRejectsAForeignHost()
    {
        var provider = Create(new FakeFinder());
        var id = SubtitleId.Encode(new Uri("https://evil.example.com/x.srt"), ".srt", "x.srt", null);

        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetSubtitles(id, CancellationToken.None));
    }

    [Fact]
    public void SubtitleFilesFindEveryChineseVariant()
    {
        var dir = Path.Combine(Path.GetTempPath(), "thunder-subs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var media = Path.Combine(dir, "ABF-363.strm");
            File.WriteAllText(media, "http://example.com/x.mp4");

            var expected = new[]
            {
                Path.Combine(dir, "ABF-363.chi.srt"),
                Path.Combine(dir, "ABF-363.chi.ass"),
                Path.Combine(dir, "ABF-363.chi.0.srt"),
                Path.Combine(dir, "ABF-363.zh-CN.vtt")
            };
            foreach (var path in expected)
            {
                File.WriteAllText(path, "x");
            }

            foreach (var other in new[]
            {
                Path.Combine(dir, "ABF-363.en.srt"),
                Path.Combine(dir, "ABF-363.china.srt"),
                Path.Combine(dir, "OTHER-001.chi.srt")
            })
            {
                File.WriteAllText(other, "x");
            }

            var found = SubtitleFiles.FindChinese(media);

            Assert.Equal(expected.Length, found.Count);
            Assert.All(expected, path => Assert.Contains(path, found));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
