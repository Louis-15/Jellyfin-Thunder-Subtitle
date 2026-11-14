using System;
using System.Globalization;
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
    private readonly ProvenanceStore _provenance;
    private readonly PluginLog _log;

    /// <summary>Initializes a new instance of the <see cref="ConvertController"/> class.</summary>
    /// <param name="library">Used to resolve an item id to its media streams.</param>
    /// <param name="provenance">字幕台账：「我的字幕」卡片要读回 AI 评分与评语。</param>
    /// <param name="logger">Logger.</param>
    public ConvertController(ILibraryManager library, ProvenanceStore provenance, ILogger<ConvertController> logger)
    {
        _library = library;
        _provenance = provenance;
        _log = new PluginLog(logger);
    }

    /// <summary>
    /// 查一个字幕流的字形、统计与 AI 记录，供「我的字幕」卡片画三行布局。
    /// 前端在「我的字幕」每一行上调用一次。
    /// </summary>
    /// <param name="itemId">The video item id.</param>
    /// <param name="index">Media stream index of the subtitle.</param>
    /// <returns>
    /// 字形 / 语言 / 格式 / 字数 / 条数 / 大小，以及台账里记过的 AI 评分、评语、翻译方式。
    /// </returns>
    [Authorize]
    [HttpGet("Script")]
    public IActionResult Script([FromQuery] Guid itemId, [FromQuery] int index)
    {
        if (!TryResolve(itemId, index, out var path, out var error))
        {
            return Ok(Unavailable(error));
        }

        // 统计字数 / 条数需要整份文件；字幕文件通常几十到几百 KB，
        // 这里上限 16 MB 只是防止误指到超大文件，正常字幕都远小于它。
        string text;
        long bytes;
        try
        {
            bytes = new FileInfo(path!).Length;
            text = ReadHead(path!, 16 * 1024 * 1024);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Ok(Unavailable("文件读不出来：" + ex.Message));
        }

        var extension = Path.GetExtension(path!);
        var stripped = SubtitleContent.StripMarkup(text);
        var cues = SubtitleContent.Parse(text, extension);
        var stats = SubtitleContent.Measure(cues, text);
        var script = ChineseScriptDetector.Detect(stripped);
        var language = SubtitleLanguageDetector.Detect(cues, stripped);
        var plugin = IsPluginSubtitle(path!);

        // 台账只有在「记录的就是这个文件」且文件没被外部改过时才可信。
        // 用户手动替换过文件后还沿用旧评分，会让「与现有字幕对比」变成对比错对象。
        var record = _provenance.Get(itemId.ToString("N"));
        var trusted = record is not null
                      && string.Equals(record.File, path, StringComparison.Ordinal)
                      && !IsStale(record, path!);

        var convertedFrom = trusted ? record!.ConvertedFrom : string.Empty;
        var detectedFromBackup = false;
        if (string.IsNullOrEmpty(convertedFrom) && plugin && script == ChineseScript.Simplified)
        {
            // 兼容这次改动之前做过的转换：那时 Convert 还没写 ConvertedFrom，
            // 只要 .bak 比台账新，就说明当前文件是转换后的产物。
            var backup = path + ".bak";
            if (System.IO.File.Exists(backup)
                && (record is null || !trusted || System.IO.File.GetLastWriteTimeUtc(backup) > record.WrittenAt.UtcDateTime))
            {
                var backupScript = DetectScript(backup);
                if (ChineseScriptDetector.NeedsConversion(backupScript))
                {
                    convertedFrom = ChineseScriptDetector.DisplayName(backupScript);
                    detectedFromBackup = true;
                }
            }
        }

        if (detectedFromBackup)
        {
            // 把探测结果落回台账：用户删掉 .bak 之后，「我的字幕」仍然记得它是转过的。
            // 旧记录的 AI 字段此时已经因为文件被改动而不可信，这里只保留身份信息。
            try
            {
                _provenance.Set(new SubtitleProvenance(
                    itemId.ToString("N"),
                    record?.Code ?? string.Empty,
                    path!,
                    record?.CandidateName ?? Path.GetFileName(path!),
                    bytes,
                    0,
                    DateTimeOffset.UtcNow,
                    record?.MediaKind ?? string.Empty,
                    record?.Language ?? string.Empty,
                    ConvertedFrom: convertedFrom));
            }
            catch (Exception ex)
            {
                _log.Warn($"读取字幕卡片：回填转换记录失败（{ex.Message}）。");
            }
        }

        bool? machine = null;
        if (trusted)
        {
            machine = record!.AiMachineTranslated;
            if (machine is null && record.AiSource.Length > 0)
            {
                // 旧台账没有这个字段，只能从来源标签反推；mixed 推不出来就留空，
                // 前端宁可不画翻译胶囊，也不要给用户一个猜的结论。
                machine = record.AiSource is "ai" or "asr" ? true : record.AiSource == "human" ? false : null;
            }
        }

        return Ok(new
        {
            available = true,
            script = ScriptLetter(script),
            label = ChineseScriptDetector.DisplayName(script),
            language = SubtitleLanguageDetector.DisplayName(language),
            // 转换只对插件自己写的 .chi.* 开放，且已经是简体时没有必要再转。
            convertible = plugin && ChineseScriptDetector.NeedsConversion(script),
            reason = plugin ? string.Empty : "不是本插件生成的字幕，不修改",
            file = Path.GetFileName(path),
            format = extension.TrimStart('.'),
            chars = stats.ContentChars,
            cues = stats.CueCount,
            bytes,
            convertedFrom,
            aiScore = trusted ? record!.AiScore : null,
            aiScoreText = trusted && record!.AiScore is { } score
                ? score.ToString("F0", CultureInfo.InvariantCulture)
                : string.Empty,
            aiComment = trusted ? ComposeComment(record!) : string.Empty,
            machine
        });
    }

    /// <summary>「我的字幕」卡片读不到文件时返回的空信息，前端据 <c>available=false</c> 跳过排版。</summary>
    private static object Unavailable(string reason) => new
    {
        available = false,
        script = "simplified",
        label = "简体",
        language = string.Empty,
        convertible = false,
        reason,
        file = string.Empty,
        format = string.Empty,
        chars = 0,
        cues = 0,
        bytes = 0L,
        convertedFrom = string.Empty,
        aiScore = (double?)null,
        aiScoreText = string.Empty,
        aiComment = string.Empty,
        machine = (bool?)null
    };

    private static string ScriptLetter(ChineseScript script) => script switch
    {
        ChineseScript.Traditional => "traditional",
        ChineseScript.Mixed => "mixed",
        _ => "simplified"
    };

    /// <summary>
    /// 台账记录是否已过期：文件在记录之后被外部改过。
    /// 手动下载是「先记台账、Jellyfin 后写文件」，文件时间会略晚于记录时间，
    /// 所以留 5 分钟容差，避免把正常写入误判成外部修改。
    /// </summary>
    private static bool IsStale(SubtitleProvenance record, string path)
    {
        try
        {
            return System.IO.File.GetLastWriteTimeUtc(path) > record.WrittenAt.UtcDateTime.AddMinutes(5);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>把台账里的 AI 结论拼成卡片第 3 行的文本。</summary>
    private static string ComposeComment(SubtitleProvenance record)
    {
        var note = record.AiAbsolute ? "（仅此一份，绝对评分）" : string.Empty;
        return string.IsNullOrWhiteSpace(record.AiReason) ? note : note + record.AiReason;
    }

    /// <summary>读一份字幕开头并判定字形；失败时按简体处理（不显示转换标签）。</summary>
    private static ChineseScript DetectScript(string path)
    {
        try
        {
            return ChineseScriptDetector.Detect(SubtitleContent.StripMarkup(ReadHead(path, 256 * 1024)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ChineseScript.Simplified;
        }
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

        RecordConversion(itemId, path!, before);

        return Ok(new
        {
            ok = true,
            message = $"已转为简体（原字形 {ChineseScriptDetector.DisplayName(before)}），原文件备份为 {Path.GetFileName(path!)}.bak。",
            bytes = bytes.Length
        });
    }

    /// <summary>
    /// 转换成功后把「从哪种字形转来的」写进台账。
    /// 「我的字幕」卡片下次打开时要显示「简繁混用转简体」而不是笼统的「简体」，
    /// 所以原字形必须记住 —— 文件转完之后已经看不出来了。
    /// </summary>
    private void RecordConversion(Guid itemId, string path, ChineseScript before)
    {
        try
        {
            var key = itemId.ToString("N");
            var label = ChineseScriptDetector.DisplayName(before);
            var size = new FileInfo(path).Length;
            var record = _provenance.Get(key);

            _provenance.Set(record is not null && string.Equals(record.File, path, StringComparison.Ordinal)
                // 转换刚写过文件，记录时间必须跟着更新：否则「文件比记录新」会被判成
                // 外部改动，下一次打开卡片就会把 AI 评分藏起来。
                ? record with { ConvertedFrom = label, Bytes = size, WrittenAt = DateTimeOffset.UtcNow }
                : new SubtitleProvenance(
                    key,
                    string.Empty,
                    path,
                    Path.GetFileName(path),
                    size,
                    0,
                    DateTimeOffset.UtcNow,
                    ConvertedFrom: label));
        }
        catch (Exception ex)
        {
            // 台账只影响界面展示，记不下来也不能让转换本身失败。
            _log.Warn($"手动繁转简：记录字幕台账失败（{ex.Message}）。");
        }
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
