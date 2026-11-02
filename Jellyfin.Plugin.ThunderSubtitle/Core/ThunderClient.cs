using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>Raised when the Xunlei API (or its CDN) cannot be used.</summary>
public sealed class ThunderException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ThunderException"/> class.</summary>
    /// <param name="message">The message.</param>
    public ThunderException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ThunderException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public ThunderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>One subtitle entry as reported by the API.</summary>
/// <param name="Uri">Absolute download URI.</param>
/// <param name="Name">The file name the uploader used.</param>
/// <param name="Extension">The declared extension, without the dot.</param>
public sealed record ThunderCandidate(Uri Uri, string Name, string Extension);

/// <summary>
/// Client for <c>https://api-shoulei-ssl.xunlei.com/oracle/subtitle?name=&lt;code&gt;</c>.
/// The endpoint needs no authentication but does want a normal User-Agent.
/// </summary>
/// <remarks>
/// Timeouts are per request rather than on the client: the search is one small JSON
/// document, while a download is a 10-100 KB file on a CDN that occasionally hangs. One
/// global HttpClient.Timeout has to be as slow as the worst case, which is how a single
/// stuck item used to block a whole run for tens of minutes.
/// </remarks>
public sealed class ThunderClient
{
    /// <summary>The search endpoint; the code is appended URL-encoded.</summary>
    public const string SearchEndpoint = "https://api-shoulei-ssl.xunlei.com/oracle/subtitle?name=";

    /// <summary>Time allowed for the search request. The API answers in well under a second.</summary>
    public static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Time allowed for one subtitle download. A candidate is 10-100 KB, so anything
    /// slower than this is a broken CDN edge, not a big file.
    /// </summary>
    public static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(15);

    private static readonly string[] AllowedHostSuffixes = ["xunlei.com", "sandai.net", "geilijiasu.com"];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HttpClient Http = CreateHttpClient();

    /// <summary>Searches the Xunlei API by JAV code.</summary>
    /// <param name="code">The code, e.g. <c>ABF-363</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The candidates the API reported, with unusable URLs already dropped.</returns>
    public async Task<IReadOnlyList<ThunderCandidate>> SearchAsync(string code, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SearchTimeout);

        var url = SearchEndpoint + Uri.EscapeDataString(code);
        try
        {
            using var response = await Http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new ThunderException($"迅雷字幕查询失败：HTTP {(int)response.StatusCode}");
            }

            ThunderResponse? payload;
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                payload = await JsonSerializer.DeserializeAsync<ThunderResponse>(stream, JsonOptions, timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw new ThunderException("迅雷字幕返回的数据无法解析。", ex);
            }

            if (payload is null)
            {
                throw new ThunderException("迅雷字幕返回了空响应。");
            }

            if (payload.Code != 0 || !string.Equals(payload.Result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new ThunderException($"迅雷字幕返回异常：code={payload.Code} result={payload.Result}");
            }

            var candidates = new List<ThunderCandidate>();
            foreach (var item in payload.Data ?? [])
            {
                if (string.IsNullOrWhiteSpace(item.Url)
                    || !Uri.TryCreate(item.Url, UriKind.Absolute, out var uri)
                    || !IsAllowedUri(uri))
                {
                    continue;
                }

                var extension = SubtitleText.NormalizeExtension(item.Ext);
                if (extension.Length == 0)
                {
                    extension = SubtitleText.NormalizeExtension(System.IO.Path.GetExtension(item.Name ?? string.Empty));
                }

                candidates.Add(new ThunderCandidate(uri, item.Name ?? "未命名字幕", extension));
            }

            return candidates;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ThunderException($"迅雷字幕查询超时（{SearchTimeout.TotalSeconds:F0} 秒）。");
        }
    }

    /// <summary>Downloads a candidate, refusing anything bigger than <see cref="SubtitleText.MaxBytes"/>.</summary>
    /// <param name="uri">The absolute URI from <see cref="SearchAsync"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The raw payload.</returns>
    public async Task<byte[]> DownloadAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!IsAllowedUri(uri))
        {
            throw new ThunderException($"拒绝下载非迅雷域名：{uri.Host}");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DownloadTimeout);

        try
        {
            using var response = await Http
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new ThunderException($"字幕下载失败：HTTP {(int)response.StatusCode}");
            }

            if (response.Content.Headers.ContentLength is > SubtitleText.MaxBytes)
            {
                throw new ThunderException("字幕文件过大，已跳过。");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];

            while (true)
            {
                var read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > SubtitleText.MaxBytes)
                {
                    throw new ThunderException("字幕文件过大，已跳过。");
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ThunderException($"字幕下载超时（{DownloadTimeout.TotalSeconds:F0} 秒）。");
        }
    }

    /// <summary>
    /// Only follows the API host and its CDN hosts, so a tampered response cannot make
    /// the server fetch an arbitrary address.
    /// </summary>
    /// <param name="uri">The URI to check.</param>
    /// <returns><c>true</c> when the URI is safe to request.</returns>
    public static bool IsAllowedUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        return AllowedHostSuffixes.Any(suffix =>
            uri.Host.Equals(suffix, StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            MaxConnectionsPerServer = 16
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin.Plugin.ThunderSubtitle/1.3.0.0");
        return client;
    }

    private sealed class ThunderResponse
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("result")]
        public string? Result { get; set; }

        [JsonPropertyName("data")]
        public ThunderItem[]? Data { get; set; }
    }

    private sealed class ThunderItem
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("ext")]
        public string? Ext { get; set; }
    }
}