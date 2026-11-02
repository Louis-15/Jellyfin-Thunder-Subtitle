using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>A downloaded, decoded and validated subtitle, ready to be written.</summary>
public sealed record SubtitlePick
{
    /// <summary>Initializes a new instance of the <see cref="SubtitlePick"/> class.</summary>
    /// <param name="candidateName">The name the API reported.</param>
    /// <param name="extension">The whitelisted extension that matches the content.</param>
    /// <param name="text">The subtitle text.</param>
    /// <param name="encodingName">The encoding it was decoded from.</param>
    public SubtitlePick(string candidateName, string extension, string text, string encodingName)
    {
        CandidateName = candidateName;
        Extension = extension;
        Text = text;
        EncodingName = encodingName;
        Stats = SubtitleContent.Measure(text, extension);
    }

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

    /// <summary>Gets every cue of the subtitle, for sampling.</summary>
    /// <returns>The cues.</returns>
    public IReadOnlyList<SubtitleCue> Cues() => SubtitleContent.Parse(Text, Extension);
}

/// <summary>
/// Picks the best candidate. The Xunlei API returns <c>score</c> and
/// <c>fingerprintf_score</c> but both are always 0, so ranking is done locally:
/// machine translations first (worst), then file size (a complete transcript is bigger).
/// </summary>
public static class SubtitleRanker
{
    /// <summary>Markers that identify a batch generated / machine translated subtitle.</summary>
    public static readonly string[] AiMarkers =
    [
        "[mt]",
        "[machine]",
        "ai翻译",
        "ai字幕",
        "ai机翻",
        "ai生成",
        "ai翻译字幕",
        "machine translated",
        "machine translation",
        "machine-translated",
        "whisper",
        "机翻",
        "机器翻译",
        "自动翻译",
        "google translate",
        "谷歌翻译",
        "gpt翻译",
        "chatgpt"
    ];

    /// <summary>Smallest relative gain that counts as an upgrade of an existing subtitle.</summary>
    public const double MinimumGrowthRatio = 0.05;

    /// <summary>Smallest absolute gain, in dialogue characters, that counts as an upgrade.</summary>
    public const int MinimumGrowthChars = 30;

    /// <summary>Counts how many machine-translation markers the name carries.</summary>
    /// <param name="name">The candidate name.</param>
    /// <returns>The marker count.</returns>
    public static int CountAiMarkers(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return 0;
        }

        var lowered = name.ToLowerInvariant();
        return AiMarkers.Count(marker => lowered.Contains(marker, StringComparison.Ordinal));
    }

    /// <summary>Number of bytes the text takes when written as UTF-8 without BOM.</summary>
    /// <param name="text">The subtitle text.</param>
    /// <returns>The byte count.</returns>
    public static long Utf8Length(string text)
    {
        return System.Text.Encoding.UTF8.GetByteCount(SubtitleText.Normalize(text));
    }

    /// <summary>Drops duplicate content and orders the survivors best-first.</summary>
    /// <param name="candidates">Downloaded candidates.</param>
    /// <param name="requestedCode">The code the search used; when set, candidates for other codes are dropped.</param>
    /// <returns>The ranked, de-duplicated candidates.</returns>
    public static IReadOnlyList<SubtitlePick> Rank(IEnumerable<SubtitlePick> candidates, string? requestedCode = null)
    {
        var key = requestedCode is null ? null : JavCode.CompareKey(requestedCode);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        return candidates
            .Where(candidate => seen.Add(candidate.Hash))
            .Select(candidate => (Pick: candidate, Code: JavCode.FromText(candidate.CandidateName)))
            .Where(entry => key is null
                            || entry.Code is null
                            || string.Equals(JavCode.CompareKey(entry.Code), key, StringComparison.OrdinalIgnoreCase))

            // A name that carries the code proves the release; a hash name does not.
            .OrderBy(entry => entry.Code is null ? 1 : 0)
            .ThenBy(entry => entry.Pick.AiMarkers)

            // Dialogue volume, not file size: an ASS wrapper or a BOM must not win.
            .ThenByDescending(entry => entry.Pick.ContentChars)
            .ThenByDescending(entry => entry.Pick.CueCount)
            .ThenBy(entry => entry.Pick.CandidateName, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Pick)
            .ToArray();
    }

    /// <summary>
    /// Reorders a shortlist using an AI verdict. Machine-translated candidates sink to the
    /// bottom, then the higher score wins, then dialogue volume breaks ties.
    /// </summary>
    /// <param name="shortlist">The candidates that were judged.</param>
    /// <param name="verdicts">One verdict per candidate, keyed by the 1-based id used in the prompt.</param>
    /// <returns>The shortlist, best first; the input unchanged when a verdict is missing.</returns>
    public static IReadOnlyList<SubtitlePick> ApplyAiVerdicts(
        IReadOnlyList<SubtitlePick> shortlist,
        IReadOnlyDictionary<int, AiVerdict> verdicts)
    {
        if (shortlist.Count == 0
            || verdicts.Count != shortlist.Count
            || shortlist.Where((_, index) => !verdicts.ContainsKey(index + 1)).Any())
        {
            return shortlist;
        }

        return shortlist
            .Select((pick, index) => (Pick: pick, Verdict: verdicts[index + 1]))
            .OrderBy(entry => entry.Verdict.MachineTranslated ? 1 : 0)
            .ThenByDescending(entry => entry.Verdict.Score)
            .ThenByDescending(entry => entry.Pick.ContentChars)
            .Select(entry => entry.Pick)
            .ToArray();
    }

    /// <summary>
    /// Picks the best candidate that actually belongs to <paramref name="requestedCode"/>.
    /// The API sometimes answers a code search with a neighbouring release (a NIMA-86
    /// search returning a NIMA-014 file); writing that would put another movie's
    /// subtitles on the video, and being bigger it would win a pure size ranking.
    /// Names without any code (hash names, "SSIS706.FHD") are kept: they carry no
    /// evidence either way and are usually the same release.
    /// </summary>
    /// <param name="candidates">The downloaded candidates.</param>
    /// <param name="requestedCode">The code the search used, e.g. <c>ABF-363</c>.</param>
    /// <returns>The best candidate, or <c>null</c> when every candidate is for another code.</returns>
    public static SubtitlePick? SelectBest(IEnumerable<SubtitlePick> candidates, string requestedCode)
    {
        return Rank(candidates, requestedCode).FirstOrDefault();
    }

    /// <summary>
    /// True when the name carries no code, or carries the same code as the search.
    /// Leading zeros are irrelevant: <c>NIMA-086</c> matches <c>NIMA-86</c>.
    /// </summary>
    /// <param name="candidateName">The candidate name reported by the API.</param>
    /// <param name="requestedCode">The code the search used.</param>
    /// <returns><c>true</c> when the candidate may be used.</returns>
    public static bool IsCodeOrUnknown(string? candidateName, string requestedCode)
    {
        var code = JavCode.FromText(candidateName);
        return code is null
               || string.Equals(JavCode.CompareKey(code), JavCode.CompareKey(requestedCode), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the name carries exactly the requested code.</summary>
    /// <param name="candidateName">The candidate name reported by the API.</param>
    /// <param name="requestedCode">The code the search used.</param>
    /// <returns><c>true</c> when the codes match.</returns>
    public static bool HasCode(string? candidateName, string requestedCode)
    {
        return JavCode.FromText(candidateName) is { } code
               && string.Equals(JavCode.CompareKey(code), JavCode.CompareKey(requestedCode), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Says whether <paramref name="candidate"/> should replace the subtitle that is
    /// already on disk.
    /// </summary>
    /// <param name="candidate">The freshly ranked best candidate.</param>
    /// <param name="existing">Dialogue volume of the file on disk.</param>
    /// <param name="existingHash">Hash of the file on disk, when it could be decoded.</param>
    /// <param name="existingAiMarkers">Markers recorded for the file on disk, when known.</param>
    /// <returns><c>true</c> when the candidate is an upgrade.</returns>
    public static bool IsUpgrade(SubtitlePick candidate, SubtitleStats existing, string? existingHash, int? existingAiMarkers)
    {
        if (existingHash is not null && string.Equals(existingHash, candidate.Hash, StringComparison.Ordinal))
        {
            return false;
        }

        // Trading a machine translation for a human subtitle is always worth a rewrite.
        if (existingAiMarkers.HasValue && candidate.AiMarkers < existingAiMarkers.Value)
        {
            return true;
        }

        // Otherwise a re-upload with a slightly longer line is churn, not an upgrade.
        // Compare dialogue characters so an ASS-to-SRT switch does not look like growth.
        var threshold = Math.Max(MinimumGrowthChars, (int)(existing.ContentChars * MinimumGrowthRatio));
        return candidate.ContentChars > existing.ContentChars + threshold;
    }
}
