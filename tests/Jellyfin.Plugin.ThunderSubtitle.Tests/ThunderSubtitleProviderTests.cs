using System;
using System.Collections.Generic;
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

        public int Calls { get; private set; }

        public Task<FindOutcome> FindAsync(string code, CancellationToken cancellationToken)
        {
            Calls++;
            var ranked = Picks.ToArray();
            return Task.FromResult(new FindOutcome(
                ranked.Select(p => new ThunderCandidate(p.Uri!, p.CandidateName, "srt")).ToArray(),
                ranked.Length,
                ranked,
                Verdicts,
                new Dictionary<SubtitleLanguage, int>(),
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
    [InlineData("Japanese", false)]
    [InlineData("English", false)]
    public void LanguageGate(string language, bool expected)
    {
        Assert.Equal(expected, ThunderSubtitleProvider.IsChineseRequested(Request(language)));
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
    public void SubtitleIdRoundTrips()
    {
        var uri = new Uri("https://subtitle.v.geilijiasu.com/DF/60/DF60BCC7.srt");
        var id = SubtitleId.Encode(uri, ".srt", "ABF-363.海南鸡字幕.srt");

        Assert.True(SubtitleId.TryDecode(id, out var decoded, out var name));
        Assert.Equal(uri, decoded);
        Assert.Equal("ABF-363.海南鸡字幕.srt", name);

        // Jellyfin splits "<providerId>_<ourId>" with a limit of 2, so underscores are safe.
        Assert.DoesNotContain("+", id);
        Assert.DoesNotContain("/", id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64!!")]
    [InlineData("YWJj")] // "abc", no separators
    public void SubtitleIdRejectsGarbage(string id)
    {
        Assert.False(SubtitleId.TryDecode(id, out _, out _));
    }

    [Fact]
    public async Task GetSubtitlesRejectsAForeignHost()
    {
        var provider = Create(new FakeFinder());
        var id = SubtitleId.Encode(new Uri("https://evil.example.com/x.srt"), ".srt", "x.srt");

        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetSubtitles(id, CancellationToken.None));
    }
}
