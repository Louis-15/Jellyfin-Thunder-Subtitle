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
using Microsoft.AspNetCore.Mvc.ModelBinding;
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

    /// <summary>
    /// 发一条最小请求，验证 AI 接口配置是否可用。
    ///
    /// 请求体里带的是**前端当前表单值**（可能还没保存）：测试必须测用户眼前这套配置，
    /// 否则「切换供应商后先点测试」测到的会是上一次保存的供应商。
    /// 不传请求体时退回已保存配置，兼容浏览器里缓存的旧页面。
    /// </summary>
    /// <param name="request">前端当前表单值；为空时用已保存配置。</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <c>{ ok, message, endpoint, model, reply, elapsedMs }</c>。失败也用 200 返回，
    /// 前端只需要读 message，不必区分「HTTP 错」和「业务错」两套路径。
    /// </returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("Ai/Test")]
    // 旧版本配置页用的是 /AiTest；浏览器缓存可能还在跑旧页面，两个路由都保留。
    [HttpPost("AiTest")]
    public async Task<IActionResult> Test(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] AiTestRequest? request,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Current;
        config.Normalize();

        var settings = BuildSettings(config, request, Guid.NewGuid().ToString("N"));

        if (string.IsNullOrWhiteSpace(settings.Model))
        {
            return Ok(Fail("还没有填写模型名。", settings));
        }

        if (!AiSubtitleJudge.TryBuildEndpoint(settings.BaseUrl, out var endpoint))
        {
            return Ok(Fail($"Base URL「{settings.BaseUrl}」不是合法的 http/https 地址。", settings));
        }

        var chatRequest = new ChatRequest
        {
            Model = settings.Model,
            Temperature = 0,
            // 与 AV Collection 用同一句提示词：两边的「模型回复」才有可比性。
            Messages = [new ChatMessage { Role = "user", Content = "只回复两个字：可以" }]
        };

        var body = JsonSerializer.Serialize(chatRequest, AiChat.Json);
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

                return Ok(Fail(
                    $"HTTP {(int)response.StatusCode}：{AiChat.Truncate(payload, 300)}",
                    settings,
                    endpoint,
                    started.ElapsedMilliseconds));
            }

            var reply = ParseReply(payload);
            _log.Info($"AI 测试连接成功：{settings.Model}，{started.ElapsedMilliseconds} ms。");

            return Ok(new
            {
                ok = true,
                message = $"连接正常（{started.ElapsedMilliseconds} ms）",
                endpoint = endpoint.ToString(),
                model = settings.Model,
                reply = AiChat.Truncate(reply.Trim(), 80),
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
            return Ok(Fail(
                $"超时（{settings.TimeoutSeconds} 秒）没有响应。",
                settings,
                endpoint,
                started.ElapsedMilliseconds));
        }
        catch (HttpRequestException ex)
        {
            started.Stop();
            return Ok(Fail(
                $"请求失败：{ex.Message}",
                settings,
                endpoint,
                started.ElapsedMilliseconds));
        }
        catch (JsonException ex)
        {
            started.Stop();
            return Ok(Fail(
                $"请求无法序列化：{ex.Message}",
                settings,
                endpoint,
                started.ElapsedMilliseconds));
        }
    }

    /// <summary>
    /// 组装这次探测要用的设置。
    ///
    /// 内部可见是为了让单元测试固定「请求值优先于已保存值」这条规则 ——
    /// 它正是「切换供应商后点测试却测到旧供应商」的根因。
    /// </summary>
    /// <param name="config">已保存的插件配置。</param>
    /// <param name="request">前端表单值；为空时用已保存配置。</param>
    /// <param name="sessionId">OpenCode 会话 id。</param>
    /// <returns>本次探测使用的设置。</returns>
    internal static AiSettings BuildSettings(
        PluginConfiguration config,
        AiTestRequest? request,
        string sessionId)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Provider))
        {
            return TrimForProbe(AiSettings.From(config, sessionId));
        }

        // 用一份临时配置承接表单值，再走和正式评审完全相同的
        // ResolveApiKey / ResolveModel / ApiRoot 规则 ——
        // 测试与正式请求的地址、请求头和模型必须一模一样，否则测试通过也说明不了什么。
        var probe = new PluginConfiguration
        {
            AiProvider = request.Provider,
            AiBaseUrl = request.BaseUrl ?? string.Empty,
            AiOpenCodeApiKey = request.OpenCodeApiKey ?? string.Empty,
            AiOpenCodeModel = request.OpenCodeModel ?? string.Empty,
            AiCommandCodeApiKey = request.CommandCodeApiKey ?? string.Empty,
            AiCommandCodeModel = request.CommandCodeModel ?? string.Empty,
            AiCustomApiKey = request.CustomApiKey ?? string.Empty,
            AiCustomModel = request.CustomModel ?? string.Empty,
            AiTimeoutSeconds = request.TimeoutSeconds ?? config.AiTimeoutSeconds
        };

        probe.Normalize();
        return TrimForProbe(AiSettings.From(probe, sessionId));
    }

    private static AiSettings TrimForProbe(AiSettings settings)
    {
        // 测试只是一条 ping，不该让用户对着 300 秒超时转圈；与 AV Collection 统一用 45 秒。
        return settings with
        {
            TimeoutSeconds = Math.Clamp(Math.Min(settings.TimeoutSeconds, 45), 10, 45)
        };
    }

    private static object Fail(
        string message,
        AiSettings settings,
        Uri? endpoint = null,
        long elapsedMs = 0)
    {
        return new
        {
            ok = false,
            message,
            endpoint = endpoint?.ToString() ?? string.Empty,
            model = settings.Model,
            reply = string.Empty,
            elapsedMs
        };
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

/// <summary>
/// 「测试连接」的请求体：前端把当前表单值原样发来。
///
/// 测试必须测用户眼前这套配置，而不是已保存的那份 ——
/// 否则切换供应商后先点测试，测到的仍是上一次保存的供应商。
/// 字段与 <see cref="PluginConfiguration"/> 的 AI 槽位一一对应，前端不需要额外转换规则。
/// </summary>
public sealed class AiTestRequest
{
    /// <summary>Gets or sets 当前选中的供应商 id。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("provider")]
    public string? Provider { get; set; }

    /// <summary>Gets or sets 自定义供应商的 Base URL（内置供应商忽略）。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("baseUrl")]
    public string? BaseUrl { get; set; }

    /// <summary>Gets or sets Open Code 的 API Key。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("openCodeApiKey")]
    public string? OpenCodeApiKey { get; set; }

    /// <summary>Gets or sets Open Code 的模型名。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("openCodeModel")]
    public string? OpenCodeModel { get; set; }

    /// <summary>Gets or sets Command Code 的 API Key。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("commandCodeApiKey")]
    public string? CommandCodeApiKey { get; set; }

    /// <summary>Gets or sets Command Code 的模型名。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("commandCodeModel")]
    public string? CommandCodeModel { get; set; }

    /// <summary>Gets or sets 自定义供应商的 API Key。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("customApiKey")]
    public string? CustomApiKey { get; set; }

    /// <summary>Gets or sets 自定义供应商的模型名。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("customModel")]
    public string? CustomModel { get; set; }

    /// <summary>Gets or sets 当前表单里的超时秒数。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("timeoutSeconds")]
    public int? TimeoutSeconds { get; set; }
}
