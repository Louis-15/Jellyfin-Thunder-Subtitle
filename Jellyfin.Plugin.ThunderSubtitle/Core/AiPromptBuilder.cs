using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>
/// 把「短名单 + 设置」变成「一整条 user prompt」。
///
/// 与 <see cref="AiSubtitleJudge"/> 分开放，是为了让离线工具能生成和线上逐字节一致的 prompt：
/// prompt 一旦被重新拼一份简化版去核对，核对结果就没有意义了。
/// 这里不碰 HTTP、不碰日志，纯函数。
/// </summary>
internal static class AiPromptBuilder
{
    /// <summary>
    /// 组装 user prompt；超长时按「先减片段、再减每段条数」的顺序收缩。
    ///
    /// 不截断到半句：模型看到残缺台词会给出更差的判断，
    /// 宁可少看两个片段，也要保证看到的每一句都是完整的。
    /// </summary>
    /// <param name="kind">The media kind.</param>
    /// <param name="brief">影片元数据卡。</param>
    /// <param name="shortlist">Candidates to compare.</param>
    /// <param name="settings">Endpoint and sampling settings.</param>
    /// <param name="extraInstruction">用户额外要求。</param>
    /// <param name="tolerance">锚点与最近 cue 的最大偏差。</param>
    /// <returns>The prompt plus the segments it was built from, or <c>null</c>.</returns>
    internal static JudgedPrompt? Compose(
        MediaKind kind,
        MediaBrief? brief,
        IReadOnlyList<SubtitlePick> shortlist,
        AiSettings settings,
        string? extraInstruction,
        TimeSpan tolerance)
    {
        var segments = Math.Clamp(settings.SegmentCount, 3, 10);
        var cues = Math.Clamp(settings.CuesFor(kind), 4, 20);
        var maxChars = settings.MaxCharsFor(kind);

        // 最多收缩 8 次：片段 3~10、每段 4~20，够走到很紧的预算了。
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var sampled = AlignedSampler.Build(shortlist, segments, cues, tolerance, maxChars);
            if (sampled.Count == 0)
            {
                return null;
            }

            var prompt = AiPrompts.Build(brief, shortlist, sampled, extraInstruction);
            if (prompt is null)
            {
                return null;
            }

            if (prompt.Length <= settings.MaxPromptChars)
            {
                return new JudgedPrompt(prompt, sampled);
            }

            if (segments > 3)
            {
                segments--;
            }
            else if (cues > 4)
            {
                cues--;
            }
            else
            {
                break;
            }
        }

        return null;
    }
}

/// <summary>组装好的送审内容：user prompt 与它引用的对齐片段。</summary>
/// <param name="UserPrompt">The user message content.</param>
/// <param name="Segments">The sampled segments it was built from, for logging.</param>
internal sealed record JudgedPrompt(string UserPrompt, IReadOnlyList<SampleSegment> Segments);
