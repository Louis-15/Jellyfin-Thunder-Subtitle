using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ThunderSubtitle.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ThunderSubtitle.Api;

/// <summary>
/// Backs the preview page: search, read a whole candidate, and only then write it next to the
/// video. Jellyfin's own "修改字幕 → 搜索字幕" dialog is compiled into the web client and only
/// renders text fields, so it cannot host a preview button; this endpoint pair is the supported
/// way to offer one.
/// </summary>
[ApiController]
[Authorize]
[Route("ThunderSubtitle")]
public sealed class PreviewController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ISubtitleFinder _finder;
    private readonly ISubtitleWriter _writer;
    private readonly PreviewCache _cache;
    private readonly ILibraryManager _libraryManager;
    private readonly PluginLog _log;

    /// <summary>Initializes a new instance of the <see cref="PreviewController"/> class.</summary>
    /// <param name="finder">The shared search pipeline.</param>
    /// <param name="writer">Writes the chosen candidate next to the video.</param>
    /// <param name="cache">In-memory store of the searches the page is looking at.</param>
    /// <param name="libraryManager">Resolves the movie the subtitle belongs to.</param>
    /// <param name="logger">Logger.</param>
    public PreviewController(
        ISubtitleFinder finder,
        ISubtitleWriter writer,
        PreviewCache cache,
        ILibraryManager libraryManager,
        Microsoft.Extensions.Logging.ILogger<PreviewController> logger)
    {
        _finder = finder;
        _writer = writer;
        _cache = cache;
        _libraryManager = libraryManager;
        _log = new PluginLog(logger);
    }

    /// <summary>Searches the Xunlei index and keeps the result in memory for previewing.</summary>
    /// <param name="code">The code to search, e.g. <c>ABF-363</c>. Optional when <paramref name="itemId"/> is given.</param>
    /// <param name="itemId">The movie to search for, when the page was opened from one.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The candidates, without their text.</returns>
    [HttpGet("Search")]
    public async Task<IActionResult> Search(
        [FromQuery] string? code,
        [FromQuery] string? itemId,
        CancellationToken cancellationToken)
    {
        var item = ResolveItem(itemId);
        var resolved = (code ?? string.Empty).Trim();
        if (resolved.Length == 0 && item is not null)
        {
            resolved = JavCode.FromPath(item.Path) ?? JavCode.FromText(item.Name) ?? string.Empty;
        }

        if (resolved.Length == 0)
        {
            return Error(StatusCodes.Status400BadRequest, "没有番号：请在输入框里填上影片番号，例如 ABF-363。");
        }

        FindOutcome outcome;
        try
        {
            outcome = await _finder.FindAsync(resolved, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"预览搜索 [{resolved}] 失败：{ex.Message}");
            return Error(StatusCodes.Status502BadGateway, $"搜索失败：{ex.Message}");
        }

        var matches = item is not null ? [item] : FindItems(resolved);
        var session = _cache.Add(resolved, item?.Id, item?.Path, outcome);

        _log.Info($"预览搜索 [{resolved}]：候选 {outcome.Candidates.Count} 条，中文 {outcome.Ranked.Count} 条，匹配影片 {matches.Count} 部。");

        return JsonResult(new
        {
            key = session.Key,
            code = resolved,
            itemId = item?.Id,
            items = matches.Select(ToItem),
            candidates = outcome.Ranked.Select((pick, index) => ToCandidate(pick, index + 1, outcome)),
            notice = outcome.Ranked.Count == 0
                ? (outcome.Candidates.Count == 0
                    ? "上游接口连续返回空数据，过一会儿再试。"
                    : "这些候选里没有简体/繁体中文，已按语言过滤丢弃。")
                : null,
            searchSeconds = Math.Round(outcome.DownloadSeconds, 1)
        });
    }

    /// <summary>Returns the complete text of one candidate, straight from memory.</summary>
    /// <param name="key">Session handle returned by <see cref="Search"/>.</param>
    /// <param name="index">1-based candidate position.</param>
    /// <returns>The subtitle file, unchanged.</returns>
    [HttpGet("Text")]
    public IActionResult Text([FromQuery] string? key, [FromQuery] int index)
    {
        var session = _cache.Get(key);
        if (session is null)
        {
            return Error(StatusCodes.Status404NotFound, "预览已过期，请重新搜索。");
        }

        var pick = session.Pick(index);
        if (pick is null)
        {
            return Error(StatusCodes.Status404NotFound, "没有这一份候选。");
        }

        return Content(SubtitleText.Normalize(pick.Text) + "\n", "text/plain; charset=utf-8");
    }

    /// <summary>Writes the chosen candidate next to the video and drops that movie's cache.</summary>
    /// <param name="key">Session handle returned by <see cref="Search"/>.</param>
    /// <param name="index">1-based candidate position.</param>
    /// <param name="itemId">The movie to write to; defaults to the one the search was started for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The file that was written.</returns>
    [HttpPost("Apply")]
    public async Task<IActionResult> Apply(
        [FromQuery] string? key,
        [FromQuery] int index,
        [FromQuery] string? itemId,
        CancellationToken cancellationToken)
    {
        var session = _cache.Get(key);
        if (session is null)
        {
            return Error(StatusCodes.Status404NotFound, "预览已过期，请重新搜索。");
        }

        var pick = session.Pick(index);
        if (pick is null)
        {
            return Error(StatusCodes.Status404NotFound, "没有这一份候选。");
        }

        var item = ResolveItem(itemId)
                   ?? (session.ItemId is { } known ? _libraryManager.GetItemById(known) : null)
                   ?? FindItems(session.Code).FirstOrDefault();
        if (item is null)
        {
            return Error(StatusCodes.Status400BadRequest, "没有找到这部影片，请在页面上先选一部影片。");
        }

        if (string.IsNullOrWhiteSpace(item.Path))
        {
            return Error(StatusCodes.Status400BadRequest, "这部影片没有文件路径（可能是虚拟条目），无法写外挂字幕。");
        }

        string target;
        try
        {
            target = await _writer.WriteAsync(item, session.Code, pick, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"预览写入 [{session.Code}] 失败：{ex.Message}");
            return Error(StatusCodes.Status500InternalServerError, $"写入失败：{ex.Message}");
        }

        // The user has picked: the other candidates for this movie are dead weight now.
        var purged = _cache.Purge(session.Code, item.Path);
        _log.Info($"预览写入 [{session.Code}]：{pick.CandidateName} → {Path.GetFileName(target)}，清理预览缓存 {purged} 份。");

        return JsonResult(new
        {
            itemId = item.Id,
            name = item.Name,
            file = target,
            fileName = Path.GetFileName(target),
            bytes = pick.Bytes,
            purged
        });
    }

    /// <summary>Drops the cached candidates of one movie.</summary>
    /// <param name="key">Session handle returned by <see cref="Search"/>.</param>
    /// <returns>How many sessions were dropped.</returns>
    [HttpPost("Clear")]
    public IActionResult Clear([FromQuery] string? key)
    {
        var session = _cache.Get(key);
        var purged = session is null ? 0 : _cache.Purge(session.Code, session.MediaPath);
        return JsonResult(new { purged, remaining = _cache.Count });
    }

    private static object ToItem(BaseItem item)
    {
        var subtitles = string.IsNullOrWhiteSpace(item.Path)
            ? Array.Empty<string>()
            : Sidecar.FindSubtitles(item.Path).Select(path => Path.GetFileName(path)).ToArray();

        return new
        {
            id = item.Id,
            name = item.Name,
            path = item.Path,
            subtitles
        };
    }

    private static object ToCandidate(SubtitlePick pick, int index, FindOutcome outcome)
    {
        var verdict = outcome.VerdictOf(pick);
        return new
        {
            index,
            name = pick.CandidateName,
            language = pick.Language == SubtitleLanguage.TraditionalChinese ? "繁体" : "简体",
            chars = pick.ContentChars,
            cues = pick.CueCount,
            bytes = pick.Bytes,
            extension = pick.Extension.TrimStart('.'),
            ai = verdict is null ? (double?)null : Math.Round(verdict.Score),
            aiReason = verdict?.Reason,
            machine = verdict?.MachineTranslated ?? (pick.AiMarkers > 0),
            judged = verdict is not null
        };
    }

    private BaseItem? ResolveItem(string? itemId)
    {
        return Guid.TryParse(itemId, out var id) ? _libraryManager.GetItemById(id) : null;
    }

    /// <summary>
    /// Finds the movies an item id would have pointed at: same code, compared with leading zeros
    /// dropped so <c>NIMA-086</c> and <c>NIMA-86</c> resolve to the same file.
    /// </summary>
    private IReadOnlyList<BaseItem> FindItems(string code)
    {
        var wanted = JavCode.CompareKey(code);
        var libraries = Plugin.Current.ResolveLibraryIds();
        var found = new List<BaseItem>();

        foreach (var libraryId in libraries)
        {
            var query = new InternalItemsQuery
            {
                ParentId = libraryId,
                Recursive = true,
                IncludeItemTypes = [BaseItemKind.Movie],
                CollapseBoxSetItems = false,
                IsVirtualItem = false,
                EnableTotalRecordCount = false
            };

            foreach (var item in _libraryManager.GetItemList(query))
            {
                var itemCode = JavCode.FromPath(item.Path);
                if (itemCode is not null
                    && string.Equals(JavCode.CompareKey(itemCode), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(item);
                }
            }
        }

        return found;
    }

    private IActionResult JsonResult(object payload)
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
