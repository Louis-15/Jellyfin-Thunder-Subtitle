using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>
/// Picks the best candidate. The Xunlei API returns <c>score</c> and
/// <c>fingerprintf_score</c> but both are always 0, so ranking is done locally.
/// </summary>
public static partial class SubtitleRanker
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

    /// <summary>Result of dropping the candidates whose language is not acceptable.</summary>
    /// <param name="Kept">Candidates that may enter the comparison.</param>
    /// <param name="Dropped">How many of each language were dropped, for the log.</param>
    public sealed record LanguageFilterResult(
        IReadOnlyList<SubtitlePick> Kept,
        IReadOnlyDictionary<SubtitleLanguage, int> Dropped);

    /// <summary>
    /// Keeps only the language forms this media kind can use.
    ///
    /// 这一步必须排在排序之前：英文字幕每 KB 的字母数远高于中文，
    /// 先按字符数排序会让英文字幕天然占优，中文反而被挤掉。
    ///
    /// 三类影片都接受「简 / 繁 / 简英双语 / 繁英双语」四种形式，
    /// 区别只在 <see cref="LanguagePolicy"/> 给出的档位不同，所以过滤规则本身是共用的；
    /// 之所以仍按类型传参，是为了将来出现「某类影片不接受某种形式」时不用改调用方。
    /// </summary>
    /// <param name="kind">The media kind.</param>
    /// <param name="candidates">Downloaded candidates.</param>
    /// <returns>The acceptable candidates and the per-language drop counts.</returns>
    public static LanguageFilterResult KeepAcceptable(MediaKind kind, IEnumerable<SubtitlePick> candidates)
    {
        var kept = new List<SubtitlePick>();
        var dropped = new Dictionary<SubtitleLanguage, int>();

        foreach (var candidate in candidates)
        {
            if (LanguagePolicy.IsAcceptable(kind, candidate.Language))
            {
                kept.Add(candidate);
                continue;
            }

            dropped[candidate.Language] = dropped.TryGetValue(candidate.Language, out var count) ? count + 1 : 1;
        }

        return new LanguageFilterResult(kept, dropped);
    }

    /// <summary>
    /// Drops duplicate content and orders the survivors best-first.
    /// </summary>
    /// <param name="candidates">Downloaded candidates.</param>
    /// <param name="kind">The media kind, which decides the language tier order.</param>
    /// <param name="requestedCode">The code the search used; when set, candidates for other codes are dropped.</param>
    /// <returns>The ranked, de-duplicated candidates.</returns>
    public static IReadOnlyList<SubtitlePick> Rank(
        IEnumerable<SubtitlePick> candidates,
        MediaKind kind,
        string? requestedCode = null)
    {
        var key = requestedCode is null ? null : ReleaseCode.CompareKey(requestedCode);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var kept = candidates
            .Where(candidate => seen.Add(candidate.Hash))
            .Select(candidate => (Pick: candidate, Code: ReleaseCode.FromText(candidate.CandidateName)))
            .Where(entry => key is null
                            || entry.Code is null
                            || string.Equals(ReleaseCode.CompareKey(entry.Code), key, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 中位数要在排序前算好：它代表「这一批候选的常见体量」，
        // 是后面衡量每份偏离多少的基准。
        var median = MedianCueCount(kept.Select(entry => entry.Pick));

        return kept

            // 语言档位是第一键：用户明确要求「有双语就用双语 / 有简体就用简体」。
            // 番号证据放在第二键：它只是「无法证明属于别的影片」，不是正确性硬条件，
            // 所以同档位内优先取名字带番号那份，跨档位时不牺牲语言偏好。
            .OrderBy(entry => LanguagePolicy.TierOf(kind, entry.Pick.Language))
            .ThenBy(entry => entry.Code is null ? 1 : 0)
            .ThenBy(entry => entry.Pick.AiMarkers)

            // 缺陷少的优先。触了红线的正常已经在 DropUnusable 里删掉，
            // 这里再排一次是为了兜底：整批都触线时会退回全部候选，那时也该是
            // 「毛病少的在前」，而不是按谁更长。
            .ThenBy(entry => entry.Pick.Health.Defects)

            // 条数接近本批中位数的优先。偏离太多意味两种毛病之一：
            // 明显偏少 = 大段漏译，明显偏多 = 一句台词拆成三段（AI 机翻的典型特征）。
            // 这一键取代了原来的「字数从多到少」—— 字数多从来不代表质量好，
            // 双语字幕因为夹着整行英文天然字数翻倍，纯中文的国配台本反而排在后面。
            .ThenBy(entry => DeviationBucket(entry.Pick, median))

            // 同一偏离档位内，才让「国配 / 官方 / 院线」这类名字信号起作用。
            // 它只当微调：文件名是发布者自己写的，不能凭它单独定胜负。
            .ThenByDescending(entry => CountQualityKeywords(entry.Pick.CandidateName))

            // 前面全都相等时的兜底：条数多的在前，保持输出稳定。
            .ThenByDescending(entry => entry.Pick.CueCount)
            .ThenBy(entry => entry.Pick.CandidateName, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Pick)
            .ToArray();
    }

    /// <summary>
    /// 片名模式的排序：先按「片名证据」分档，再按语言档位与质量。
    ///
    /// 与番号模式的区别：番号是硬标识，对不上就可以直接丢掉；
    /// 片名没有硬标识，中文片名查出来常常是英文命名的候选（流浪地球 →
    /// The.Wandering.Earth），所以证据只用来自动收窄范围：
    /// 有片名/季集匹配就只留这一档，只有年份匹配就留年份匹配的，
    /// 两者都没有时全部保留（按质量排），否则中文片名的库几乎一条都选不出来。
    /// </summary>
    /// <param name="candidates">Downloaded candidates.</param>
    /// <param name="target">The title search target.</param>
    /// <param name="kind">The media kind, which decides the language tier order.</param>
    /// <returns>The ranked, de-duplicated candidates.</returns>
    public static IReadOnlyList<SubtitlePick> RankForTitle(
        IEnumerable<SubtitlePick> candidates,
        SearchTarget target,
        MediaKind kind)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var scored = candidates
            .Where(candidate => seen.Add(candidate.Hash))
            .Select(candidate => (Pick: candidate, Level: TitleMatch.Level(candidate.CandidateName, target)))
            .ToList();

        if (scored.Count == 0)
        {
            return Array.Empty<SubtitlePick>();
        }

        var best = scored.Max(entry => entry.Level);
        var kept = best == TitleMatchLevel.None
            ? scored
            : scored.Where(entry => entry.Level == best).ToList();

        // 片名档无法排除「同一系列的另一部」：中文片名对不上英文候选名，
        // 年份也对不上（流浪地球 2019 vs The.Wandering.Earth.2.2023），
        // 于是两份都落进 None 档，续集因为字符数更多而胜出。
        // 这里用「候选名标注的年份」做最后一道筛子：只要所有年份都与目标相差 > 1 年，
        // 就认为它不是这部影片。
        if (best == TitleMatchLevel.None && target.Year is > 0)
        {
            kept = TitleYearGuard.ExcludeConflictingYears(kept, target.Year.Value);
        }

        var median = MedianCueCount(kept.Select(entry => entry.Pick));

        return kept
            .OrderByDescending(entry => (int)entry.Level)
            .ThenBy(entry => LanguagePolicy.TierOf(kind, entry.Pick.Language))
            .ThenBy(entry => entry.Pick.AiMarkers)

            // 与番号模式同样的取舍：先看毛病多少，再看条数是否偏离同批常见体量，
            // 最后才用名字里的「国配 / 官方」做微调。字数不再参与排序。
            .ThenBy(entry => entry.Pick.Health.Defects)
            .ThenBy(entry => DeviationBucket(entry.Pick, median))
            .ThenByDescending(entry => CountQualityKeywords(entry.Pick.CandidateName))
            .ThenByDescending(entry => entry.Pick.CueCount)
            .ThenBy(entry => entry.Pick.CandidateName, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Pick)
            .ToArray();
    }

    /// <summary>
    /// Reorders a shortlist using an AI verdict.
    ///
    /// 顺序是「语言档位 → 分数 → 来源 → 字数」：
    /// 档位由系统决定（用户明确要求语言优先），分数是质量的主要依据，
    /// 来源只在分数相同时起作用 —— 用户的原话是「最终的目的是翻译质量高」，
    /// 所以一份 85 分的 AI 翻译要排在 70 分的人工翻译之前。
    ///
    /// 日本AV 额外有「质量降级」：如果首选档（简体）的最高分低于
    /// <paramref name="simplifiedAcceptScore"/>，而次选档（繁体）有更高的分数，
    /// 就把首选档整体降级 —— 这是用户「找不到合适的简体才用繁体」的落地方式。
    /// 其他两类影片不做降级：只要存在双语 / 简体，就一定用它。
    /// </summary>
    /// <param name="shortlist">The candidates that were judged.</param>
    /// <param name="verdicts">One verdict per candidate, keyed by the 1-based id used in the prompt.</param>
    /// <param name="kind">The media kind, which decides the language tier order.</param>
    /// <param name="simplifiedAcceptScore">
    /// 日本AV 简体档的合格分；0 表示关闭降级。其他类型忽略此参数。
    /// </param>
    /// <param name="tierOverrideGap">
    /// 非中文影视的跨档择优分差；0 表示关闭。其他类型忽略此参数。
    /// </param>
    /// <returns>The shortlist, best first; the input unchanged when a verdict is missing.</returns>
    public static IReadOnlyList<SubtitlePick> ApplyAiVerdicts(
        IReadOnlyList<SubtitlePick> shortlist,
        IReadOnlyDictionary<int, AiVerdict> verdicts,
        MediaKind kind,
        int simplifiedAcceptScore = 0,
        int tierOverrideGap = 0)
    {
        if (shortlist.Count == 0
            || verdicts.Count != shortlist.Count
            || shortlist.Where((_, index) => !verdicts.ContainsKey(index + 1)).Any())
        {
            return shortlist;
        }

        var scored = shortlist
            .Select((pick, index) => (Pick: pick, Verdict: verdicts[index + 1]))
            .ToList();

        var tiers = new Dictionary<SubtitlePick, int>(ReferenceEqualityComparer.Instance);
        foreach (var entry in scored)
        {
            tiers[entry.Pick] = LanguagePolicy.TierOf(kind, entry.Pick.Language);
        }

        if (kind == MediaKind.JapaneseAv && simplifiedAcceptScore > 0)
        {
            DemoteUnacceptableSimplified(scored, tiers, simplifiedAcceptScore);
        }
        else if (kind == MediaKind.Foreign && tierOverrideGap > 0)
        {
            PromoteBetterLowerTier(scored, tiers, tierOverrideGap);
        }

        return scored
            .OrderBy(entry => tiers[entry.Pick])
            .ThenByDescending(entry => entry.Verdict.Score)
            .ThenBy(entry => SourceRank(entry.Verdict.EffectiveSource))
            .ThenByDescending(entry => entry.Pick.ContentChars)
            .Select(entry => entry.Pick)
            .ToArray();
    }

    /// <summary>
    /// 来源标签的排序权重：人工最好，机翻后校对次之，纯机翻再次，语音转写最后。
    /// 只在分数相同时起作用，绝不让「人翻 60 分」压过「AI 翻 90 分」。
    /// </summary>
    private static int SourceRank(string source) => source switch
    {
        "human" => 0,
        "mixed" => 1,
        "ai" => 2,
        "asr" => 3,
        _ => 2
    };

    /// <summary>
    /// 「找不到合适的简体就用繁体」：首选档（Tier 0）的最高分低于阈值、
    /// 且次选档（Tier 1）确实更好时，把首选档整体排到次选档之后。
    /// 只改排序，不删除候选 —— 万一模型判断失误，那份简体仍然在列表里。
    /// </summary>
    private static void DemoteUnacceptableSimplified(
        IReadOnlyList<(SubtitlePick Pick, AiVerdict Verdict)> scored,
        Dictionary<SubtitlePick, int> tiers,
        int threshold)
    {
        var tier0 = scored.Where(entry => tiers[entry.Pick] == 0).ToList();
        var tier1 = scored.Where(entry => tiers[entry.Pick] == 1).ToList();

        if (tier0.Count == 0 || tier1.Count == 0)
        {
            return;
        }

        var bestPreferred = tier0.Max(entry => entry.Verdict.Score);
        var bestFallback = tier1.Max(entry => entry.Verdict.Score);

        if (bestPreferred >= threshold || bestFallback <= bestPreferred)
        {
            return;
        }

        foreach (var entry in tier0)
        {
            tiers[entry.Pick] = DemotedTier;
        }
    }

    /// <summary>降级后的排序键：排在所有正常档位之后。</summary>
    private const int DemotedTier = 99;

    /// <summary>
    /// 非中文影视的跨档择优：Tier 0（简英双语）本来优先，但如果某个更低档位的候选
    /// 分数明显更高，说明那份译文质量确实好到值得放弃双语对照。
    ///
    /// 只提升分数最高的那一份，而且差距必须够大（默认 8 分）。
    /// 为什么需要这么高的门槛：AI 打分有主观波动，实测《加勒比海盗》8 份候选的分数
    /// 挤在 72～81 这 9 分区间里，三、五分的差距不足以为此推翻语言偏好；
    /// 而这一次 81（国配台本）vs 73（简英双语）差 8 分，人工核对也确认国配台本更好，
    /// 说明 8 分确实代表「质量档位不同」。
    ///
    /// 只改这一份的档位，不整体提升低档位 —— 否则一份 81 分会把同档位 60 分的兄弟
    /// 一起带上来，反而挤掉本该优先的双语版本。
    /// </summary>
    private static void PromoteBetterLowerTier(
        IReadOnlyList<(SubtitlePick Pick, AiVerdict Verdict)> scored,
        Dictionary<SubtitlePick, int> tiers,
        int gap)
    {
        var best = tiers.Values.Min();

        var preferred = scored.Where(entry => tiers[entry.Pick] == best).ToList();
        var challengers = scored.Where(entry => tiers[entry.Pick] > best).ToList();

        if (preferred.Count == 0 || challengers.Count == 0)
        {
            return;
        }

        var bestPreferred = preferred.Max(entry => entry.Verdict.Score);
        var winner = challengers.MaxBy(entry => entry.Verdict.Score);

        if (winner.Pick is null || winner.Verdict.Score - bestPreferred < gap)
        {
            return;
        }

        tiers[winner.Pick] = best;
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
    public static SubtitlePick? SelectBest(IEnumerable<SubtitlePick> candidates, MediaKind kind, string requestedCode)
    {
        return Rank(candidates, kind, requestedCode).FirstOrDefault();
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
        var code = ReleaseCode.FromText(candidateName);
        return code is null
               || string.Equals(ReleaseCode.CompareKey(code), ReleaseCode.CompareKey(requestedCode), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the name carries exactly the requested code.</summary>
    /// <param name="candidateName">The candidate name reported by the API.</param>
    /// <param name="requestedCode">The code the search used.</param>
    /// <returns><c>true</c> when the codes match.</returns>
    public static bool HasCode(string? candidateName, string requestedCode)
    {
        return ReleaseCode.FromText(candidateName) is { } code
               && string.Equals(ReleaseCode.CompareKey(code), ReleaseCode.CompareKey(requestedCode), StringComparison.OrdinalIgnoreCase);
    }
}
