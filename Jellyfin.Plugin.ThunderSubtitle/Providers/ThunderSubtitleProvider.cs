using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThunderSubtitle.Core;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.ThunderSubtitle.Providers;

/// <summary>
/// Exposes the Xunlei search inside Jellyfin's own per-item dialog: right-click a movie
/// -> 修改字幕 -> 搜索字幕. Jellyfin calls this provider, the user picks one candidate and
/// Jellyfin writes it next to the video as <c>&lt;name&gt;.chi.&lt;ext&gt;</c>.
/// </summary>
public sealed class ThunderSubtitleProvider : ISubtitleProvider
{
    /// <summary>The name shown in Jellyfin's provider lists and search results.</summary>
    public const string DisplayName = "迅雷字幕";

    /// <summary>Language tag Jellyfin uses to name the saved file (<c>.chi.srt</c>).</summary>
    private const string SavedLanguage = "chi";

    private readonly ISubtitleFinder _finder;
    private readonly ThunderClient _thunder;
    private readonly PluginLog _log;

    /// <summary>Initializes a new instance of the <see cref="ThunderSubtitleProvider"/> class.</summary>
    /// <param name="finder">The shared search pipeline.</param>
    /// <param name="thunder">Used to fetch the chosen candidate again.</param>
    /// <param name="logger">Logger.</param>
    public ThunderSubtitleProvider(
        ISubtitleFinder finder,
        ThunderClient thunder,
        Microsoft.Extensions.Logging.ILogger<ThunderSubtitleProvider> logger)
    {
        _finder = finder;
        _thunder = thunder;
        _log = new PluginLog(logger);
    }

    /// <inheritdoc />
    public string Name => DisplayName;

    /// <inheritdoc />
    public IEnumerable<VideoContentType> SupportedMediaTypes => [VideoContentType.Movie, VideoContentType.Episode];

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteSubtitleInfo>> Search(
        SubtitleSearchRequest request,
        CancellationToken cancellationToken)
    {
        _log.Info(
            $"手动搜索请求：Name=「{request.Name}」，MediaPath=「{request.MediaPath}」，"
            + $"Language={Show(request.Language)}，TwoLetter={Show(request.TwoLetterISOLanguageName)}，"
            + $"ContentType={request.ContentType}。");

        if (!IsChineseRequested(request))
        {
            _log.Info($"跳过：请求的语言不是中文（Language={Show(request.Language)}，TwoLetter={Show(request.TwoLetterISOLanguageName)}）。");
            return Array.Empty<RemoteSubtitleInfo>();
        }

        var code = JavCode.FromPath(request.MediaPath) ?? JavCode.FromText(request.Name);
        if (code is null)
        {
            _log.Info($"手动搜索：无法从「{request.Name ?? request.MediaPath}」识别番号。");
            return Array.Empty<RemoteSubtitleInfo>();
        }

        FindOutcome outcome;
        try
        {
            outcome = await _finder.FindAsync(code, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"手动搜索 [{code}] 失败：{ex.Message}");
            return Array.Empty<RemoteSubtitleInfo>();
        }

        _log.Info($"手动搜索 [{code}]：候选 {outcome.Candidates.Count} 条，中文 {outcome.Ranked.Count} 条。");

        if (outcome.Ranked.Count == 0)
        {
            return [Notice(code, outcome)];
        }

        return outcome.Ranked
            .Where(pick => pick.Uri is not null)
            .Select((pick, index) => ToInfo(pick, index + 1, outcome, request.MediaPath))
            .ToArray();
    }

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

        if (!SubtitleId.TryDecode(id, out var uri, out var name, out var mediaPath) || uri is null)
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

        _log.Info($"手动下载：{name}（{decoded.EncodingName} → utf-8）。");

        RemoveOldSubtitles(mediaPath);

        return new SubtitleResponse
        {
            Language = SavedLanguage,
            Format = extension.TrimStart('.'),
            IsForced = false,
            IsHearingImpaired = false,
            Stream = new MemoryStream(
                new UTF8Encoding(false).GetBytes(SubtitleText.Normalize(decoded.Text) + "\n"),
                writable: false)
        };
    }

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

    /// <summary>
    /// A row that explains why the list is otherwise empty. Jellyfin renders an empty result
    /// as "no subtitles found", which hides the difference between "the API was empty" and
    /// "the release genuinely has none", and the user cannot tell it should be retried.
    /// </summary>
    /// <param name="code">The JAV code that was searched.</param>
    /// <param name="outcome">What the pipeline produced.</param>
    /// <returns>The notice row. Its id cannot be downloaded.</returns>
    private static RemoteSubtitleInfo Notice(string code, FindOutcome outcome)
    {
        var reason = outcome.Candidates.Count == 0
            ? $"上游接口连续 {ThunderClient.SearchAttempts} 次都返回空数据"
            : $"上游 {outcome.Candidates.Count} 条候选里没有可用的中文字幕";

        var detail = outcome.Candidates.Count == 0
            ? "这是上游临时故障，过一会儿重试即可"
            : string.Join('，', outcome.DroppedLanguages.Select(pair => $"{Show(pair.Key)} {pair.Value} 条")) + "，本次已按语言过滤丢弃";

        return new RemoteSubtitleInfo
        {
            Id = SubtitleId.EncodePlaceholder(reason),
            ProviderName = DisplayName,
            Name = $"（{code}）{reason}",
            Format = "srt",
            Comment = detail,
            ThreeLetterISOLanguageName = SavedLanguage,
            IsHashMatch = false,
            Forced = false,
            HearingImpaired = false
        };
    }

    private static RemoteSubtitleInfo ToInfo(
        SubtitlePick pick,
        int rank,
        FindOutcome outcome,
        string? mediaPath)
    {
        var language = pick.Language == SubtitleLanguage.TraditionalChinese ? "繁体" : "简体";
        var size = pick.Bytes >= 1024
            ? string.Create(CultureInfo.InvariantCulture, $"{pick.Bytes / 1024.0:F1} KB")
            : $"{pick.Bytes} B";
        var comment = $"{language} · {pick.ContentChars} 字 / {pick.CueCount} 条 · {size}";

        float? rating = null;
        bool? machine = pick.AiMarkers > 0 ? true : null;

        var verdict = outcome.VerdictOf(pick);
        if (verdict is not null)
        {
            // On its own line: Jellyfin renders the comment with white-space: pre-line, and mixing
            // a long reason into the measurements made the row unreadable.
            comment += "\n" + string.Create(CultureInfo.InvariantCulture, $"AI {verdict.Score:F0} 分");
            if (!string.IsNullOrWhiteSpace(verdict.Reason))
            {
                comment += $"（{verdict.Reason}）";
            }

            rating = (float)Math.Round(verdict.Score / 20.0, 2);
            machine = verdict.MachineTranslated;
        }

        return new RemoteSubtitleInfo
        {
            Id = SubtitleId.Encode(pick.Uri!, pick.Extension, pick.CandidateName, mediaPath),
            ProviderName = DisplayName,
            Name = $"{rank}. {pick.CandidateName}",
            Format = pick.Extension.TrimStart('.'),
            Comment = comment,
            ThreeLetterISOLanguageName = SavedLanguage,
            CommunityRating = rating,
            MachineTranslated = machine,
            IsHashMatch = false,
            Forced = false,
            HearingImpaired = false
        };
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

    /// <summary>Builds the id for a candidate.</summary>
    /// <param name="uri">Download URI.</param>
    /// <param name="extension">Content extension, with or without the dot.</param>
    /// <param name="name">Candidate name, used to re-detect the format.</param>
    /// <param name="mediaPath">Path of the video, so the download can replace the old files.</param>
    /// <returns>A URL-safe id.</returns>
    public static string Encode(Uri uri, string extension, string name, string? mediaPath)
    {
        var payload = string.Join('|', uri.AbsoluteUri, SubtitleText.NormalizeExtension(extension), name, mediaPath ?? string.Empty);
        return CandidateMarker + ToBase64Url(payload);
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
        return id.StartsWith(CandidateMarker, StringComparison.Ordinal)
               || id.StartsWith(NoticeMarker, StringComparison.Ordinal);
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
        uri = null;
        name = string.Empty;
        mediaPath = string.Empty;

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
        if (id.StartsWith(CandidateMarker, StringComparison.Ordinal))
        {
            return id[CandidateMarker.Length..];
        }

        return id.StartsWith(NoticeMarker, StringComparison.Ordinal)
            ? id[NoticeMarker.Length..]
            : id;
    }
}
