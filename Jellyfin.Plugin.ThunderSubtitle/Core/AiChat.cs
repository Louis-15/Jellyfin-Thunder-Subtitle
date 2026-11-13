using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>
/// OpenAI 兼容接口的请求/响应外壳。
///
/// 只放「协议」本身，不放业务判断：这里的东西换成任何一家兼容网关都能用，
/// 而「怎么打分、怎么核对证据」属于 <see cref="AiReplyParser"/> 和 <see cref="AiPrompts"/>。
/// </summary>
internal static class AiChat
{
    /// <summary>
    /// 序列化与反序列化共用的选项。
    /// <c>Web</c> 默认值让 <c>PascalCase</c> 属性对上 <c>snake_case</c> 之外的常见写法，
    /// 并忽略大小写 —— 不同网关返回的字段大小写并不统一。
    /// </summary>
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>按字符数截断，用于日志里展示出错响应体。</summary>
    /// <param name="value">The text.</param>
    /// <param name="length">Maximum characters to keep.</param>
    /// <returns>The truncated text.</returns>
    internal static string Truncate(string value, int length)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= length ? value : value[..length] + "…";
    }
}

/// <summary>OpenAI-compatible chat completion request.</summary>
public sealed class ChatRequest
{
    /// <summary>Gets or sets the model name.</summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the sampling temperature.
    /// 固定为 0：同一批候选重复评审要给出同一个结果，否则「自动择优」会来回改主意。
    /// </summary>
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
