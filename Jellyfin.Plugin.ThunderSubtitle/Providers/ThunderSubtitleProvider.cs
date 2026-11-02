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
        if (!IsChineseRequested(request))
        {
            return Array.Empty<RemoteSubtitleInfo>();
        }

        var code = JavCode.FromPath(request.MediaPath) ?? JavCode.FromText(request.Name);
        if (code is null)
        {
            _log.Debug($"手动搜索：无法从「{request.Name ?? request.MediaPath}」识别番号。");
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

        return outcome.Ranked
            .Where(pick => pick.Uri is not null)
            .Select((pick, index) => ToInfo(pick, index + 1, outcome.Verdicts))
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
    {
        if (!SubtitleId.TryDecode(id, out var uri, out var name) || uri is null)
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
        if (!string.IsNullOrWhiteSpace(request.TwoLetterISOLanguageName))
        {
            return string.Equals(request.TwoLetterISOLanguageName, "zh", StringComparison.OrdinalIgnoreCase);
        }

        var language = request.Language;
        if (string.IsNullOrWhiteSpace(language))
        {
            return true;
        }

        var normalized = language.Trim().ToLowerInvariant();
        return normalized.StartsWith("zh", StringComparison.Ordinal)
               || normalized.StartsWith("chi", StringComparison.Ordinal)
               || normalized.StartsWith("zho", StringComparison.Ordinal)
               || normalized.StartsWith("cmn", StringComparison.Ordinal)
               || normalized.Contains("chinese", StringComparison.Ordinal)
               || normalized.Contains("中文", StringComparison.Ordinal);
    }

    private static RemoteSubtitleInfo ToInfo(SubtitlePick pick, int rank, IReadOnlyDictionary<int, AiVerdict>? verdicts)
    {
        var language = pick.Language == SubtitleLanguage.TraditionalChinese ? "繁体" : "简体";
        var size = pick.Bytes >= 1024
            ? string.Create(CultureInfo.InvariantCulture, $"{pick.Bytes / 1024.0:F1} KB")
            : $"{pick.Bytes} B";
        var comment = $"{language} · {pick.ContentChars} 字 / {pick.CueCount} 条 · {size}";

        float? rating = null;
        bool? machine = pick.AiMarkers > 0 ? true : null;

        if (verdicts is not null && verdicts.TryGetValue(rank, out var verdict))
        {
            comment += string.Create(CultureInfo.InvariantCulture, $" · AI {verdict.Score:F0} 分");
            if (!string.IsNullOrWhiteSpace(verdict.Reason))
            {
                comment += $"（{verdict.Reason}）";
            }

            rating = (float)Math.Round(verdict.Score / 20.0, 2);
            machine = verdict.MachineTranslated;
        }

        return new RemoteSubtitleInfo
        {
            Id = SubtitleId.Encode(pick.Uri!, pick.Extension, pick.CandidateName),
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
    /// <summary>Builds the id for a candidate.</summary>
    /// <param name="uri">Download URI.</param>
    /// <param name="extension">Content extension, with or without the dot.</param>
    /// <param name="name">Candidate name, used to re-detect the format.</param>
    /// <returns>A URL-safe id.</returns>
    public static string Encode(Uri uri, string extension, string name)
    {
        var payload = string.Join('|', uri.AbsoluteUri, SubtitleText.NormalizeExtension(extension), name);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>Decodes an id produced by <see cref="Encode"/>.</summary>
    /// <param name="id">The id.</param>
    /// <param name="uri">The download URI.</param>
    /// <param name="name">The candidate name, or an empty string.</param>
    /// <returns><c>true</c> when the id is well formed.</returns>
    public static bool TryDecode(string id, out Uri? uri, out string name)
    {
        uri = null;
        name = string.Empty;

        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var base64 = id.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');

        string payload;
        try
        {
            payload = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            return false;
        }

        var parts = payload.Split('|', 3);
        if (parts.Length < 3 || !Uri.TryCreate(parts[0], UriKind.Absolute, out var parsed))
        {
            return false;
        }

        uri = parsed;
        name = parts[2];
        return true;
    }
}
