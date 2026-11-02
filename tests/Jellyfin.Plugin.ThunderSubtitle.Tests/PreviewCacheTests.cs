using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ThunderSubtitle.Core;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

/// <summary>
/// The preview page keeps whole subtitles in memory and only drops them when the user picks one,
/// so the bookkeeping is what keeps the server from hoarding a copy of every movie.
/// </summary>
public class PreviewCacheTests
{
    private const string Srt = "1\n00:00:01,000 --> 00:00:02,000\n看 这是今年的新人\n";

    private static FindOutcome Outcome(int candidates = 3)
    {
        var picks = Enumerable.Range(1, candidates)
            .Select(i => new SubtitlePick($"c{i}.srt", ".srt", Srt, "utf-8", new Uri($"https://subtitle.v.geilijiasu.com/a/{i}.srt")))
            .ToArray();

        return new FindOutcome(Array.Empty<ThunderCandidate>(), picks.Length, picks, null, new Dictionary<SubtitleLanguage, int>(), 0, 0.1);
    }

    [Fact]
    public void SessionKeepsTheCandidatesAndServesThemByPosition()
    {
        var cache = new PreviewCache();

        var session = cache.Add("ABF-363", null, "/media/strm/ABF-363.strm", Outcome());

        Assert.Same(session, cache.Get(session.Key));
        Assert.Equal("c1.srt", session.Pick(1)!.CandidateName);
        Assert.Equal("c3.srt", session.Pick(3)!.CandidateName);
        Assert.Null(session.Pick(0));
        Assert.Null(session.Pick(4));
    }

    [Fact]
    public void SearchingTheSameMovieAgainReplacesTheOldCopy()
    {
        var cache = new PreviewCache();

        var first = cache.Add("ABF-363", null, "/media/strm/ABF-363.strm", Outcome());
        var second = cache.Add("abf-363", null, "/media/strm/ABF-363.strm", Outcome());

        Assert.Null(cache.Get(first.Key));
        Assert.NotNull(cache.Get(second.Key));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void PurgeDropsEverySearchForTheMovie()
    {
        var cache = new PreviewCache();
        var movie = cache.Add("ABF-363", null, "/media/strm/ABF-363.strm", Outcome());
        var other = cache.Add("SSIS-706", null, "/media/strm/SSIS-706.strm", Outcome());

        var purged = cache.Purge("ABF-363", null);

        Assert.Equal(1, purged);
        Assert.Null(cache.Get(movie.Key));
        Assert.NotNull(cache.Get(other.Key));
    }

    [Fact]
    public void PurgeAlsoMatchesOnTheMediaPath()
    {
        var cache = new PreviewCache();
        var session = cache.Add("ABF-363", null, "/media/strm/ABF-363.strm", Outcome());

        Assert.Equal(1, cache.Purge(null, "/media/strm/ABF-363.strm"));
        Assert.Null(cache.Get(session.Key));
    }

    [Fact]
    public void UnknownOrEmptyKeysResolveToNothing()
    {
        var cache = new PreviewCache();
        cache.Add("ABF-363", null, null, Outcome());

        Assert.Null(cache.Get(null));
        Assert.Null(cache.Get(string.Empty));
        Assert.Null(cache.Get("nope"));
    }

    [Fact]
    public void ExpiredSessionsAreGone()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new PreviewCache(() => now, 20);
        var session = cache.Add("ABF-363", null, null, Outcome());

        Assert.NotNull(cache.Get(session.Key));

        now = now.Add(PreviewCache.Lifetime).Add(TimeSpan.FromMinutes(1));

        Assert.Null(cache.Get(session.Key));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void OldestSessionsAreDroppedFirstWhenTheCacheIsFull()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new PreviewCache(() => now, maxSessions: 2);

        var first = cache.Add("A-1", null, null, Outcome());
        now = now.AddSeconds(5);
        var second = cache.Add("B-2", null, null, Outcome());
        now = now.AddSeconds(5);
        var third = cache.Add("C-3", null, null, Outcome());

        Assert.Equal(2, cache.Count);
        Assert.Null(cache.Get(first.Key));
        Assert.NotNull(cache.Get(second.Key));
        Assert.NotNull(cache.Get(third.Key));
    }

    [Fact]
    public void ClearEmptiesEverything()
    {
        var cache = new PreviewCache();
        cache.Add("A-1", null, null, Outcome());
        cache.Add("B-2", null, null, Outcome());

        Assert.Equal(2, cache.Clear());
        Assert.Equal(0, cache.Count);
    }
}
