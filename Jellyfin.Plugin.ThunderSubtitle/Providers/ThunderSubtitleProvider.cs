using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThunderSubtitle.Core;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.ThunderSubtitle.Providers;

/// <summary>
/// Exposes the Xunlei search inside Jellyfin's own per-item dialog: right-click a movie
/// -> 修改字幕 -> 搜索字幕. Jellyfin calls this provider, the user picks one candidate and
/// Jellyfin writes it next to the video as <c>&lt;name&gt;.chi.&lt;ext&gt;</c>.
/// </summary>
public sealed partial class ThunderSubtitleProvider : ISubtitleProvider
{
    /// <summary>The name shown in Jellyfin's provider lists and search results.</summary>
    public const string DisplayName = "迅雷字幕";

    /// <summary>Language tag Jellyfin uses to name the saved file (<c>.chi.srt</c>).</summary>
    private const string SavedLanguage = "chi";

    private readonly ISubtitleFinder _finder;
    private readonly ThunderClient _thunder;
    private readonly ILibraryContext _libraryContext;
    private readonly HintStore _hints;
    private readonly ProvenanceStore _provenance;
    private readonly PluginLog _log;

    /// <summary>
    /// 手动搜索到下载之间临时保存的 AI 结论。
    ///
    /// 为什么不在搜索时直接写台账：搜索只是「看看有哪些」，用户可能一份都不下；
    /// 而真正落盘发生在 Jellyfin 调用 <see cref="GetSubtitles"/> 之后，那时已经拿不到
    /// <see cref="AiVerdict"/> 了。用短命的内存表把结论带过去，下载时再写进台账。
    ///
    /// 键是 id 去掉四字符前缀后的 base64 载荷，这样用户点「简」把 tm1- 换成 tm2-
    /// 之后仍然命中同一条记录。
    ///
    /// 用 static 而不是实例字段：Jellyfin 解析字幕提供者的方式可能是每次新实例，
    /// Search 与 GetSubtitles 未必落在同一个对象上；静态表才能保证两边命中。
    /// </summary>
    private static readonly ConcurrentDictionary<string, PendingManualAi> ManualAi = new(StringComparer.Ordinal);

    private static readonly TimeSpan ManualAiLifetime = TimeSpan.FromMinutes(30);

    /// <summary>Initializes a new instance of the <see cref="ThunderSubtitleProvider"/> class.</summary>
    /// <param name="finder">The shared search pipeline.</param>
    /// <param name="thunder">Used to fetch the chosen candidate again.</param>
    /// <param name="libraryContext">Tells which library the item belongs to, so the configured search mode applies.</param>
    /// <param name="hints">手动搜索对话框里填的附加提示词。</param>
    /// <param name="provenance">字幕台账：记住下载时用的是哪份候选、AI 怎么评价的。</param>
    /// <param name="logger">Logger.</param>
    public ThunderSubtitleProvider(
        ISubtitleFinder finder,
        ThunderClient thunder,
        ILibraryContext libraryContext,
        HintStore hints,
        ProvenanceStore provenance,
        Microsoft.Extensions.Logging.ILogger<ThunderSubtitleProvider> logger)
    {
        _finder = finder;
        _thunder = thunder;
        _libraryContext = libraryContext;
        _hints = hints;
        _provenance = provenance;
        _log = new PluginLog(logger);
    }

    /// <inheritdoc />
    public string Name => DisplayName;

    /// <inheritdoc />
    public IEnumerable<VideoContentType> SupportedMediaTypes => [VideoContentType.Movie, VideoContentType.Episode];

    /// <inheritdoc />
    public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
    {
        id = SubtitleId.NormalizeIncoming(id);

        if (SubtitleId.IsPlaceholder(id))
        {
            var reason = SubtitleId.DecodePlaceholder(id);
            _log.Info($"手动下载：用户点了提示条目（{reason}），没有可下载的内容。");
            throw new InvalidOperationException($"{reason}，请稍后重新搜索。");
        }

        if (!SubtitleId.TryDecode(id, out var uri, out var name, out var mediaPath, out var simplify) || uri is null)
        {
            throw new ArgumentException("无法识别的字幕标识。", nameof(id));
        }

        if (!ThunderClient.IsAllowedUri(uri))
        {
            throw new ArgumentException("字幕地址不在允许的域名内。", nameof(id));
        }

        var bytes = await _thunder.DownloadAsync(uri, cancellationToken).ConfigureAwait(false);
        var decoded = SubtitleText.TryDecode(bytes)
                      ?? throw new InvalidOperationException("下载到的字幕无法解码。");

        var extension = SubtitleText.DetectExtension(decoded.Text, Path.GetExtension(name))
                        ?? throw new InvalidOperationException("下载到的内容不是受支持的字幕格式。");

        // 原文字形要在转换前判定：转成简体之后就再也看不出它是从繁体还是混用来的了。
        var originalText = decoded.Text;
        var originalScript = ChineseScriptDetector.Detect(SubtitleContent.StripMarkup(originalText));

        var text = originalText;
        if (simplify)
        {
            // 用户点了「简」：把繁体（或简繁混用）转成简体再写盘。
            // 这里不判断「有没有繁体」——用户既然点了，就按他的意思统一成简体，
            // 哪怕已经是简体，转换也是幂等的。
            text = SimplifiedConverter.ToSimplified(text);
            _log.Info($"手动下载：{name} 按用户要求转简体（原字形 {ChineseScriptDetector.DisplayName(originalScript)}）。");
        }

        _log.Info($"手动下载：{name}（{decoded.EncodingName} → utf-8）。");

        var payload = new UTF8Encoding(false).GetBytes(SubtitleText.Normalize(text) + "\n");

        RemoveOldSubtitles(mediaPath);
        RecordManualDownload(id, mediaPath, extension, name, originalText, originalScript, payload.Length, simplify);

        return new SubtitleResponse
        {
            Language = SavedLanguage,
            Format = extension.TrimStart('.'),
            IsForced = false,
            IsHearingImpaired = false,
            Stream = new MemoryStream(payload, writable: false)
        };
    }

    /// <summary>
    /// 手动下载也要写台账，否则「我的字幕」卡片只能看到字形，看不到 AI 评分和评语。
    ///
    /// Jellyfin 是在本方法返回之后才把流写到磁盘的，所以这里只能按命名规则预测路径
    /// （<c>&lt;片名&gt;.chi.&lt;ext&gt;</c>）；写失败时台账里会留下一条指向不存在文件的记录，
    /// 但「我的字幕」只会为真实存在的字幕流查询，不会显示脏数据。
    /// </summary>
    private void RecordManualDownload(
        string id,
        string? mediaPath,
        string extension,
        string candidateName,
        string originalText,
        ChineseScript originalScript,
        int payloadLength,
        bool simplify)
    {
        if (string.IsNullOrWhiteSpace(mediaPath))
        {
            return;
        }

        var itemId = _libraryContext.ItemIdForPath(mediaPath);
        if (itemId is null)
        {
            return;
        }

        try
        {
            var cues = SubtitleContent.Parse(originalText, extension);
            var language = SubtitleLanguageDetector.Detect(cues, SubtitleContent.StripMarkup(originalText));
            var ai = FindManualAi(id);

            _provenance.Set(new SubtitleProvenance(
                itemId.Value.ToString("N"),
                string.Empty,
                Sidecar.TargetPath(mediaPath, extension),
                candidateName,
                payloadLength,
                SubtitleRanker.CountAiMarkers(candidateName),
                DateTimeOffset.UtcNow,
                string.Empty,
                language.ToString(),
                ai?.Score,
                ai?.Source ?? string.Empty,
                ai?.Typos ?? 0,
                ai?.PromptVersion ?? 0,
                ai?.Reason ?? string.Empty,
                ai?.Machine,
                ai?.Absolute ?? false,
                simplify && ChineseScriptDetector.NeedsConversion(originalScript)
                    ? ChineseScriptDetector.DisplayName(originalScript)
                    : string.Empty));
        }
        catch (Exception ex)
        {
            // 台账只是界面用的附加信息，绝不能因为它让下载失败。
            _log.Warn($"手动下载：记录字幕台账失败（{ex.Message}），文件仍会正常写入。");
        }
    }

    /// <summary>记住一次手动搜索里某份候选的 AI 结论，等用户点下载时再写进台账。</summary>
    private static void RememberManualAi(string id, AiVerdict verdict, bool absolute)
    {
        PruneManualAi();
        ManualAi[ManualAiKey(id)] = new PendingManualAi(
            verdict.Score,
            verdict.Reason,
            verdict.MachineTranslated,
            absolute,
            verdict.EffectiveSource,
            verdict.Typos,
            AiPrompts.Version,
            DateTimeOffset.UtcNow);
    }

    private static PendingManualAi? FindManualAi(string id)
    {
        return ManualAi.TryGetValue(ManualAiKey(id), out var info)
               && info.At >= DateTimeOffset.UtcNow - ManualAiLifetime
            ? info
            : null;
    }

    private static void PruneManualAi()
    {
        var cutoff = DateTimeOffset.UtcNow - ManualAiLifetime;
        foreach (var pair in ManualAi)
        {
            if (pair.Value.At < cutoff)
            {
                ManualAi.TryRemove(pair.Key, out _);
            }
        }
    }

    /// <summary>
    /// 去掉 id 的四字符前缀（<c>tm1-</c> / <c>tm2-</c>），只留 base64 载荷。
    /// 用户点「简」只会换前缀，载荷不变；按载荷做键才能让下载命中搜索时记下的结论。
    /// </summary>
    private static string ManualAiKey(string id)
    {
        var normalized = SubtitleId.NormalizeIncoming(id);
        return normalized.Length > 4 && normalized[3] == '-'
            ? normalized[4..]
            : normalized;
    }

    private sealed record PendingManualAi(
        double Score,
        string Reason,
        bool Machine,
        bool Absolute,
        string Source,
        int Typos,
        int PromptVersion,
        DateTimeOffset At);

    /// <summary>
    /// Whether the requested language is Chinese. Jellyfin passes a display name such as
    /// "Chinese (Simplified)" plus culture hints, and the value differs per version, so
    /// both forms are accepted and anything else is declined politely.
    /// </summary>
    /// <param name="request">The search request.</param>
    /// <returns><c>true</c> when Chinese was requested.</returns>
    public static bool IsChineseRequested(SubtitleSearchRequest request)
    {
        var twoLetter = request.TwoLetterISOLanguageName?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(twoLetter) && !IsUndetermined(twoLetter))
        {
            return LooksChinese(twoLetter);
        }

        var language = request.Language?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(language) || IsUndetermined(language))
        {
            // Jellyfin resolves the route language to a culture; when that fails the request
            // carries "und"/"iv"/nothing at all. Treat that as "let the caller decide" instead
            // of silently showing an empty list.
            return true;
        }

        return LooksChinese(language);
    }

    private static bool LooksChinese(string language)
    {
        return language.StartsWith("zh", StringComparison.Ordinal)
               || language.StartsWith("chi", StringComparison.Ordinal)
               || language.StartsWith("zho", StringComparison.Ordinal)
               || language.StartsWith("cmn", StringComparison.Ordinal)
               || language.Contains("chinese", StringComparison.Ordinal)
               || language.Contains("中文", StringComparison.Ordinal);
    }

    private static bool IsUndetermined(string language)
    {
        return language is "iv" or "und" or "unk" or "mul" or "mis" or "zxx" or "none" or "neutral";
    }

    private static string Show(string? value) => string.IsNullOrWhiteSpace(value) ? "(空)" : value;

    private static string Show(SubtitleLanguage language) => language switch
    {
        SubtitleLanguage.SimplifiedChinese => "简体",
        SubtitleLanguage.TraditionalChinese => "繁体",
        SubtitleLanguage.SimplifiedChineseEnglish => "简英双语",
        SubtitleLanguage.TraditionalChineseEnglish => "繁英双语",
        SubtitleLanguage.Japanese => "日文",
        SubtitleLanguage.Korean => "韩文",
        SubtitleLanguage.English => "英文",
        _ => "其他语言"
    };

    /// <summary>
    /// Deletes the Chinese subtitles already sitting next to the video, so Jellyfin writes the
    /// freshly picked one under the canonical name instead of adding <c>.0</c>, <c>.1</c>, ...
    /// </summary>
    /// <param name="mediaPath">Path of the video.</param>
    private void RemoveOldSubtitles(string mediaPath)
    {
        if (string.IsNullOrWhiteSpace(mediaPath))
        {
            return;
        }

        foreach (var file in SubtitleFiles.FindChinese(mediaPath))
        {
            try
            {
                File.Delete(file);
                _log.Info($"手动下载：已移除旧字幕 {Path.GetFileName(file)}。");
            }
            catch (IOException ex)
            {
                _log.Warn($"手动下载：旧字幕 {Path.GetFileName(file)} 删不掉（{ex.Message}），Jellyfin 会另存一个新文件。");
            }
            catch (UnauthorizedAccessException ex)
            {
                _log.Warn($"手动下载：旧字幕 {Path.GetFileName(file)} 删不掉（{ex.Message}），Jellyfin 会另存一个新文件。");
            }
        }
    }
}

/// <summary>
/// Encodes a candidate into the opaque id Jellyfin round-trips between the search result
/// and the download call. The URL is re-validated on the way back in.
/// </summary>
public static class SubtitleId
{
    private const string PlaceholderMarker = "notice|";

    /// <summary>
    /// Prefix of a real candidate. The client script injected into jellyfin-web uses it to tell
    /// this plugin's rows apart from other providers', which is how the preview button knows
    /// where to appear.
    /// </summary>
    internal const string CandidateMarker = "ts1-";

    /// <summary>
    /// Prefix of a notice row. Deliberately different from <see cref="CandidateMarker"/>, so the
    /// "upstream was empty" entry does not get a preview button.
    /// </summary>
    internal const string NoticeMarker = "nt1-";

    /// <summary>
    /// 是不是「下载后转简体」的前缀。
    /// </summary>
    /// <remarks>
    /// 判第三位，而不是比对某个固定字符串。字形有三种（<c>s</c> 简体 / <c>t</c> 繁体 /
    /// <c>m</c> 混用），转简标志与字形无关，三种都要认。
    ///
    /// 早先这里是一个 <c>const string SimplifyMarker = "ts2-"</c> 加 <c>StartsWith</c>，
    /// 只有简体那一支能命中 —— 而现实里需要转简的恰恰全是 <c>tt2-</c> 和 <c>tm2-</c>，
    /// 于是「点了简、下载出来还是繁体」。
    /// </remarks>
    /// <param name="id">已规范化的 id（可能还带 md5 前缀）。</param>
    /// <returns><c>true</c> 表示用户要求转简。</returns>
    private static bool IsSimplifyMarker(string id)
    {
        return id.Length >= 4
            && id[0] == 't'
            && id[2] == '2'
            && id[3] == '-'
            && (id[1] == 's' || id[1] == 't' || id[1] == 'm');
    }

    /// <summary>
    /// 全部字形前缀。格式固定四个字符：<c>t</c> + 字形 + <c>1/2</c> + <c>-</c>。
    ///
    /// 字形：<c>s</c> 简体 / <c>t</c> 繁体 / <c>m</c> 简繁混用。
    /// 第三位：<c>1</c> 原样下载 / <c>2</c> 下载后转简体。
    ///
    /// 为什么编码进前缀而不是新开一个查询接口：前端每画一个紫色标签就要问一次服务端，
    /// 而 scan 是跟着 DOM 变动跑的，一次搜索会有几十次调用。前缀里本来就有的信息，
    /// 没有理由让前端再问一遍。旧版本生成的 <c>ts1-</c> 后缀含义不变，因此向后兼容。
    /// </summary>
    internal static readonly string[] ScriptMarkers =
    [
        "ts1-", "ts2-",
        "tt1-", "tt2-",
        "tm1-", "tm2-"
    ];

    /// <summary>Builds the id for a candidate.</summary>
    /// <param name="uri">Download URI.</param>
    /// <param name="extension">Content extension, with or without the dot.</param>
    /// <param name="name">Candidate name, used to re-detect the format.</param>
    /// <param name="mediaPath">Path of the video, so the download can replace the old files.</param>
    /// <param name="script">
    /// 字形。前缀里带上它，前端才能在不问服务端的情况下画出「繁体 / 简繁混用」紫色标签。
    /// </param>
    /// <returns>A URL-safe id.</returns>
    public static string Encode(
        Uri uri,
        string extension,
        string name,
        string? mediaPath,
        ChineseScript script = ChineseScript.Simplified)
    {
        var payload = string.Join('|', uri.AbsoluteUri, SubtitleText.NormalizeExtension(extension), name, mediaPath ?? string.Empty);
        return MarkerFor(script, simplify: false) + ToBase64Url(payload);
    }

    /// <summary>
    /// 取一组 id 前缀，把「原样下载」换成「下载后转简体」。
    /// 前端点「简」按钮时用它改 id，服务端不需要知道是哪种字形 —— 转换本身是幂等的。
    /// </summary>
    /// <param name="marker">原来的前缀。</param>
    /// <returns>对应的转简前缀；不是候选前缀时返回 <c>null</c>。</returns>
    internal static string? ToSimplifyMarker(string marker) => marker switch
    {
        "ts1-" => "ts2-",
        "tt1-" => "tt2-",
        "tm1-" => "tm2-",
        "ts2-" => "ts2-",
        "tt2-" => "tt2-",
        "tm2-" => "tm2-",
        _ => null
    };

    /// <summary>按字形与是否转简拼出前缀。</summary>
    private static string MarkerFor(ChineseScript script, bool simplify)
    {
        var letter = script switch
        {
            ChineseScript.Traditional => 't',
            ChineseScript.Mixed => 'm',
            _ => 's'
        };

        return $"t{letter}{(simplify ? '2' : '1')}-";
    }

    /// <summary>
    /// Builds the id of a notice row: the entry shown in the dialog when the search came back
    /// with nothing, so the user sees "upstream is empty" instead of an empty list.
    /// </summary>
    /// <param name="message">The reason to show.</param>
    /// <returns>A URL-safe id that <see cref="TryDecode"/> rejects.</returns>
    public static string EncodePlaceholder(string message) => NoticeMarker + ToBase64Url(PlaceholderMarker + message);

    /// <summary>
    /// Turns an id that came back from the client into one this class understands. Jellyfin
    /// prefixes every remote subtitle id with the md5 of the provider name and an underscore, so
    /// the payload is whatever follows the first underscore. Ids from older versions carry no
    /// marker at all and are returned unchanged.
    /// </summary>
    /// <param name="id">The id Jellyfin showed in the dialog.</param>
    /// <returns>The normalized id, or an empty string.</returns>
    public static string NormalizeIncoming(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return string.Empty;
        }

        var trimmed = id.Trim();
        if (HasMarker(trimmed))
        {
            return trimmed;
        }

        var separator = trimmed.IndexOf('_', StringComparison.Ordinal);
        if (separator < 0)
        {
            return trimmed;
        }

        var tail = trimmed[(separator + 1)..];
        return HasMarker(tail) ? tail : trimmed;
    }

    /// <summary>Reads the candidate name back out of an id.</summary>
    /// <param name="id">A candidate id.</param>
    /// <returns>The candidate name, or <c>字幕</c> when the id cannot be decoded.</returns>
    public static string DecodeName(string id)
    {
        return TryDecode(id, out _, out var name, out _) && !string.IsNullOrWhiteSpace(name)
            ? name
            : "字幕";
    }

    private static bool HasMarker(string id)
    {
        return FindMarker(id) is not null;
    }

    /// <summary>取 id 开头的字形前缀，没有时返回 <c>null</c>。</summary>
    private static string? FindMarker(string id)
    {
        foreach (var marker in ScriptMarkers)
        {
            if (id.StartsWith(marker, StringComparison.Ordinal))
            {
                return marker;
            }
        }

        return id.StartsWith(NoticeMarker, StringComparison.Ordinal) ? NoticeMarker : null;
    }

    /// <summary>Reads the reason out of a notice id.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The reason, or a generic message when the id is not a notice.</returns>
    public static string DecodePlaceholder(string id)
    {
        return IsPlaceholder(id)
            ? Base64UrlToText(id)[PlaceholderMarker.Length..]
            : "上游暂无字幕";
    }

    /// <summary>Whether the id identifies a notice row rather than a real subtitle.</summary>
    /// <param name="id">The id.</param>
    /// <returns><c>true</c> when the id is a notice.</returns>
    public static bool IsPlaceholder(string id)
    {
        return !string.IsNullOrWhiteSpace(id)
               && TryDecodeText(id, out var text)
               && text.StartsWith(PlaceholderMarker, StringComparison.Ordinal);
    }

    /// <summary>Decodes an id produced by <see cref="Encode"/>.</summary>
    /// <param name="id">The id.</param>
    /// <param name="uri">The download URI.</param>
    /// <param name="name">The candidate name, or an empty string.</param>
    /// <param name="mediaPath">The video path, or an empty string.</param>
    /// <returns><c>true</c> when the id is well formed.</returns>
    public static bool TryDecode(string id, out Uri? uri, out string name, out string mediaPath)
    {
        return TryDecode(id, out uri, out name, out mediaPath, out _);
    }

    /// <summary>Decodes an id, also reporting whether the content must be converted to Simplified.</summary>
    /// <param name="id">The id.</param>
    /// <param name="uri">The download URI.</param>
    /// <param name="name">The candidate name, or an empty string.</param>
    /// <param name="mediaPath">The video path, or an empty string.</param>
    /// <param name="simplify">
    /// <c>true</c> when the user asked for「繁转简」: the download is written in Simplified Chinese.
    /// </param>
    /// <returns><c>true</c> when the id is well formed.</returns>
    public static bool TryDecode(
        string id,
        out Uri? uri,
        out string name,
        out string mediaPath,
        out bool simplify)
    {
        uri = null;
        name = string.Empty;
        mediaPath = string.Empty;
        simplify = false;

        var normalized = NormalizeIncoming(id);
        simplify = IsSimplifyMarker(normalized);

        if (!TryDecodeText(id, out var payload))
        {
            return false;
        }

        var parts = payload.Split('|', 4);
        if (parts.Length < 3 || !Uri.TryCreate(parts[0], UriKind.Absolute, out var parsed))
        {
            return false;
        }

        uri = parsed;
        name = parts[2];
        mediaPath = parts.Length > 3 ? parts[3] : string.Empty;
        return true;
    }

    private static string ToBase64Url(string text)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(text))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string Base64UrlToText(string id)
    {
        return TryDecodeText(id, out var text) ? text : string.Empty;
    }

    private static bool TryDecodeText(string id, out string text)
    {
        text = string.Empty;
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var payload = StripMarker(NormalizeIncoming(id));
        var base64 = payload.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');

        try
        {
            text = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string StripMarker(string id)
    {
        return FindMarker(id) is { } marker ? id[marker.Length..] : id;
    }
}
