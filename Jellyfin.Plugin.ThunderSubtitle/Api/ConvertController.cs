using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThunderSubtitle.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThunderSubtitle.Api;

/// <summary>
/// 「我的字幕」那一行的两个服务端能力：查字形（画紫色标签）、就地繁转简。
///
/// 为什么不走字幕提供者接口：那套接口只覆盖「搜索 → 下载远程字幕」，
/// 已有字幕是本地文件，Jellyfin 没有暴露修改它的通道。
/// </summary>
[ApiController]
[Route("ThunderSubtitle")]
public sealed class ConvertController : ControllerBase
{
    private readonly ILibraryManager _library;
    private readonly PluginLog _log;

    /// <summary>Initializes a new instance of the <see cref="ConvertController"/> class.</summary>
    /// <param name="library">Used to resolve an item id to its media streams.</param>
    /// <param name="logger">Logger.</param>
    public ConvertController(ILibraryManager library, ILogger<ConvertController> logger)
    {
        _library = library;
        _log = new PluginLog(logger);
    }

    /// <summary>
    /// 查一个字幕流用的是什么字形，以及它能不能被本插件转换。
    /// 前端在「我的字幕」每一行上调用一次。
    /// </summary>
    /// <param name="itemId">The video item id.</param>
    /// <param name="index">Media stream index of the subtitle.</param>
    /// <returns><c>{ script, convertible, reason }</c>。</returns>
    [Authorize]
    [HttpGet("Script")]
    public IActionResult Script([FromQuery] Guid itemId, [FromQuery] int index)
    {
        if (!TryResolve(itemId, index, out var path, out var error))
        {
            return Ok(new { script = "simplified", convertible = false, reason = error });
        }

        // 只读一次开头就够了：字形判定不需要整份文件。
        // 但读太少会误判（「这是」两个字判断不了），所以最多读 256 KB，
        // 对 200 KB 的常见字幕就是全文。
        string text;
        try
        {
            text = ReadHead(path!, 256 * 1024);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Ok(new { script = "simplified", convertible = false, reason = "文件读不出来：" + ex.Message });
        }

        var script = ChineseScriptDetector.Detect(SubtitleContent.StripMarkup(text));

        return Ok(new
        {
            script = script switch
            {
                ChineseScript.Traditional => "traditional",
                ChineseScript.Mixed => "mixed",
                _ => "simplified"
            },
            label = ChineseScriptDetector.DisplayName(script),
            convertible = IsPluginSubtitle(path!),
            reason = IsPluginSubtitle(path!) ? string.Empty : "不是本插件生成的字幕，不修改"
        });
    }

    /// <summary>
    /// 把「我的字幕」里的一份字幕就地转成简体。
    ///
    /// <b>先写 .bak 再覆盖</b>：转换是不可逆的信息损失（繁简多对一，个别歧义字会转错），
    /// 用户点错了至少还能拿回原文件。备份只留一份，重复转换不会堆出 .bak.1、.bak.2。
    /// </summary>
    /// <param name="itemId">The video item id.</param>
    /// <param name="index">Media stream index of the subtitle.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>{ ok, message, bytes }</c>；失败也用 200 返回，前端只读 message。</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("Convert")]
    public async Task<IActionResult> Convert(
        [FromQuery] Guid itemId,
        [FromQuery] int index,
        CancellationToken cancellationToken)
    {
        if (!TryResolve(itemId, index, out var path, out var error))
        {
            return Ok(new { ok = false, message = error, bytes = 0 });
        }

        if (!IsPluginSubtitle(path!))
        {
            // 安全边界与插件其他地方保持一致：只碰自己写的 .chi.*，
            // 用户的 .srt / .zh.srt 一律不动。
            return Ok(new { ok = false, message = "只允许转换本插件生成的 .chi.* 字幕。", bytes = 0 });
        }

        string original;
        try
        {
            original = await System.IO.File.ReadAllTextAsync(path!, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Ok(new { ok = false, message = "读取失败：" + ex.Message, bytes = 0 });
        }

        var before = ChineseScriptDetector.Detect(SubtitleContent.StripMarkup(original));
        if (!ChineseScriptDetector.NeedsConversion(before))
        {
            return Ok(new { ok = true, message = "这份字幕已经是简体，无需转换。", bytes = 0 });
        }

        var converted = SimplifiedConverter.ToSimplified(original);
        var bytes = Utf8NoBom.GetBytes(converted);

        try
        {
            var backup = path + ".bak";
            if (!System.IO.File.Exists(backup))
            {
                await System.IO.File.WriteAllTextAsync(backup, original, cancellationToken).ConfigureAwait(false);
            }

            await System.IO.File.WriteAllTextAsync(path!, converted, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Ok(new { ok = false, message = "写入失败：" + ex.Message, bytes = 0 });
        }

        _log.Info($"手动繁转简：{Path.GetFileName(path!)}（原字形 {ChineseScriptDetector.DisplayName(before)}），"
                  + $"{bytes.Length} B，原文件已备份为 .bak。");

        return Ok(new
        {
            ok = true,
            message = $"已转为简体（原字形 {ChineseScriptDetector.DisplayName(before)}），原文件备份为 {Path.GetFileName(path!)}.bak。",
            bytes = bytes.Length
        });
    }

    private static readonly System.Text.UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>把 itemId + 流序号定位成一个磁盘文件。</summary>
    private bool TryResolve(Guid itemId, int index, out string? path, out string error)
    {
        path = null;
        error = string.Empty;

        var item = _library.GetItemById(itemId);
        if (item is null)
        {
            error = "找不到影片条目。";
            return false;
        }

        foreach (var stream in item.GetMediaStreams())
        {
            if (stream.Type != MediaStreamType.Subtitle || stream.Index != index)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(stream.Path))
            {
                error = "这条字幕是内嵌的，没有独立文件。";
                return false;
            }

            path = stream.Path;
            return true;
        }

        error = "找不到这条字幕流。";
        return false;
    }

    /// <summary>只读文件开头若干字节，避免为判定字形把整部 4K 字幕读两遍。</summary>
    private static string ReadHead(string path, int maxBytes)
    {
        using var stream = System.IO.File.OpenRead(path);
        var length = (int)Math.Min(maxBytes, stream.Length);
        var buffer = new byte[length];
        var read = 0;

        while (read < length)
        {
            var chunk = stream.Read(buffer, read, length - read);
            if (chunk <= 0)
            {
                break;
            }

            read += chunk;
        }

        var text = SubtitleText.TryDecode(buffer.AsSpan(0, read).ToArray());
        return text?.Text ?? string.Empty;
    }

    /// <summary>是不是本插件的 .chi.* 文件。判定方式与 <see cref="Sidecar"/> 保持一致。</summary>
    private static bool IsPluginSubtitle(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.EndsWith(".chi", StringComparison.OrdinalIgnoreCase);
    }
}
