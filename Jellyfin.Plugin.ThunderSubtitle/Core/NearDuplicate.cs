using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>
/// 近似重复合并：同一份机翻被不同站点转码、改名、换标点后，内容哈希完全不同，
/// 精确去重拦不住，它们会一起挤进 6 个送审名额（实测 MIUM-1457 就出现了 610 条 3205 字
/// 与 610 条 3200 字两份几乎一样的候选）。
///
/// 做法：把台词归一成「只有字母数字」的长串，取 3-gram 集合算 Jaccard 相似度。
/// 不用编辑距离：字幕动辄三千字，两两算 Levenshtein 是 O(n²) 的平方级开销。
/// </summary>
public static class NearDuplicate
{
    /// <summary>默认阈值。0.99 漏掉改标点的版本，0.90 会把「同一影片的不同翻译」误合并。</summary>
    public const double DefaultThreshold = 0.95;

    /// <summary>条数允许的相对差异。断句不同但内容相同的两份，条数可能差 3% 以内。</summary>
    private const double CueCountTolerance = 0.03;

    /// <summary>台词字数允许的相对差异。转码常去掉或补上几句旁白。</summary>
    private const double ContentCharsTolerance = 0.05;

    /// <summary>
    /// 每份候选最多与几份比较。只比「条数最接近」的那几份：
    /// 同一份字幕的转码条数一定接近，而条数差很远的即使内容相似也不该合并
    /// （那是「完整版 vs 删减版」，合并会丢掉信息）。
    /// </summary>
    private const int MaxComparisons = 5;

    /// <summary>
    /// 合并近似重复的候选，保留每组里排在最前的那份。
    /// </summary>
    /// <param name="ranked">已经排好序的候选（质量高的在前）。顺序决定保留哪一份。</param>
    /// <param name="threshold">相似度阈值，0~1。</param>
    /// <returns>合并后的候选与合并掉的份数。</returns>
    public static (List<SubtitlePick> Kept, int Merged) Collapse(
        IReadOnlyList<SubtitlePick> ranked,
        double threshold = DefaultThreshold)
    {
        if (ranked.Count < 2)
        {
            return (ranked.ToList(), 0);
        }

        var clamped = Math.Clamp(threshold, 0.5, 1.0);

        // 1.0 表示「只合并完全相同的」——那种情况精确哈希已经拦过，这里直接跳过。
        if (clamped >= 1.0)
        {
            return (ranked.ToList(), 0);
        }

        var kept = new List<SubtitlePick>(ranked.Count);
        var keptTrigrams = new Dictionary<SubtitlePick, HashSet<string>>(ReferenceEqualityComparer.Instance);
        var merged = 0;

        foreach (var pick in ranked)
        {
            if (IsNearDuplicateOf(pick, kept, keptTrigrams, clamped))
            {
                merged++;
                continue;
            }

            kept.Add(pick);
        }

        return (kept, merged);
    }

    /// <summary>
    /// 判断一份候选是否与「已保留的某一份」近似重复。
    ///
    /// 只和条数最接近的 <see cref="MaxComparisons"/> 份比较：
    /// 100 份候选两两比较是 4950 次，每次都要建 3000 个 3-gram，
    /// 而真正的重复几乎一定落在条数邻近的那几份里。
    /// </summary>
    private static bool IsNearDuplicateOf(
        SubtitlePick pick,
        List<SubtitlePick> kept,
        Dictionary<SubtitlePick, HashSet<string>> keptTrigrams,
        double threshold)
    {
        var neighbours = kept
            .Where(existing => IsComparable(existing, pick))
            .OrderBy(existing => Math.Abs(existing.CueCount - pick.CueCount))
            .Take(MaxComparisons)
            .ToList();

        if (neighbours.Count == 0)
        {
            return false;
        }

        HashSet<string>? mine = null;
        foreach (var existing in neighbours)
        {
            if (!keptTrigrams.TryGetValue(existing, out var theirs))
            {
                theirs = Trigrams(existing);
                keptTrigrams[existing] = theirs;
            }

            mine ??= Trigrams(pick);
            if (Jaccard(mine, theirs) >= threshold)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 粗筛：条数与字数都要接近才值得算相似度。
    /// 这一步把「100 份两两比较」压到「每份最多 5 次」。
    /// </summary>
    private static bool IsComparable(SubtitlePick left, SubtitlePick right)
    {
        if (left.CueCount == 0 || right.CueCount == 0)
        {
            return false;
        }

        var cueDiff = Math.Abs(left.CueCount - right.CueCount) / (double)Math.Max(left.CueCount, right.CueCount);
        if (cueDiff > CueCountTolerance)
        {
            return false;
        }

        if (left.ContentChars == 0 || right.ContentChars == 0)
        {
            return false;
        }

        var charDiff = Math.Abs(left.ContentChars - right.ContentChars) / (double)Math.Max(left.ContentChars, right.ContentChars);
        return charDiff <= ContentCharsTolerance;
    }

    /// <summary>
    /// 把台词归一成 3-gram 集合。
    ///
    /// 归一化只保留字母数字并转小写：标点、空格、换行、全角半角的差异都不该影响判断。
    /// 不做繁简转换 —— 简体版和繁体版是「不同的翻译」，合并掉会丢掉用户可能更想要的那份。
    /// </summary>
    private static HashSet<string> Trigrams(SubtitlePick pick)
    {
        var builder = new StringBuilder(pick.ContentChars);
        foreach (var cue in pick.Cues())
        {
            foreach (var ch in cue.Text)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    builder.Append(char.ToLowerInvariant(ch));
                }
            }
        }

        var text = builder.ToString();
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i + 3 <= text.Length; i++)
        {
            set.Add(text.Substring(i, 3));
        }

        return set;
    }

    private static double Jaccard(HashSet<string> left, HashSet<string> right)
    {
        if (left.Count == 0 || right.Count == 0)
        {
            return 0;
        }

        // 用较小的集合做遍历，减少哈希查找次数。
        var (small, large) = left.Count <= right.Count ? (left, right) : (right, left);

        var intersection = 0;
        foreach (var item in small)
        {
            if (large.Contains(item))
            {
                intersection++;
            }
        }

        var union = left.Count + right.Count - intersection;
        return union == 0 ? 0 : intersection / (double)union;
    }
}
