using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>
/// Asks a chat model which of the sampled candidates reads best.
///
/// 三类影片走三套提示词（见 <see cref="AiPrompts"/>），但机制相同：
/// 把候选按时间轴对齐成若干连续片段，放进同一个请求横向对比，模型只负责打分。
/// 任何异常都回退到确定性排序，绝不阻断写字幕。
/// </summary>
public sealed class AiSubtitleJudge
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = System.Net.DecompressionMethods.All
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    /// <summary>锚点与最近 cue 的最大偏差。超过它说明这份候选不覆盖该时间位置。</summary>
    internal static readonly TimeSpan SegmentTolerance = TimeSpan.FromSeconds(60);

    private readonly PluginLog _log;

    /// <summary>Initializes a new instance of the <see cref="AiSubtitleJudge"/> class.</summary>
    /// <param name="logger">Logger.</param>
    public AiSubtitleJudge(ILogger<AiSubtitleJudge> logger)
    {
        _log = new PluginLog(logger);
    }

    /// <summary>Judges a shortlist of candidates.</summary>
    /// <param name="kind">The media kind, which selects the prompt.</param>
    /// <param name="brief">影片元数据卡，可为空。</param>
    /// <param name="shortlist">Candidates to compare, best-by-volume first.</param>
    /// <param name="settings">Endpoint and sampling settings.</param>
    /// <param name="extraInstruction">用户手动搜索时输入的额外要求。</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The verdicts keyed by candidate id, or <c>null</c> when the call failed.</returns>
    public async Task<IReadOnlyDictionary<int, AiVerdict>?> JudgeAsync(
        MediaKind kind,
        MediaBrief? brief,
        IReadOnlyList<SubtitlePick> shortlist,
        AiSettings settings,
        string? extraInstruction,
        CancellationToken cancellationToken)
    {
        // 只有一份候选时排序本身就是答案，白花一次请求。
        if (shortlist.Count < 2)
        {
            return null;
        }

        if (!TryBuildEndpoint(settings.BaseUrl, out var endpoint))
        {
            _log.Warn($"AI 评审跳过：Base URL「{settings.BaseUrl}」不是合法的 http/https 地址。");
            return null;
        }

        if (string.IsNullOrWhiteSpace(settings.Model))
        {
            _log.Warn("AI 评审跳过：还没有填写模型名。");
            return null;
        }

        var composed = AiPromptBuilder.Compose(kind, brief, shortlist, settings, extraInstruction, SegmentTolerance);
        if (composed is null)
        {
            _log.Warn("AI 评审跳过：抽不出可用的台词片段（候选可能没有可解析的字幕结构）。");
            return null;
        }

        var request = ToRequest(kind, composed, settings);
        var body = JsonSerializer.Serialize(request, AiChat.Json);
        _log.Debug($"[AI] 影片类型 {MediaKindResolver.DisplayName(kind)}，送审 {shortlist.Count} 份，"
                   + $"prompt {composed.UserPrompt.Length} 字符 / {composed.Segments.Count} 段。");

        // 落点日志是人工核对 AI 判断是否离谱的主要抓手：
        // 同一片段里所有候选的时间偏差应该都很小，出现「—」说明那份字幕不覆盖该位置。
        foreach (var segment in composed.Segments)
        {
            var landing = string.Join(
                "  ",
                segment.Candidates.Select(candidate => candidate.Lines.Count == 0
                    ? $"#{candidate.PickIndex}—"
                    : $"#{candidate.PickIndex}{FormatOffset(candidate.OffsetSeconds)}"));
            _log.Debug($"  片段 {segment.Index} 锚点 {segment.Anchor:hh\\:mm\\:ss} → {landing}");
        }

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

            // OpenCode Go requires a stable per-conversation session id; their docs use it
            // for routing and prompt caching. Other providers do not read the header, so
            // it is only sent when the built-in provider is selected.
            if (settings.UseSessionHeader)
            {
                message.Headers.TryAddWithoutValidation(
                    "x-opencode-session",
                    string.IsNullOrWhiteSpace(settings.SessionId) ? "jellyfin-thunder-subtitle" : settings.SessionId);
            }

            using var response = await Http
                .SendAsync(message, HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);

            var payload = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _log.Warn($"AI 评审失败：HTTP {(int)response.StatusCode} - {AiChat.Truncate(payload, 200)}");
                return null;
            }

            var verdicts = AiReplyParser.Parse(payload, shortlist.Count, shortlist);
            if (verdicts is null)
            {
                _log.Warn($"AI 评审失败：返回内容无法解析 - {AiChat.Truncate(payload, 200)}");
                return null;
            }

            foreach (var verdict in verdicts.Values.OrderBy(value => value.Id))
            {
                // ✓ 原句核对通过 / ✗ 引了原句但核对不上 / · 没引用
                var flag = verdict.EvidenceVerified ? "✓" : (verdict.Evidence.Length > 0 ? "✗" : "·");
                var evidence = verdict.Evidence.Length > 0 ? $"「{verdict.Evidence}」" : string.Empty;
                _log.Debug($"  AI #{verdict.Id} {verdict.Score:F0} 分（{verdict.EffectiveSource}）{flag}{evidence} {verdict.Reason}");
            }

            return verdicts;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _log.Warn($"AI 评审超时（{settings.TimeoutSeconds} 秒），改用确定性排序。");
            return null;
        }
        catch (HttpRequestException ex)
        {
            _log.Warn($"AI 评审失败：{ex.Message}，改用确定性排序。");
            return null;
        }
        catch (JsonException ex)
        {
            _log.Warn($"AI 评审失败：请求无法序列化 - {ex.Message}");
            return null;
        }
    }

    /// <summary>Builds the chat completion body. Exposed for tests.</summary>
    /// <param name="kind">The media kind.</param>
    /// <param name="brief">影片元数据卡，可为空。</param>
    /// <param name="shortlist">Candidates to compare.</param>
    /// <param name="settings">Endpoint and sampling settings.</param>
    /// <param name="extraInstruction">用户额外要求。</param>
    /// <returns>The request payload, or <c>null</c> when nothing could be sampled.</returns>
    public static ChatRequest? BuildRequest(
        MediaKind kind,
        MediaBrief? brief,
        IReadOnlyList<SubtitlePick> shortlist,
        AiSettings settings,
        string? extraInstruction)
    {
        var composed = AiPromptBuilder.Compose(kind, brief, shortlist, settings, extraInstruction, SegmentTolerance);
        return composed is null ? null : ToRequest(kind, composed, settings);
    }

    private static ChatRequest ToRequest(MediaKind kind, JudgedPrompt composed, AiSettings settings)
    {
        return new ChatRequest
        {
            Model = settings.Model,
            Temperature = 0,
            Messages =
            [
                new ChatMessage { Role = "system", Content = AiPrompts.System(kind) },
                new ChatMessage { Role = "user", Content = composed.UserPrompt }
            ]
        };
    }

    private static string FormatOffset(double seconds)
    {
        return double.IsNaN(seconds) ? "?" : $"{(seconds >= 0 ? "+" : string.Empty)}{seconds:F0}s";
    }

    /// <summary>
    /// 把配置里的 Base URL 补成完整的 <c>/chat/completions</c> 端点。
    ///
    /// 单独抽出来是因为「测试连接」按钮也要用同一套规则：
    /// 用户在配置页填的可能是 <c>https://host/v1</c>，也可能是完整地址，
    /// 两边判断不一致就会出现「测试通过但评审连不上」。
    /// </summary>
    /// <param name="baseUrl">配置里的 Base URL。</param>
    /// <param name="endpoint">补全后的端点。</param>
    /// <returns>是合法的 http/https 地址时返回 true。</returns>
    internal static bool TryBuildEndpoint(string baseUrl, out Uri endpoint)
    {
        endpoint = null!;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return false;
        }

        var trimmed = baseUrl.Trim().TrimEnd('/');

        // Accept both "https://host/v1" and a full "https://host/v1/chat/completions".
        if (!trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            trimmed += "/chat/completions";
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        endpoint = uri;
        return true;
    }
}
