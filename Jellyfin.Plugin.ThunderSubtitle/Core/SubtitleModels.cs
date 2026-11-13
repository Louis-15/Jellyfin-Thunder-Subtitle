using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>A downloaded, decoded and validated subtitle, ready to be written.</summary>
public sealed record SubtitlePick
{
    private readonly IReadOnlyList<SubtitleCue> _cues;

    /// <summary>Initializes a new instance of the <see cref="SubtitlePick"/> class.</summary>
    /// <param name="candidateName">The name the API reported.</param>
    /// <param name="extension">The whitelisted extension that matches the content.</param>
    /// <param name="text">The subtitle text.</param>
    /// <param name="encodingName">The encoding it was decoded from.</param>
    /// <param name="uri">Where the file came from, used to download it again by id.</param>
    public SubtitlePick(string candidateName, string extension, string text, string encodingName, Uri? uri = null)
    {
        CandidateName = candidateName;
        Extension = extension;
        Text = text;
        EncodingName = encodingName;
        Uri = uri;

        // 解析一次、三处复用：台词量、语言检测、AI 抽样都要遍历 cue。
        // 一份 3000 条的 ASS 解析一次约 2~5 ms，25 份候选重复解析会白烧 100 ms。
        _cues = SubtitleContent.Parse(text, extension);
        Stats = SubtitleContent.Measure(_cues, text);
        Language = SubtitleLanguageDetector.Detect(_cues, SubtitleContent.StripMarkup(text));

        // 健康度必须在构造时算完：排序和短名单筛选都要用，而它们会对同一份候选
        // 反复比较。一次遍历 1600 条约 0.2 ms，比在比较器里反复扫描便宜得多。
        Health = SubtitleHealth.Scan(_cues);
    }

    /// <summary>Gets the source URI, when the pick came from the API.</summary>
    public Uri? Uri { get; }

    /// <summary>Gets the name the API reported, shown in the log.</summary>
    public string CandidateName { get; }

    /// <summary>Gets the whitelisted extension that matches the content.</summary>
    public string Extension { get; }

    /// <summary>Gets the subtitle text.</summary>
    public string Text { get; }

    /// <summary>Gets the encoding it was decoded from.</summary>
    public string EncodingName { get; }

    /// <summary>Gets the dialogue volume, the format independent measure used for ranking.</summary>
    public SubtitleStats Stats { get; }

    /// <summary>Gets the detected language of the subtitle.</summary>
    public SubtitleLanguage Language { get; }

    /// <summary>Gets the number of machine-translation markers found in the candidate name.</summary>
    public int AiMarkers => SubtitleRanker.CountAiMarkers(CandidateName);

    /// <summary>Gets the size of the file as it will be written (UTF-8, no BOM).</summary>
    public long Bytes => SubtitleRanker.Utf8Length(Text);

    /// <summary>Gets the CRLF-insensitive content hash.</summary>
    public string Hash => SubtitleText.Hash(Text);

    /// <summary>Gets the dialogue character count; higher means more subtitle content.</summary>
    public int ContentChars => Stats.ContentChars;

    /// <summary>Gets the number of dialogue lines.</summary>
    public int CueCount => Stats.CueCount;

    /// <summary>
    /// Gets the automatically measurable defects of this candidate.
    /// 只用来淘汰明显有毛病的候选，不用来排名 —— 具体原因见 <see cref="SubtitleHealth"/>。
    /// </summary>
    public SubtitleHealth Health { get; }

    /// <summary>Gets every cue of the subtitle, parsed once at construction.</summary>
    /// <returns>The cues.</returns>
    public IReadOnlyList<SubtitleCue> Cues() => _cues;
}

/// <summary>What is already on disk for an item.</summary>
/// <param name="Stats">Dialogue volume.</param>
/// <param name="Language">Detected language.</param>
/// <param name="Hash">Content hash, when the file could be decoded.</param>
/// <param name="AiMarkers">Machine-translation markers, when the provenance ledger knows them.</param>
/// <param name="AiScore">AI 分数，台账里记过才有。</param>
/// <param name="AiSource">human / ai / asr / mixed，台账里记过才有。</param>
/// <param name="TypoCount">中文影视的错别字数量，台账里记过才有。</param>
public sealed record ExistingSubtitle(
    SubtitleStats Stats,
    SubtitleLanguage Language,
    string? Hash,
    int? AiMarkers,
    double? AiScore = null,
    string AiSource = "",
    int TypoCount = 0);

/// <summary>What the model decided about one candidate.</summary>
/// <param name="Id">The 1-based id used in the prompt.</param>
/// <param name="Score">Usability score, 0-100, higher is better.</param>
/// <param name="MachineTranslated">Whether the model believes it is machine translated or auto-captioned.</param>
/// <param name="Reason">Short justification, kept for the log.</param>
/// <param name="Source">
/// <c>human</c> / <c>ai</c> / <c>asr</c> / <c>mixed</c>。
/// 空字符串表示模型没返回（旧模型），此时由 <see cref="MachineTranslated"/> 推断。
/// </param>
/// <param name="Typos">中文影视专用：抽样台词里发现的错别字数量。</param>
/// <param name="Evidence">模型从送审台词里引用的原句，校验失败时为空。</param>
/// <param name="EvidenceVerified">引用的原句是否真的出现在送审文本里。</param>
public sealed record AiVerdict(
    int Id,
    double Score,
    bool MachineTranslated,
    string Reason,
    string Source = "",
    int Typos = 0,
    string Evidence = "",
    bool EvidenceVerified = false)
{
    /// <summary>
    /// 归一化后的来源标签：模型返回了就用它，没返回时按 <see cref="MachineTranslated"/> 推断。
    /// </summary>
    public string EffectiveSource => string.IsNullOrWhiteSpace(Source)
        ? (MachineTranslated ? "ai" : "human")
        : Source;
}
