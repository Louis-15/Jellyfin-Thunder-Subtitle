using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThunderSubtitle.Core;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

/// <summary>
/// The Xunlei endpoint answers about 40% of requests with an empty <c>data</c> array and the
/// real list on a later call, so the retry is what makes "search subtitles" reliable.
/// </summary>
public class ThunderClientRetryTests
{
    private const string EmptyBody = """{"code":0,"data":[],"result":"ok"}""";

    private const string OneCandidateBody = """
        {"code":0,"data":[{"url":"https://subtitle.v.geilijiasu.com/AA/BB/x.srt","name":"ABF-363.srt","ext":"srt"}],"result":"ok"}
        """;

    private static readonly TimeSpan NoDelay = TimeSpan.Zero;

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<string> _bodies;
        private readonly List<string?> _userAgents = [];

        public QueueHandler(params string[] bodies) => _bodies = new Queue<string>(bodies);

        public int Calls => _userAgents.Count;

        public IReadOnlyList<string?> UserAgents => _userAgents;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _userAgents.Add(request.Headers.TryGetValues("User-Agent", out var values) ? string.Join(" ", values) : null);

            var body = _bodies.Count > 0 ? _bodies.Dequeue() : EmptyBody;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static ThunderClient Client(QueueHandler handler)
    {
        return new ThunderClient(new HttpClient(handler), [NoDelay, NoDelay]);
    }

    [Fact]
    public async Task RetriesAnEmptyAnswerAndReportsTheAttemptCount()
    {
        var handler = new QueueHandler(EmptyBody, EmptyBody, OneCandidateBody);

        var search = await Client(handler).SearchAsync("ABF-363", CancellationToken.None);

        Assert.Equal(3, search.Attempts);
        Assert.Single(search.Candidates);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task StopsAfterTheFirstAnswerThatHasData()
    {
        var handler = new QueueHandler(OneCandidateBody);

        var search = await Client(handler).SearchAsync("ABF-363", CancellationToken.None);

        Assert.Equal(1, search.Attempts);
        Assert.Single(search.Candidates);
    }

    [Fact]
    public async Task GivesUpAfterEveryAttemptIsEmpty()
    {
        var handler = new QueueHandler(EmptyBody);

        var search = await Client(handler).SearchAsync("ABF-363", CancellationToken.None);

        Assert.Equal(ThunderClient.SearchAttempts, search.Attempts);
        Assert.True(search.IsEmpty);
        Assert.Equal(ThunderClient.SearchAttempts, handler.Calls);
    }

    [Fact]
    public async Task SendsABrowserUserAgent()
    {
        var handler = new QueueHandler(OneCandidateBody);

        await Client(handler).SearchAsync("ABF-363", CancellationToken.None);

        Assert.Contains("Mozilla/5.0", handler.UserAgents[0]);
    }

    [Fact]
    public async Task DoesNotRetryAFailure()
    {
        var handler = new QueueHandler("not json");

        await Assert.ThrowsAsync<ThunderException>(() => Client(handler).SearchAsync("ABF-363", CancellationToken.None));

        Assert.Equal(1, handler.Calls);
    }
}
