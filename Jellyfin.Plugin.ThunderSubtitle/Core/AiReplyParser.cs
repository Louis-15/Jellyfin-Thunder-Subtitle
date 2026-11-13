using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>
/// 把模型回复解析成 <see cref="AiVerdict"/>，并核对它引用的「问题原句」。
///
/// 核对 evidence 的目的不只是防胡说：模型只要知道会被抽查，
/// 就会真的去读送审文本，而不是凭「看起来像机翻」给个中间分。
/// </summary>
internal static class AiReplyParser
{
    /// <summary>
    /// 解析模型回复。
    /// </summary>
    /// <param name="payload">The raw HTTP body.</param>
    /// <param name="expectedCount">How many candidates were sent.</param>
    /// <returns>The verdicts, or <c>null</c> when the reply is unusable.</returns>
    internal static IReadOnlyDictionary<int, AiVerdict>? Parse(string payload, int expectedCount)
    {
        return Parse(payload, expectedCount, null);
    }

    /// <summary>
    /// 解析模型回复，并在给出候选原文时核对 evidence。
    /// </summary>
    /// <param name="payload">The raw HTTP body.</param>
    /// <param name="expectedCount">How many candidates were sent.</param>
    /// <param name="shortlist">The candidates that were sent, in prompt order.</param>
    /// <returns>The verdicts, or <c>null</c> when the reply is unusable.</returns>
    internal static IReadOnlyDictionary<int, AiVerdict>? Parse(
        string payload,
        int expectedCount,
        IReadOnlyList<SubtitlePick>? shortlist)
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
            reply = JsonSerializer.Deserialize<Reply>(json, AiChat.Json);
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

        var verdicts = new Dictionary<int, AiVerdict>(items.Length);
        foreach (var item in items)
        {
            if (item.Id < 1 || item.Id > expectedCount || verdicts.ContainsKey(item.Id))
            {
                return null;
            }

            var source = NormalizeSource(item.Source, item.Machine);

            // mixed（机翻后人工校对）仍然含机翻成分，排序时按机翻对待，
            // 否则校对过的机翻会挤掉真正的人翻。
            var machine = source is "ai" or "asr" or "mixed";
            var evidence = (item.Evidence ?? string.Empty).Trim();
            var verified = evidence.Length > 0
                           && shortlist is not null
                           && VerifyEvidence(shortlist[item.Id - 1], evidence);

            verdicts[item.Id] = new AiVerdict(
                item.Id,
                Math.Clamp(item.Score, 0, 100),
                machine,
                AiChat.Truncate(item.Reason ?? string.Empty, 60),
                source,
                Math.Max(0, item.Typos ?? 0),
                verified ? evidence : string.Empty,
                verified);
        }

        return verdicts.Count == expectedCount ? verdicts : null;
    }

    /// <summary>
    /// 校验模型引用的原句是否真的出现在候选台词里。
    ///
    /// 归一化只保留字母和数字：模型经常把全角引号写成半角、自己补个句号，
    /// 严格匹配会误伤。不做繁简转换 —— 模型通常是原样复制的，
    /// 而且繁体候选本来就该按繁体原文核对。
    /// </summary>
    private static bool VerifyEvidence(SubtitlePick pick, string evidence)
    {
        var needle = NormalizeForMatch(evidence);
        if (needle.Length < 2)
        {
            return false;
        }

        foreach (var cue in pick.Cues())
        {
            if (NormalizeForMatch(cue.Text).Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeForMatch(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static string NormalizeSource(string? source, bool machine)
    {
        return source?.Trim().ToLowerInvariant() switch
        {
            "human" => "human",
            "ai" => "ai",
            "asr" => "asr",
            "mixed" => "mixed",

            // 旧提示词只让模型回答 machine，没有 source；按它回退。
            _ => machine ? "ai" : "human"
        };
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

        // 部分网关把 content 拆成多个 part（多模态格式），这里只取文本。
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

    /// <summary>
    /// 从可能带 markdown 代码块或前后废话的回复里抠出 JSON 对象。
    /// 只取第一个 <c>{</c> 到最后一个 <c>}</c>：比正则可读，也不会被嵌套花括号搞崩。
    /// </summary>
    private static string? ExtractJsonObject(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        return start >= 0 && end > start ? content[start..(end + 1)] : null;
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

        [JsonPropertyName("source")]
        public string? Source { get; set; }

        [JsonPropertyName("typos")]
        public int? Typos { get; set; }

        [JsonPropertyName("evidence")]
        public string? Evidence { get; set; }

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }
    }
}
