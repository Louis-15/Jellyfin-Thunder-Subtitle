using System;
using Jellyfin.Plugin.ThunderSubtitle.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ThunderSubtitle.Api;

/// <summary>
/// 接收手动搜索对话框里填的「本次额外要求」。
///
/// 为什么不塞进搜索请求：Jellyfin 的 <c>SubtitleSearchRequest</c> 是平台类型，
/// 插件没有地方挂自定义参数。所以前端先把提示词 POST 上来，
/// <see cref="Providers.ThunderSubtitleProvider"/> 搜索时再按条目 Id 取回。
/// </summary>
[ApiController]
[Route("ThunderSubtitle")]
public sealed class HintController : ControllerBase
{
    private readonly HintStore _hints;
    private readonly PluginLog _log;

    /// <summary>Initializes a new instance of the <see cref="HintController"/> class.</summary>
    /// <param name="hints">The in-memory store.</param>
    /// <param name="logger">Logger.</param>
    public HintController(HintStore hints, Microsoft.Extensions.Logging.ILogger<HintController> logger)
    {
        _hints = hints;
        _log = new PluginLog(logger);
    }

    /// <summary>保存一条提示词，供紧接着的这次搜索使用。</summary>
    /// <param name="request">The item id plus the user's text.</param>
    /// <returns>Whether the hint was accepted.</returns>
    [Authorize]
    [HttpPost("Hint")]
    public IActionResult Set([FromBody] HintRequest? request)
    {
        if (request is null || !Guid.TryParse(request.ItemId, out var itemId))
        {
            return new JsonResult(new { ok = false, error = "缺少有效的条目 Id。" })
            {
                StatusCode = StatusCodes.Status400BadRequest
            };
        }

        var hint = request.Hint ?? string.Empty;
        _hints.Set(itemId, hint);

        var stored = _hints.Take(itemId);
        if (stored is null)
        {
            _log.Debug("手动搜索：本次没有附加提示词。");
        }
        else
        {
            _log.Info($"手动搜索：收到附加提示词（{stored.Length} 字），只在本次搜索中使用。");
        }

        // 把「实际存下来的长度」返回给前端：用户能立刻看到内容被截断了。
        return new JsonResult(new { ok = true, length = stored?.Length ?? 0 });
    }

    /// <summary>请求体。属性名小写，与前端 JSON 保持一致。</summary>
    public sealed class HintRequest
    {
        /// <summary>Gets or sets the Jellyfin item id (any Guid format).</summary>
        [System.Text.Json.Serialization.JsonPropertyName("itemId")]
        public string? ItemId { get; set; }

        /// <summary>Gets or sets the user's extra instruction. Empty clears it.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("hint")]
        public string? Hint { get; set; }
    }
}
