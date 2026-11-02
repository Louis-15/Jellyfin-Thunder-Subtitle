using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>
/// Adds the preview button to Jellyfin's own "修改字幕 → 搜索字幕" dialog.
/// <para>
/// That dialog is compiled into jellyfin-web, so a subtitle provider cannot add a row action from
/// the server side. The supported way around it is the same one the community JavaScript
/// injector plugins use: rewrite the web app shell on its way out and append one script tag.
/// Nothing is written to the web folder, so this keeps working on read-only Docker images and
/// never leaves a half-patched index.html behind. Any failure serves the original response.
/// </para>
/// </summary>
public sealed class ClientScriptInjectionFilter : IStartupFilter
{
    /// <summary>Marker written into index.html, also used to keep the rewrite idempotent.</summary>
    internal const string Marker = "<!--thunder-subtitle-client-->";

    private readonly ILogger<ClientScriptInjectionFilter> _logger;
    private int _logged;

    /// <summary>Initializes a new instance of the <see cref="ClientScriptInjectionFilter"/> class.</summary>
    /// <param name="logger">Logger.</param>
    public ClientScriptInjectionFilter(ILogger<ClientScriptInjectionFilter> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            // Registered before the rest of the pipeline so this runs outermost: stripping
            // Accept-Encoding below then reliably yields an uncompressed body we can rewrite.
            app.Use(InvokeAsync);
            next(app);
        };
    }

    /// <summary>Builds the tag inserted before &lt;/body&gt;.</summary>
    /// <param name="basePath">Path the server is mounted on, without a trailing slash.</param>
    /// <returns>The markup.</returns>
    internal static string BuildScriptBlock(string basePath)
    {
        return Marker
               + "<script src=\"" + basePath + "/ThunderSubtitle/Client.js\" defer></script>";
    }

    /// <summary>Whether a request is asking for the web app shell.</summary>
    /// <param name="path">The request path.</param>
    /// <returns><c>true</c> for /web, /web/ or /web/index.html, with or without a base path.</returns>
    internal static bool IsIndexRequest(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        return path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
               || path.Equals("/web", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Strips the web shell part off a request path, keeping any base path.</summary>
    /// <param name="path">The request path.</param>
    /// <returns>The prefix, e.g. <c>""</c> or <c>"/jellyfin"</c>.</returns>
    internal static string ResolveBasePath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var index = path.LastIndexOf("/web", StringComparison.OrdinalIgnoreCase);
        return index > 0 ? path[..index] : string.Empty;
    }

    /// <summary>Rewrites the shell, or returns <paramref name="html"/> untouched.</summary>
    /// <param name="html">The original document.</param>
    /// <param name="basePath">Path the server is mounted on.</param>
    /// <returns>The document to serve.</returns>
    internal static string Rewrite(string html, string basePath)
    {
        if (html.Contains(Marker, StringComparison.OrdinalIgnoreCase))
        {
            return html;
        }

        var bodyClose = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (bodyClose < 0)
        {
            return html;
        }

        return html[..bodyClose] + BuildScriptBlock(basePath) + "\n" + html[bodyClose..];
    }

    private async Task InvokeAsync(HttpContext context, Func<Task> next)
    {
        if (!IsIndexRequest(context.Request.Path.Value) || !HttpMethods.IsGet(context.Request.Method))
        {
            await next().ConfigureAwait(false);
            return;
        }

        // Ask for a complete, plain-text 200: a compressed or ranged body could not be rewritten.
        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("Range");
        context.Request.Headers.Remove("If-Range");

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await next().ConfigureAwait(false);
        }
        catch
        {
            context.Response.Body = originalBody;
            throw;
        }

        context.Response.Body = originalBody;
        buffer.Seek(0, SeekOrigin.Begin);

        var isHtml = context.Response.StatusCode == StatusCodes.Status200OK
                     && (context.Response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) ?? false);
        if (!isHtml)
        {
            await buffer.CopyToAsync(originalBody, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        string html;
        using (var reader = new StreamReader(buffer, Encoding.UTF8, true, 1024, leaveOpen: true))
        {
            html = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
        }

        try
        {
            var basePath = context.Request.PathBase.Value + ResolveBasePath(context.Request.Path.Value);
            html = Rewrite(html, (basePath ?? string.Empty).TrimEnd('/'));

            if (System.Threading.Interlocked.Exchange(ref _logged, 1) == 0)
            {
                _logger.LogInformation("Thunder Subtitle：已把预览脚本注入 jellyfin-web（请求时注入，不写磁盘）。");
            }
        }
        catch (Exception ex)
        {
            // Never break the web app: serve whatever came back.
            _logger.LogWarning(ex, "Thunder Subtitle：注入预览脚本失败，返回原始 index.html。");
        }

        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html;charset=utf-8";
        context.Response.ContentLength = bytes.Length;
        context.Response.Headers.Remove("ETag");
        context.Response.Headers.Remove("Last-Modified");
        context.Response.Headers.Remove("Accept-Ranges");
        await originalBody.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }
}
