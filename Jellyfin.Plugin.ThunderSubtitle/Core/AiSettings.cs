namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>Connection and sampling settings for the OpenAI-compatible endpoint.</summary>
/// <param name="BaseUrl">API root, e.g. <c>https://opencode.ai/inference/go/openai/v1</c>.</param>
/// <param name="ApiKey">Bearer token; empty for a local server that needs none.</param>
/// <param name="Model">Model name.</param>
/// <param name="SegmentCount">对齐抽样取几个片段。</param>
/// <param name="CuesPerSegment">每个片段连续取几条台词。</param>
/// <param name="MaxCharsPerCandidate">每份候选的抽样字数上限。</param>
/// <param name="TimeoutSeconds">Per-request timeout.</param>
/// <param name="CjkCuesPerSegment">中文影视每个片段取几条（错别字稀疏，需要更密）。</param>
/// <param name="CjkMaxChars">中文影视每份候选的字数上限。</param>
/// <param name="MaxPromptChars">user prompt 字符上限，超出时自动缩减片段。</param>
/// <param name="SessionId">
/// Stable id sent as <c>x-opencode-session</c>. OpenCode Go requires it and uses it for
/// routing and prompt caching; other providers ignore the header, so it is only sent
/// when the built-in provider is selected.
/// </param>
/// <param name="UseSessionHeader">Whether to send the <c>x-opencode-session</c> header.</param>
public sealed partial record AiSettings(
    string BaseUrl,
    string ApiKey,
    string Model,
    int SegmentCount,
    int CuesPerSegment,
    int MaxCharsPerCandidate,
    int TimeoutSeconds,
    int CjkCuesPerSegment = 10,
    int CjkMaxChars = 6000,
    int MaxPromptChars = 24000,
    string SessionId = "",
    bool UseSessionHeader = false)
{
    /// <summary>按影片类型取每段台词条数。</summary>
    /// <param name="kind">The media kind.</param>
    /// <returns>Cues per segment.</returns>
    public int CuesFor(MediaKind kind) => kind == MediaKind.Chinese ? CjkCuesPerSegment : CuesPerSegment;

    /// <summary>按影片类型取每份候选的字数上限。</summary>
    /// <param name="kind">The media kind.</param>
    /// <returns>Character budget per candidate.</returns>
    public int MaxCharsFor(MediaKind kind) => kind == MediaKind.Chinese ? CjkMaxChars : MaxCharsPerCandidate;
}
