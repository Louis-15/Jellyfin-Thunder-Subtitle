using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThunderSubtitle.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

/// <summary>
/// Exercises the real HTTP path against a local stub, so the request shape, the bearer
/// header and the failure fallbacks are covered without needing an API key.
/// </summary>
public class AiJudgeHttpTests
{
    private const string SrtA = "1\n00:00:01,000 --> 00:00:03,000\n看 这是今年的新人\n\n2\n00:00:20,000 --> 00:00:23,000\n从美国回来的\n";
    private const string SrtB = "1\n00:00:01,000 --> 00:00:03,000\n看 这是今年的新的人\n\n2\n00:00:20,000 --> 00:00:23,000\n从美国回来了\n";

    private static SubtitlePick Pick(string name, string text) => new(name, ".srt", text, "utf-8");

    private static MediaBrief Brief() => new("Dune", null, 2021, "电影", [], [], null, null);

    [Fact]
    public async Task JudgesThroughARealHttpCall()
    {
        await using var stub = new StubServer(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"{\"candidates\":[{\"id\":1,\"score\":35,\"machine\":true,\"reason\":\"直译\"},{\"id\":2,\"score\":91,\"machine\":false,\"reason\":\"自然\"}]}"}}]}""");

        var judge = new AiSubtitleJudge(NullLogger<AiSubtitleJudge>.Instance);
        var settings = new AiSettings(
            stub.BaseUrl + "/v1", "secret-key", "stub-model",
            SegmentCount: 12, CuesPerSegment: 8, MaxCharsPerCandidate: 3000, TimeoutSeconds: 30,
            SessionId: "session-abc", UseSessionHeader: true);

        var verdicts = await judge.JudgeAsync(
            MediaKind.JapaneseAv,
            Brief(),
            [Pick("a.srt", SrtA), Pick("b.srt", SrtB)],
            settings,
            null,
            CancellationToken.None);

        Assert.NotNull(verdicts);
        Assert.True(verdicts![1].MachineTranslated);
        Assert.Equal(91, verdicts[2].Score);
        Assert.Equal("/v1/chat/completions", stub.LastPath);
        Assert.Equal("Bearer secret-key", stub.LastAuthorization);
        Assert.Equal("session-abc", stub.LastSessionHeader);
        Assert.Contains("\"model\":\"stub-model\"", stub.LastBody);
        Assert.Contains("\"temperature\":0", stub.LastBody);
        Assert.Contains("Dune", stub.LastBody);
    }

    [Fact]
    public async Task DoesNotSendTheOpenCodeHeaderForCustomProviders()
    {
        await using var stub = new StubServer(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"{\"candidates\":[{\"id\":1,\"score\":35,\"machine\":true},{\"id\":2,\"score\":91,\"machine\":false}]}"}}]}""");

        var judge = new AiSubtitleJudge(NullLogger<AiSubtitleJudge>.Instance);
        var settings = new AiSettings(
            stub.BaseUrl + "/v1", "k", "m",
            SegmentCount: 8, CuesPerSegment: 8, MaxCharsPerCandidate: 3000, TimeoutSeconds: 30,
            SessionId: "session-abc", UseSessionHeader: false);

        await judge.JudgeAsync(
            MediaKind.Foreign,
            null,
            [Pick("a.srt", SrtA), Pick("b.srt", SrtB)],
            settings,
            null,
            CancellationToken.None);

        Assert.Equal(string.Empty, stub.LastSessionHeader);
    }

    [Fact]
    public async Task ReturnsNullOnHttpError()
    {
        await using var stub = new StubServer(HttpStatusCode.Unauthorized, """{"error":"bad key"}""");
        var judge = new AiSubtitleJudge(NullLogger<AiSubtitleJudge>.Instance);

        var verdicts = await judge.JudgeAsync(
            MediaKind.Foreign,
            null,
            [Pick("a.srt", SrtA), Pick("b.srt", SrtB)],
            new AiSettings(stub.BaseUrl + "/v1", "wrong", "m", 8, 8, 3000, 10),
            null,
            CancellationToken.None);

        Assert.Null(verdicts);
    }

    [Fact]
    public async Task ReturnsNullOnGarbageReply()
    {
        await using var stub = new StubServer(HttpStatusCode.OK, "totally not json");
        var judge = new AiSubtitleJudge(NullLogger<AiSubtitleJudge>.Instance);

        var verdicts = await judge.JudgeAsync(
            MediaKind.Foreign,
            null,
            [Pick("a.srt", SrtA), Pick("b.srt", SrtB)],
            new AiSettings(stub.BaseUrl + "/v1", string.Empty, "m", 8, 8, 3000, 10),
            null,
            CancellationToken.None);

        Assert.Null(verdicts);
    }

    [Fact]
    public async Task SkipsWithoutCallingTheApiWhenOnlyOneCandidate()
    {
        await using var stub = new StubServer(HttpStatusCode.OK, "{}");
        var judge = new AiSubtitleJudge(NullLogger<AiSubtitleJudge>.Instance);

        var verdicts = await judge.JudgeAsync(
            MediaKind.Foreign,
            null,
            [Pick("a.srt", SrtA)],
            new AiSettings(stub.BaseUrl + "/v1", "k", "m", 8, 8, 3000, 10),
            null,
            CancellationToken.None);

        Assert.Null(verdicts);
        Assert.Equal(0, stub.RequestCount);
    }

    private sealed class StubServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;
        private readonly string _body;
        private readonly HttpStatusCode _status;

        public StubServer(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;

            var port = FreePort();
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(BaseUrl + "/");
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        public string BaseUrl { get; }

        public int RequestCount { get; private set; }

        public string LastPath { get; private set; } = string.Empty;

        public string LastAuthorization { get; private set; } = string.Empty;

        public string LastSessionHeader { get; private set; } = string.Empty;

        public string LastBody { get; private set; } = string.Empty;

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            _listener.Close();
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The loop ends by exception when the listener is closed.
            }
        }

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return;
                }

                RequestCount++;
                LastPath = context.Request.Url?.AbsolutePath ?? string.Empty;
                LastAuthorization = context.Request.Headers["Authorization"] ?? string.Empty;
                LastSessionHeader = context.Request.Headers["x-opencode-session"] ?? string.Empty;
                using (var reader = new System.IO.StreamReader(context.Request.InputStream, Encoding.UTF8))
                {
                    LastBody = await reader.ReadToEndAsync().ConfigureAwait(false);
                }

                var bytes = Encoding.UTF8.GetBytes(_body);
                context.Response.StatusCode = (int)_status;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                context.Response.Close();
            }
        }

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
