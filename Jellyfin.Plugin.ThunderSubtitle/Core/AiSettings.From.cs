namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>
/// <see cref="AiSettings"/> 的配置映射部分。
///
/// 单独一个文件：离线工具（SubtitleProbe）只编译不含 <see cref="PluginConfiguration"/> 的
/// 那一半，这样它手上的抽样参数和线上完全一致，又不需要拖进整个配置类。
/// </summary>
public sealed partial record AiSettings
{
    /// <summary>Creates settings from the plugin configuration.</summary>
    /// <param name="configuration">The plugin configuration.</param>
    /// <param name="sessionId">Stable id for this conversation.</param>
    /// <returns>The settings.</returns>
    public static AiSettings From(PluginConfiguration configuration, string sessionId = "")
    {
        return new AiSettings(
            configuration.ApiRoot,
            configuration.AiApiKey,
            configuration.AiModel,
            configuration.AiAlignedSegments,
            configuration.AiAlignedCues,
            configuration.AiMaxCharsPerCandidate,
            configuration.AiTimeoutSeconds,
            configuration.AiCjkAlignedCues,
            configuration.AiCjkMaxChars,
            configuration.AiMaxPromptChars,
            sessionId,
            configuration.IsOpenCodeProvider);
    }
}
