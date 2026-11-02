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
public sealed class ThunderClient : IDisposable
{
    /// <summary>The search endpoint; the code is appended URL-encoded.</summary>
    public const string SearchEndpoint = "https://api-shoulei-ssl.xunlei.com/oracle/subtitle?name=";

    private static readonly string[] AllowedHostSuffixes = ["xunlei.com", "sandai.net", "geilijiasu.com"];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    /// <summary>Initializes a new instance of the <see cref="ThunderClient"/> class.</summary>
    /// <param name="httpClient">The HTTP client to use, or <c>null</c> to create one.</param>
    public ThunderClient(HttpClient? httpClient = null)
    {
        if (httpClient is null)
        {
            _http = CreateDefaultClient();
            _ownsClient = true;
        }
        else
        {
            _http = httpClient;
        }
    }

    /// <summary>Creates the client configuration Jellyfin plugins normally use.</summary>
    /// <returns>A configured <see cref="HttpClient"/>.</returns>
    public static HttpClient CreateDefaultClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        })
        {
            Timeout = TimeSpan.FromSeconds(60)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin.Plugin.ThunderSubtitle/1.0.0.0");
        return client;
    }

    /// <summary>Searches the Xunlei API by JAV code.</summary>
    /// <param name="code">The code, e.g. <c>ABF-363</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The candidates the API reported, with unusable URLs already dropped.</returns>
    public async Task<IReadOnlyList<ThunderCandidate>> SearchAsync(string code, CancellationToken cancellationToken)
    {
        var url = SearchEndpoint + Uri.EscapeDataString(code);
        using var response = await _http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new ThunderException($"迅雷字幕查询失败：HTTP {(int)response.StatusCode}");
        }

        ThunderResponse? payload;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            payload = await JsonSerializer.DeserializeAsync<ThunderResponse>(stream, JsonOptions, cancellationToken)
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
                extension = SubtitleText.NormalizeExtension(Path.GetExtension(item.Name ?? string.Empty));
            }

            candidates.Add(new ThunderCandidate(uri, item.Name ?? "未命名字幕", extension));
        }

        return candidates;
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

        using var response = await _http
            .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new ThunderException($"字幕下载失败：HTTP {(int)response.StatusCode}");
        }

        if (response.Content.Headers.ContentLength is > SubtitleText.MaxBytes)
        {
            throw new ThunderException("字幕文件过大，已跳过。");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];

        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
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

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
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
