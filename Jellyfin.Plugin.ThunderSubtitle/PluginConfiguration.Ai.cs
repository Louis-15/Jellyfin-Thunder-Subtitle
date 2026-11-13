using System;

namespace Jellyfin.Plugin.ThunderSubtitle;

/// <summary>
/// AI 评审相关的配置项。
///
/// 单独一个文件：这一组有 20 多项，且和「下载 / 影视库」那部分没有交集。
/// 配置页也是按这两组分 fieldset 的，文件结构和页面结构对得上，改起来才不容易漏。
/// </summary>
public partial class PluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether the largest finalists are sent to a chat
    /// model so it can tell a real translation from verbose machine output.
    /// </summary>
    public bool AiEnabled { get; set; }

    /// <summary>
    /// Gets or sets the AI provider: <c>OpenCode</c> (built in) or <c>Custom</c>
    /// (any other OpenAI-compatible endpoint).
    /// </summary>
    public string AiProvider { get; set; } = AiProviderOpenCode;

    /// <summary>Provider id for the built-in OpenCode Go endpoint.</summary>
    public const string AiProviderOpenCode = "OpenCode";

    /// <summary>Provider id for a user-supplied OpenAI-compatible endpoint.</summary>
    public const string AiProviderCustom = "Custom";

    /// <summary>
    /// The OpenCode Go API root. Required by their terms: requests must also carry a
    /// stable <c>x-opencode-session</c> header, which <see cref="Core.AiSubtitleJudge"/> adds.
    /// </summary>
    public const string OpenCodeBaseUrl = "https://opencode.ai/inference/go/openai/v1";

    /// <summary>Sensible default model on OpenCode Go.</summary>
    public const string OpenCodeDefaultModel = "deepseek-v4.1-flash";

    /// <summary>Gets or sets the OpenAI-compatible API root for the custom provider.</summary>
    public string AiBaseUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the API key. Sent as a bearer token, never logged.</summary>
    public string AiApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the model name.</summary>
    public string AiModel { get; set; } = OpenCodeDefaultModel;

    /// <summary>
    /// 每个语言档位挑几份送 AI 评审。
    ///
    /// 1.10 起默认 5：既然 token 不是瓶颈，就多送几份让模型横向比较 ——
    /// 只送两三份时它缺少参照，容易给所有候选都打 70 多分。
    /// 上限 6（两个档位正好占满 12 份总名额）。
    ///
    /// 注意这个数字现在只是「每档最多几份」，不再乘档位数量：
    /// 名额按档位顺序分配，凑不满的档位会把余量让给其他档位。
    /// </summary>
    public int AiPerTier { get; set; } = 5;

    /// <summary>
    /// 旧版的「评审前 N 名」。1.9.1.0 起不再参与筛选，保留字段只为兼容已存在的 XML 配置
    /// （删掉字段会让旧配置里的同名节点变成"未知元素"，虽然不影响解析，但没必要制造噪音）。
    /// </summary>
    public int AiTopCandidates { get; set; } = 3;

    /// <summary>
    /// 旧版的「每份候选抽多少条台词」。1.10 起被「对齐片段数 × 每段条数」取代，
    /// 保留字段只为兼容已存在的 XML 配置，不再参与筛选。
    /// </summary>
    public int AiSampleCues { get; set; } = 24;

    /// <summary>对齐抽样的片段数。6 段覆盖 12%～90% 的片长。</summary>
    public int AiAlignedSegments { get; set; } = 6;

    /// <summary>每个片段连续取几条台词。</summary>
    public int AiAlignedCues { get; set; } = 8;

    /// <summary>
    /// 中文影视每段取几条台词。
    /// 错别字是稀疏事件，24 条很容易一条都碰不到，所以默认比另外两类更密。
    /// </summary>
    public int AiCjkAlignedCues { get; set; } = 10;

    /// <summary>中文影视每份候选的抽样字数上限。</summary>
    public int AiCjkMaxChars { get; set; } = 6000;

    /// <summary>
    /// 单次请求的 user prompt 字符上限。超出时自动减少片段数 / 每段条数，
    /// 防的是用户配了 8k 上下文的本地小模型。
    /// </summary>
    public int AiMaxPromptChars { get; set; } = 24000;

    /// <summary>
    /// 近似重复的相似度阈值。
    ///
    /// 同一份机翻被不同站点转码、改名、换标点后内容哈希不同，精确去重拦不住，
    /// 它们会一起挤进送审名额。设 0.99 会漏掉改标点的版本，设 0.90 会把
    /// 「同一影片的不同翻译」误合并 —— 那就等于替用户做了选择。
    /// 1.0 表示关闭（只保留精确哈希去重）。
    /// </summary>
    public double NearDuplicateThreshold { get; set; } = 0.95;

    /// <summary>是否缓存 AI 评分。「自动补全」和「自动择优」会对同一批候选重复评审，缓存能省下这笔钱。</summary>
    public bool AiCacheEnabled { get; set; } = true;

    /// <summary>AI 评分缓存的有效期（天）。</summary>
    public int AiCacheDays { get; set; } = 30;

    /// <summary>
    /// 中文影视的错别字率告警阈值（每千字）。
    /// 只用于日志：让用户知道「这份字幕错别字偏多」，不参与排序 ——
    /// 用户明确说过中文影视的错别字是「排序参考」，不是淘汰条件。
    /// </summary>
    public double AiTypoWarnRate { get; set; } = 3.0;

    /// <summary>
    /// 日本AV 专用：简体档的合格分。
    ///
    /// 用户的原话是「优先简体，找不到合适的简体才用繁体」。这个分数就是「合适」的定义：
    /// 简体档的最高分低于它、且繁体档确实更高时，本次降级到繁体。
    /// 设为 0 关闭降级（只按语言档位排，不看质量）。非 AV 影片忽略此配置。
    /// </summary>
    public int AiSimplifiedAcceptScore { get; set; } = 60;

    /// <summary>
    /// 非中文影视的跨档择优分差。默认 8，0 表示关闭。
    ///
    /// 非中文影视的语言策略是「有简英双语就用双语」，但双语版有时是照英文词序硬翻的，
    /// 而纯简体里可能有院线国配台本。实测《加勒比海盗》里国配台本 81 分、
    /// 最好的双语版 73 分 —— 差 8 分时人工核对也确认国配台本明显更好。
    ///
    /// 门槛刻意设得高：AI 打分有主观波动，8 份候选常常挤在 70～80 这 10 分区间里，
    /// 三五分的差距不足以为此推翻语言偏好。设为 0 即恢复「无条件双语优先」。
    /// </summary>
    public int AiTierOverrideGap { get; set; } = 8;

    /// <summary>Gets or sets the hard cap on sampled characters per candidate.</summary>
    public int AiMaxCharsPerCandidate { get; set; } = 3000;

    /// <summary>
    /// Gets or sets the per-request timeout.
    ///
    /// 默认 100 秒：送审提到 5 份/档后，8 份候选的 prompt 约 2 万字符，
    /// 实测一次完整评审要 75 秒左右，原来默认 60 秒会稳定超时并退回确定性排序。
    /// </summary>
    public int AiTimeoutSeconds { get; set; } = 100;

    /// <summary>Gets a value indicating whether the AI check has everything it needs.</summary>
    public bool IsAiUsable => AiEnabled
                              && !string.IsNullOrWhiteSpace(ApiRoot)
                              && !string.IsNullOrWhiteSpace(AiModel);

    /// <summary>Gets the effective API root for the selected provider.</summary>
    public string ApiRoot => IsOpenCodeProvider
        ? OpenCodeBaseUrl
        : (AiBaseUrl ?? string.Empty).Trim();

    /// <summary>Gets a value indicating whether the built-in OpenCode endpoint is selected.</summary>
    public bool IsOpenCodeProvider =>
        string.Equals(AiProvider, AiProviderOpenCode, StringComparison.OrdinalIgnoreCase);

    /// <summary>Clamps the AI settings a hand-edited config file could have broken.</summary>
    private void NormalizeAi()
    {
        AiBaseUrl ??= string.Empty;
        AiApiKey ??= string.Empty;
        AiModel ??= string.Empty;
        AiProvider = string.Equals(AiProvider, AiProviderCustom, StringComparison.OrdinalIgnoreCase)
            ? AiProviderCustom
            : AiProviderOpenCode;

        if (string.IsNullOrWhiteSpace(AiModel))
        {
            AiModel = OpenCodeDefaultModel;
        }

        // AiTopCandidates / AiSampleCues 已不再参与筛选，但仍然收敛一次：
        // 配置页与旧 XML 里可能留着离谱的值，看着别扭。
        AiTopCandidates = Math.Clamp(AiTopCandidates, 2, 5);
        AiPerTier = Math.Clamp(AiPerTier, 1, 6);
        AiSimplifiedAcceptScore = Math.Clamp(AiSimplifiedAcceptScore, 0, 100);
        AiTierOverrideGap = Math.Clamp(AiTierOverrideGap, 0, 40);
        AiAlignedSegments = Math.Clamp(AiAlignedSegments, 3, 10);
        AiAlignedCues = Math.Clamp(AiAlignedCues, 4, 20);
        AiCjkAlignedCues = Math.Clamp(AiCjkAlignedCues, 4, 20);
        AiCjkMaxChars = Math.Clamp(AiCjkMaxChars, 2000, 20_000);
        AiMaxPromptChars = Math.Clamp(AiMaxPromptChars, 4000, 120_000);
        AiMaxCharsPerCandidate = Math.Clamp(AiMaxCharsPerCandidate, 500, 20_000);
        AiTimeoutSeconds = Math.Clamp(AiTimeoutSeconds, 10, 300);
        NearDuplicateThreshold = Math.Clamp(NearDuplicateThreshold, 0.80, 1.0);
        AiCacheDays = Math.Clamp(AiCacheDays, 1, 365);
        AiTypoWarnRate = Math.Clamp(AiTypoWarnRate, 0, 20);
    }
}
