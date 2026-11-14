using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThunderSubtitle.Core;
using Jellyfin.Plugin.ThunderSubtitle.Providers;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ThunderSubtitle.Api;

/// <summary>
/// Serves the two things the preview button needs: the client script itself, and the text of one
/// candidate. Both are read-only: nothing here touches the media folder, so reading a subtitle
/// can never leave a stray file behind.
/// </summary>
[ApiController]
[Route("ThunderSubtitle")]
public sealed class PreviewController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Lazy<string> ClientScript = new(LoadClientScript);
    private static readonly Lazy<string> ConfigStyleSheet = new(() => LoadEmbeddedText(".configPage.css"));

    private readonly ThunderClient _thunder;
    private readonly PreviewTextCache _cache;
    private readonly ILibraryManager _library;
    private readonly PluginLog _log;
    private int _clientServed;

    /// <summary>Initializes a new instance of the <see cref="PreviewController"/> class.</summary>
    /// <param name="thunder">Used to fetch the candidate again.</param>
    /// <param name="cache">In-memory store of previewed texts.</param>
    /// <param name="library">Used by <see cref="Stream"/> to resolve an item id to its subtitle files.</param>
    /// <param name="logger">Logger.</param>
    public PreviewController(
        ThunderClient thunder,
        PreviewTextCache cache,
        ILibraryManager library,
        Microsoft.Extensions.Logging.ILogger<PreviewController> logger)
    {
        _thunder = thunder;
        _cache = cache;
        _library = library;
        _log = new PluginLog(logger);
    }

    /// <summary>Returns the script that adds the preview button to the subtitle dialog.</summary>
    /// <returns>JavaScript.</returns>
    [AllowAnonymous]
    [HttpGet("Client.js")]
    public IActionResult Client()
    {
        if (Interlocked.Exchange(ref _clientServed, 1) == 0)
        {
            _log.Info("预览脚本已被浏览器加载，字幕弹窗里会出现预览按钮。");
        }

        Response.Headers.CacheControl = "no-cache";
        return Content(ClientScript.Value, "application/javascript; charset=utf-8");
    }

    /// <summary>Returns the complete text of one candidate.</summary>
    /// <param name="id">The subtitle id Jellyfin showed in the dialog.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The text plus the numbers shown in the preview header.</returns>
    [Authorize]
    [HttpGet("Text")]
    public async Task<IActionResult> Text([FromQuery] string? id, CancellationToken cancellationToken)
    {
        var key = SubtitleId.NormalizeIncoming(id);
        if (!SubtitleId.TryDecode(key, out var uri, out _, out _, out var simplify) || uri is null)
        {
            return Error(StatusCodes.Status400BadRequest, "无法识别的字幕标识。");
        }

        if (!ThunderClient.IsAllowedUri(uri))
        {
            return Error(StatusCodes.Status400BadRequest, "字幕地址不在允许的域名内。");
        }

        // 缓存键带上「要不要转简」：同一份候选的原文与简体版是两份不同的内容，
        // 共用一个键会让用户点了「简」之后预览到没转的那份。
        var cacheKey = simplify ? key + "|zhs" : key;

        var cached = _cache.Get(cacheKey);
        if (cached is not null)
        {
            return JsonResult(cached);
        }

        PreviewText preview;
        try
        {
            var bytes = await _thunder.DownloadAsync(uri, cancellationToken).ConfigureAwait(false);
            var decoded = SubtitleText.TryDecode(bytes);
            if (decoded is null)
            {
                return Error(StatusCodes.Status502BadGateway, "下载到的字幕无法解码。");
            }

            var extension = SubtitleText.DetectExtension(decoded.Text, Path.GetExtension(uri.AbsolutePath));
            if (extension is null)
            {
                return Error(StatusCodes.Status502BadGateway, "下载到的内容不是受支持的字幕格式。");
            }

            // 「简」按钮只是一个开关：点了之后预览与下载都用转简版的 id，
            // 这里转完只在内存里返回，绝不写盘 —— 用户要先看效果再决定下不下载。
            var text = simplify ? SimplifiedConverter.ToSimplified(decoded.Text) : decoded.Text;

            var stats = SubtitleContent.Measure(text, extension);
            preview = new PreviewText(
                SubtitleId.DecodeName(key),
                extension.TrimStart('.'),
                decoded.EncodingName,
                SubtitleText.Normalize(text),
                stats.CueCount,
                stats.ContentChars,
                SubtitleLanguageDetector.Detect(SubtitleContent.StripMarkup(text)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"预览失败（{uri.Host}）：{ex.Message}");
            return Error(StatusCodes.Status502BadGateway, $"下载字幕失败：{ex.Message}");
        }

        _cache.Put(key, preview);
        _log.Info($"预览：{preview.Name}（{preview.CueCount} 条 / {preview.ContentChars} 字，{preview.EncodingName}），仅内存，未落盘。");
        return JsonResult(preview);
    }

    /// <summary>
    /// 读取「我的字幕」里一份本地字幕的全文。
    ///
    /// 与 <see cref="Text"/> 的区别：那边是「下载一份远程候选到内存里看」，
    /// 这边是「读磁盘上已经存在的那份」。用户要对比已有字幕和新候选，
    /// 没有这个端点就只能看到候选，看不到手上那份。
    ///
    /// 只读，绝不写盘。内嵌字幕没有独立文件，返回 400 并说明原因。
    /// </summary>
    /// <param name="itemId">The video item id.</param>
    /// <param name="index">Media stream index of the subtitle.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Full subtitle text, in the same shape as <see cref="Text"/>.</returns>
    [Authorize]
    [HttpGet("Stream")]
    public async Task<IActionResult> Stream(
        [FromQuery] Guid itemId,
        [FromQuery] int index,
        CancellationToken cancellationToken)
    {
        var item = _library.GetItemById(itemId);
        if (item is null)
        {
            return Error(StatusCodes.Status404NotFound, "找不到影片条目。");
        }

        string? path = null;
        foreach (var stream in item.GetMediaStreams())
        {
            if (stream.Type == MediaStreamType.Subtitle && stream.Index == index)
            {
                path = stream.Path;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return Error(StatusCodes.Status400BadRequest, "这条字幕是内嵌的，没有独立文件可以预览。");
        }

        byte[] bytes;
        try
        {
            bytes = await System.IO.File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error(StatusCodes.Status500InternalServerError, $"读取失败：{ex.Message}");
        }

        var decoded = SubtitleText.TryDecode(bytes);
        if (decoded is null)
        {
            return Error(StatusCodes.Status415UnsupportedMediaType, "这个文件不是文本字幕，无法预览。");
        }

        var extension = SubtitleText.DetectExtension(decoded.Text, Path.GetExtension(path))
                        ?? SubtitleText.NormalizeExtension(Path.GetExtension(path));

        var stats = SubtitleContent.Measure(decoded.Text, extension);
        var preview = new PreviewText(
            Path.GetFileName(path),
            extension.TrimStart('.'),
            decoded.EncodingName,
            SubtitleText.Normalize(decoded.Text),
            stats.CueCount,
            stats.ContentChars,
            SubtitleLanguageDetector.Detect(SubtitleContent.StripMarkup(decoded.Text)));

        _log.Info($"预览已有字幕：{preview.Name}（{preview.CueCount} 条 / {preview.ContentChars} 字），只读，未落盘。");
        return JsonResult(preview);
    }

    /// <summary>Drops every cached preview.</summary>
    /// <returns>How many entries were dropped.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("Cache/Clear")]
    public IActionResult ClearCache()
    {
        var cleared = _cache.Clear();
        _log.Info($"预览缓存已清空（{cleared} 份）。");
        return JsonPayload(new { cleared, remaining = _cache.Count });
    }

    /// <summary>
    /// Serves the configuration page stylesheet.
    ///
    /// 为什么不在 configPage.html 里内联样式：Jellyfin 只把插件页的 &lt;body&gt; 子节点
    /// 搬进 SPA 容器，&lt;head&gt; 里的 &lt;style&gt; 会被整块丢弃。所以样式改由
    /// configPage.html 顶部的一小段脚本注入到 document.head，这里只负责把 CSS 送出去。
    /// </summary>
    /// <returns>CSS.</returns>
    [AllowAnonymous]
    [HttpGet("Config.css")]
    public IActionResult ConfigStyle()
    {
        Response.Headers.CacheControl = "no-cache";
        return Content(ConfigStyleSheet.Value, "text/css; charset=utf-8");
    }

    private static string LoadEmbeddedText(string suffix)
    {
        var assembly = typeof(PreviewController).Assembly;

        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        throw new InvalidOperationException($"找不到内嵌资源：{suffix}");
    }

    /// <summary>
    /// 客户端脚本拆成多个文件，这里按固定顺序拼起来。
    ///
    /// 顺序有意义：第一个文件建立 <c>window.__ts</c>，Card.js 往里加卡片排版与胶囊工具，
    /// Rows.js / Hint.js 再使用它们。反过来拼会直接抛异常。
    /// </summary>
    private static readonly string[] ClientScriptFiles =
    [
        ".clientScript.js",
        ".clientScript.Card.js",
        ".clientScript.Rows.js",
        ".clientScript.Hint.js"
    ];

    internal static string LoadClientScript()
    {
        var assembly = typeof(PreviewController).Assembly;
        var names = assembly.GetManifestResourceNames();
        var builder = new StringBuilder();

        foreach (var suffix in ClientScriptFiles)
        {
            var found = false;

            foreach (var name in names)
            {
                // 用「文件名精确匹配」而不是 EndsWith：clientScript.js 的后缀
                // 也能匹配上 clientScript.Rows.js 以外的一切，容易串。
                if (!name.EndsWith(suffix, StringComparison.Ordinal))
                {
                    continue;
                }

                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is null)
                {
                    continue;
                }

                using var reader = new StreamReader(stream, Encoding.UTF8);
                builder.Append(reader.ReadToEnd()).Append('\n');
                found = true;
                break;
            }

            if (!found)
            {
                throw new InvalidOperationException($"找不到内嵌资源：{suffix}");
            }
        }

        return builder.ToString();
    }

    private IActionResult JsonResult(PreviewText preview)
    {
        var language = preview.Language switch
        {
            SubtitleLanguage.SimplifiedChinese => "简体",
            SubtitleLanguage.TraditionalChinese => "繁体",
            SubtitleLanguage.SimplifiedChineseEnglish => "简英双语",
            SubtitleLanguage.TraditionalChineseEnglish => "繁英双语",
            SubtitleLanguage.Japanese => "日文",
            SubtitleLanguage.Korean => "韩文",
            SubtitleLanguage.English => "英文",
            _ => "未知语言"
        };

        return JsonPayload(new
        {
            Name = preview.Name,
            Extension = preview.Extension,
            EncodingName = preview.EncodingName,
            Text = preview.Text,
            CueCount = preview.CueCount,
            ContentChars = preview.ContentChars,
            Bytes = preview.Bytes,
            Language = language
        });
    }

    private IActionResult JsonPayload(object payload)
    {
        return Content(JsonSerializer.Serialize(payload, JsonOptions), "application/json; charset=utf-8");
    }

    private IActionResult Error(int status, string message)
    {
        return new ContentResult
        {
            StatusCode = status,
            ContentType = "application/json; charset=utf-8",
            Content = JsonSerializer.Serialize(new { error = message }, JsonOptions)
        };
    }
}
