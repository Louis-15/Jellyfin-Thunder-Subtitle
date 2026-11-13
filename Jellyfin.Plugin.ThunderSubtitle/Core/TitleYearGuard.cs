using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>
/// 片名模式的最后一道筛子：用候选名里的 4 位年份排除「同一系列的另一部」。
///
/// 只处理 <see cref="TitleMatchLevel.None"/> 档 —— 片名或年份已经匹配上的候选不需要它。
/// 真实场景：检索「流浪地球 2019」时，《流浪地球 2》的字幕因为字符数更多而胜出；
/// 中文片名对不上英文候选名、年份也对不上（2019 vs 2023），两份都落进 None 档。
/// </summary>
internal static class TitleYearGuard
{
    /// <summary>
    /// 匹配候选名里的 4 位年份。前后不能再接数字，避免从 <c>20261028</c> 里截出 <c>2026</c>；
    /// <c>1920x1080</c> 这类分辨率在 <see cref="ExtractYears"/> 里另外排除。
    /// </summary>
    private static readonly Regex YearToken = new(
        @"(?<!\d)(19|20)\d{2}(?!\d)",
        RegexOptions.Compiled);

    /// <summary>
    /// 丢掉「明确标了别的年份」的候选。
    ///
    /// 允许 ±1 年的误差：上映年、制作年、不同地区的发行年经常差一年，
    /// 字幕文件名的年份也可能取的是其中一个。
    ///
    /// 全部候选都被排除时返回原样：宁可让续集混进来交给 AI 评审，也不要把候选集
    /// 清空导致这部影片永远没有字幕。
    /// </summary>
    /// <param name="candidates">The candidates in the None tier.</param>
    /// <param name="targetYear">The year Jellyfin knows for the item.</param>
    /// <returns>The candidates that are not clearly for another year.</returns>
    internal static List<(SubtitlePick Pick, TitleMatchLevel Level)> ExcludeConflictingYears(
        List<(SubtitlePick Pick, TitleMatchLevel Level)> candidates,
        int targetYear)
    {
        var kept = candidates.FindAll(entry => !HasConflictingYear(entry.Pick.CandidateName, targetYear));
        return kept.Count > 0 ? kept : candidates;
    }

    /// <summary>
    /// 名字里出现的所有 4 位年份都与 <paramref name="targetYear"/> 相差超过 1 年时返回 true。
    /// 名字里没有任何年份时返回 false —— 没有证据不能当成「冲突」。
    /// </summary>
    /// <param name="name">The candidate name.</param>
    /// <param name="targetYear">The year Jellyfin knows for the item.</param>
    /// <returns><c>true</c> when every year found contradicts the target.</returns>
    internal static bool HasConflictingYear(string? name, int targetYear)
    {
        var found = false;

        foreach (var year in ExtractYears(name))
        {
            found = true;
            if (Math.Abs(year - targetYear) <= 1)
            {
                return false;
            }
        }

        return found;
    }

    /// <summary>提取名字里的 4 位年份，跳过 <c>1920x1080</c> 这类分辨率。</summary>
    private static IEnumerable<int> ExtractYears(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            yield break;
        }

        foreach (Match match in YearToken.Matches(name))
        {
            var after = match.Index + match.Length;
            if (after + 1 < name.Length
                && (name[after] is 'x' or 'X')
                && char.IsDigit(name[after + 1]))
            {
                // 1920x1080 / 1280x720 里的前一个数字不是年份。
                continue;
            }

            yield return int.Parse(match.Value, CultureInfo.InvariantCulture);
        }
    }
}
