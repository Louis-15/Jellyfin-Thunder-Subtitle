using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>Connection settings for the OpenAI-compatible endpoint.</summary>
/// <param name="BaseUrl">API root, e.g. <c>https://api.deepseek.com/v1</c>.</param>
/// <param name="ApiKey">Bearer token; empty for a local server that needs none.</param>
/// <param name="Model">Model name.</param>
/// <param name="SampleCues">How many cue lines to sample per candidate.</param>
/// <param name="MaxCharsPerCandidate">Hard cap on sampled characters per candidate.</param>
/// <param name="TimeoutSeconds">Per-request timeout.</param>
public sealed record AiSettings(
    string BaseUrl,
    string ApiKey,
    string Model,
    int SampleCues,
    int MaxCharsPerCandidate,
    int TimeoutSeconds)
{
    /// <summary>Creates settings from the plugin configuration.</summary>
    /// <param name="configuration">The plugin configuration.</param>
    /// <returns>The settings.</returns>
    public static AiSettings From(PluginConfiguration configuration)
    {
        return new AiSettings(
            configuration.AiBaseUrl,
            configuration.AiApiKey,
            configuration.AiModel,
            configuration.AiSampleCues,
            configuration.AiMaxCharsPerCandidate,
            configuration.AiTimeoutSeconds);
    }
}

/// <summary>
/// Asks a chat model which of the largest candidates reads like a real translation.
/// Size alone cannot tell a complete human translation from a verbosely wrong machine
/// one, and machine output is often <em>longer</em>: every filler word is translated
/// literally. The model only ever reorders a shortlist; anything unexpected falls back
/// to the deterministic ranking, so a broken endpoint can never stop a subtitle being
/// written.
/// </summary>
public sealed class AiSubtitleJudge
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = System.Net.DecompressionMethods.All
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly PluginLog _log;

    /// <summary>Initializes a new instance of the <see cref="AiSubtitleJudge"/> class.</summary>
    /// <param name="logger">Logger.</param>
    public AiSubtitleJudge(Microsoft.Extensions.Logging.ILogger<AiSubtitleJudge> logger)
    {
        _log = new PluginLog(logger);
    }

    /// <summary>Judges a shortlist of candidates.</summary>
    /// <param name="code">The JAV code, only used to give the model context.</param>
    /// <param name="shortlist">Candidates to compare, best-by-volume first.</param>
    /// <param name="settings">Endpoint settings.</param>
    /// <param name="preferred">The Chinese form the user prefers.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The verdicts keyed by candidate id, or <c>null</c> when the call failed.</returns>
    public async Task<IReadOnlyDictionary<int, AiVerdict>?> JudgeAsync(
        string code,
        IReadOnlyList<SubtitlePick> shortlist,
        AiSettings settings,
        SubtitleLanguage preferred,
        CancellationToken cancellationToken)
    {
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

        var request = BuildRequest(code, shortlist, settings, preferred);
        var body = JsonSerializer.Serialize(request, JsonOptions);

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

            using var response = await Http
                .SendAsync(message, HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);

            var payload = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _log.Warn($"AI 评审失败：HTTP {(int)response.StatusCode} - {Truncate(payload, 200)}");
                return null;
            }

            var verdicts = ParseVerdicts(payload, shortlist.Count);
            if (verdicts is null)
            {
                _log.Warn($"AI 评审失败：返回内容无法解析 - {Truncate(payload, 200)}");
            }

            return verdicts;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _log.Warn($"AI 评审超时（{settings.TimeoutSeconds} 秒），改用字符数排序。");
            return null;
        }
        catch (HttpRequestException ex)
        {
            _log.Warn($"AI 评审失败：{ex.Message}，改用字符数排序。");
            return null;
        }
        catch (JsonException ex)
        {
            _log.Warn($"AI 评审失败：请求无法序列化 - {ex.Message}");
            return null;
        }
    }

    /// <summary>Builds the chat completion body. Exposed for tests.</summary>
    /// <param name="code">The JAV code.</param>
    /// <param name="shortlist">Candidates to compare.</param>
    /// <param name="settings">Endpoint settings.</param>
    /// <param name="preferred">The Chinese form the user prefers.</param>
    /// <returns>The request payload.</returns>
    public static ChatRequest BuildRequest(
        string code,
        IReadOnlyList<SubtitlePick> shortlist,
        AiSettings settings,
        SubtitleLanguage preferred = SubtitleLanguage.SimplifiedChinese)
    {
        var prompt = new StringBuilder();
        prompt.Append("番号：").Append(code).Append('\n');
        prompt.Append("用户偏好：").Append(preferred == SubtitleLanguage.TraditionalChinese ? "繁体中文" : "简体中文")
            .Append("（同分时优先这一种）\n");
        prompt.Append("下面是同一部影片的 ").Append(shortlist.Count).Append(" 份中文字幕候选，每份只抽取了部分对白（时间戳为原片时间）。\n");

        for (var index = 0; index < shortlist.Count; index++)
        {
            var pick = shortlist[index];
            prompt.Append("\n=== 候选 ").Append(index + 1).Append(" ===\n");
            prompt.Append("语言：").Append(pick.Language == SubtitleLanguage.TraditionalChinese ? "繁体中文" : "简体中文")
                .Append("，总条数 ").Append(pick.CueCount).Append("，台词 ").Append(pick.ContentChars).Append(" 字\n");
            prompt.Append(BuildSample(pick, settings.SampleCues, settings.MaxCharsPerCandidate));
        }

        return new ChatRequest
        {
            Model = settings.Model,
            Temperature = 0,
            Messages =
            [
                new ChatMessage { Role = "system", Content = SystemPrompt },
                new ChatMessage { Role = "user", Content = prompt.ToString() }
            ]
        };
    }

    /// <summary>Parses the model reply into verdicts.</summary>
    /// <param name="payload">The raw HTTP body.</param>
    /// <param name="expectedCount">How many candidates were sent.</param>
    /// <returns>The verdicts, or <c>null</c> when the reply is unusable.</returns>
    public static IReadOnlyDictionary<int, AiVerdict>? ParseVerdicts(string payload, int expectedCount)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        string? content;
        try
        {
            using var document = JsonDocument.Parse(payload);
            content = ExtractAssistantContent(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }

        if (content is null)
        {
            return null;
        }

        var json = ExtractJsonObject(content);
        if (json is null)
        {
            return null;
        }

        Reply? reply;
        try
        {
            reply = JsonSerializer.Deserialize<Reply>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        var items = reply?.Candidates;
        if (items is null || items.Length != expectedCount)
        {
            return null;
        }

        var verdicts = new Dictionary<int, AiVerdict>();
        foreach (var item in items)
        {
            if (item.Id < 1 || item.Id > expectedCount || verdicts.ContainsKey(item.Id))
            {
                return null;
            }

            verdicts[item.Id] = new AiVerdict(
                item.Id,
                Math.Clamp(item.Score, 0, 100),
                item.Machine,
                item.Reason ?? string.Empty);
        }

        return verdicts.Count == expectedCount ? verdicts : null;
    }

    private const string SystemPrompt =
        "你是字幕质量审核员。用户给你同一部日本成人影片的若干份中文字幕候选，每份只抽取了部分对白。\n"
        + "请判断每一份是【人工翻译】还是【机器翻译或自动语音识别转写】，并给出 0-100 的可用性评分（越高越可用）。\n"
        + "判断依据：\n"
        + "1. 人工翻译：中文口语自然、符合语境；人称代词前后一致；语气词、呻吟声处理得当（不会逐字硬译）；断句符合说话节奏；专有名词前后一致。\n"
        + "2. 机器翻译或 ASR：字面直译、语序生硬；他/她/它乱用；把「あぁ」「ん」这类语气词逐字翻成没有意义的字；句子不通顺、前后矛盾；同一句话重复出现；成语或俗语被直译成怪话；明显是听不懂硬猜。\n"
        + "3. 两份都像人工翻译时，给信息更完整、断句更贴合对话节奏的那份更高分。\n"
        + "严格只输出 JSON，不要解释，不要 markdown 代码块，不要多余文字。格式：\n"
        + "{\"candidates\":[{\"id\":1,\"score\":85,\"machine\":false,\"reason\":\"不超过20字\"}]}";

    private static string BuildSample(SubtitlePick pick, int sampleCues, int maxChars)
    {
        var cues = pick.Cues();
        if (cues.Count == 0)
        {
            return Truncate(SubtitleContent.StripMarkup(pick.Text), maxChars) + "\n";
        }

        var count = Math.Clamp(sampleCues, 4, 80);
        var take = Math.Min(count, cues.Count);
        var builder = new StringBuilder();
        var chars = 0;

        for (var i = 0; i < take; i++)
        {
            // Evenly spaced across the whole file so the start, middle and end are all seen.
            var index = take == 1 ? 0 : (int)Math.Round((double)i * (cues.Count - 1) / (take - 1));
            var cue = cues[index];
            var text = cue.Text.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            var line = string.Create(
                CultureInfo.InvariantCulture,
                $"[{cue.Start:hh\\:mm\\:ss}] {text}");

            if (chars + line.Length > maxChars)
            {
                break;
            }

            builder.AppendLine(line);
            chars += line.Length;
        }

        return builder.ToString();
    }

    private static string? ExtractAssistantContent(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            return null;
        }

        var first = choices[0];
        if (!first.TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var content))
        {
            return null;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }

        // Some gateways return an array of content parts.
        if (content.ValueKind == JsonValueKind.Array)
        {
            var parts = content.EnumerateArray()
                .Where(part => part.TryGetProperty("text", out _))
                .Select(part => part.GetProperty("text").GetString())
                .Where(text => !string.IsNullOrEmpty(text));

            return string.Join("\n", parts);
        }

        return null;
    }

    private static string? ExtractJsonObject(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        return start >= 0 && end > start ? content[start..(end + 1)] : null;
    }

    private static bool TryBuildEndpoint(string baseUrl, out Uri endpoint)
    {
        endpoint = null!;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return false;
        }

        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (!trimmed.Equals(trimmed, StringComparison.Ordinal))
        {
            return false;
        }

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

    private static string Truncate(string value, int length)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= length ? value : value[..length] + "…";
    }

    /// <summary>OpenAI-compatible chat completion request.</summary>
    public sealed class ChatRequest
    {
        /// <summary>Gets or sets the model name.</summary>
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        /// <summary>Gets or sets the sampling temperature.</summary>
        [JsonPropertyName("temperature")]
        public double Temperature { get; set; }

        /// <summary>Gets or sets the messages.</summary>
        [JsonPropertyName("messages")]
        public IReadOnlyList<ChatMessage> Messages { get; set; } = Array.Empty<ChatMessage>();
    }

    /// <summary>One chat message.</summary>
    public sealed class ChatMessage
    {
        /// <summary>Gets or sets the role.</summary>
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        /// <summary>Gets or sets the content.</summary>
        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }

    private sealed class Reply
    {
        [JsonPropertyName("candidates")]
        public Item[]? Candidates { get; set; }
    }

    private sealed class Item
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("score")]
        public double Score { get; set; }

        [JsonPropertyName("machine")]
        public bool Machine { get; set; }

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }
    }
}
