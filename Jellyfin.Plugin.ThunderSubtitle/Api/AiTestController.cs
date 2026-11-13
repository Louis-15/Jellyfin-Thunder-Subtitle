using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThunderSubtitle.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThunderSubtitle.Api;

/// <summary>
/// 配置页「测试连接」按钮的后端。
///
/// 为什么由服务器代发而不是让浏览器直接请求模型服务：
/// 一是 API Key 只存在服务器插件配置里，浏览器根本没有它；
/// 二是第三方网关几乎都不会给 Jellyfin 的页面来源放行 CORS。
/// 所以这里用当前配置发一条最小请求，把「地址通不通、Key 有没有效、模型名对不对」
/// 一次性验证掉，用户不用等到真正跑任务才发现填错。
///
/// 只发一个 "ping"，token 消耗可以忽略；日志里只记模型名和耗时，
/// 绝不打印 API Key 或响应全文。
/// </summary>
[ApiController]
[Route("ThunderSubtitle")]
public sealed class AiTestController : ControllerBase
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = System.Net.DecompressionMethods.All
    })
    {
        // 超时交给每次请求自己的 CancellationTokenSource：这里再挂一个固定的
        // Timeout 会和配置项打架，用户把超时调大也不起作用。
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly PluginLog _log;

    /// <summary>Initializes a new instance of the <see cref="AiTestController"/> class.</summary>
    /// <param name="logger">Logger.</param>
    public AiTestController(ILogger<AiTestController> logger)
    {
        _log = new PluginLog(logger);
    }

    /// <summary>发一条最小请求，验证 AI 接口配置是否可用。</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <c>{ ok, message, elapsedMs }</c>。注意失败也用 200 返回 ——
    /// 这样前端只需要读 message，不必区分「HTTP 错」和「业务错」两套路径。
    /// </returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("AiTest")]
    public async Task<IActionResult> Test(CancellationToken cancellationToken)
    {
        var config = Plugin.Current;
        config.Normalize();

        if (string.IsNullOrWhiteSpace(config.AiModel))
        {
            return Ok(new { ok = false, message = "还没有填写模型名。", elapsedMs = 0L });
        }

        var settings = AiSettings.From(config, Guid.NewGuid().ToString("N"));
        if (!AiSubtitleJudge.TryBuildEndpoint(settings.BaseUrl, out var endpoint))
        {
            return Ok(new
            {
                ok = false,
                message = $"Base URL「{settings.BaseUrl}」不是合法的 http/https 地址。",
                elapsedMs = 0L
            });
        }

        var request = new ChatRequest
        {
            Model = settings.Model,
            Temperature = 0,
            Messages = [new ChatMessage { Role = "user", Content = "ping" }]
        };

        var body = JsonSerializer.Serialize(request, AiChat.Json);
        var started = Stopwatch.StartNew();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 10, 300)));

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

            if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());
            }

            if (settings.UseSessionHeader)
            {
                // 和正式评审用同一个固定 session id：OpenCode 按它做路由与提示词缓存。
                message.Headers.TryAddWithoutValidation("x-opencode-session", "jellyfin-thunder-subtitle");
            }

            using var response = await Http
                .SendAsync(message, HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);

            var payload = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            started.Stop();

            if (!response.IsSuccessStatusCode)
            {
                _log.Warn($"AI 测试连接失败：HTTP {(int)response.StatusCode} - {AiChat.Truncate(payload, 200)}");

                return Ok(new
                {
                    ok = false,
                    message = $"HTTP {(int)response.StatusCode}：{AiChat.Truncate(payload, 300)}",
                    elapsedMs = started.ElapsedMilliseconds
                });
            }

            var reply = ParseReply(payload);
            _log.Info($"AI 测试连接成功：{settings.Model}，{started.ElapsedMilliseconds} ms。");

            return Ok(new
            {
                ok = true,
                message = string.IsNullOrWhiteSpace(reply)
                    ? "连接成功，但模型返回的内容是空的。"
                    : $"连接成功，模型回复：{AiChat.Truncate(reply.Trim(), 60)}",
                elapsedMs = started.ElapsedMilliseconds
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            started.Stop();
            return Ok(new
            {
                ok = false,
                message = $"超时（{settings.TimeoutSeconds} 秒）没有响应。",
                elapsedMs = started.ElapsedMilliseconds
            });
        }
        catch (HttpRequestException ex)
        {
            started.Stop();
            return Ok(new { ok = false, message = $"请求失败：{ex.Message}", elapsedMs = started.ElapsedMilliseconds });
        }
        catch (JsonException ex)
        {
            started.Stop();
            return Ok(new { ok = false, message = $"请求无法序列化：{ex.Message}", elapsedMs = started.ElapsedMilliseconds });
        }
    }

    /// <summary>
    /// 取 <c>choices[0].message.content</c>。
    ///
    /// 用 JsonDocument 而不是定义类型：这个端点的目的是「证明能连通」，
    /// 不该因为某家网关多返回几个字段就解析失败。
    /// 拿不到就当空回复，由调用方按成功但空内容展示。
    /// </summary>
    /// <param name="payload">响应体。</param>
    /// <returns>模型的回答，取不到时为空字符串。</returns>
    private static string ParseReply(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);

            if (document.RootElement.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String)
            {
                return content.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // 网关返回的不是 JSON，按空回复处理。
        }

        return string.Empty;
    }
}
