using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>
/// 片名模式下一条候选与目标影片的吻合程度，从强到弱。
/// 排序时只用「本次最好的那一档」，避免把毫无关系的候选混进对比。
/// </summary>
public enum TitleMatchLevel
{
    /// <summary>名字里既没有片名也没有年份，无法核对（通常是哈希命名或纯英文别名）。</summary>
    None = 0,

    /// <summary>只有年份对得上。</summary>
    Year = 1,

    /// <summary>片名或季集标记对得上。</summary>
    Title = 2
}

/// <summary>
/// 判断迅雷返回的候选文件名是否属于目标影片。
///
/// 番号模式靠番号一对一核对；片名模式没有这种硬标识，只能用文件名里的片名、
/// 年份、季集标记做证据。证据强度分三档，取本次候选里最强的一档：
/// 有片名匹配就只留片名匹配的，否则退到年份匹配，两者都没有时全部保留并告警。
/// </summary>
public static class TitleMatch
{
    /// <summary>字幕文件后缀，归一化时先去掉，避免它被当成片名里的一个词。</summary>
    private static readonly string[] SubtitleExtensions = [".srt", ".ass", ".ssa", ".sub", ".idx", ".vtt"];

    private static readonly Regex Bracketed = new(@"\[[^\]]*\]|【[^】]*】|\([^)]*\)", RegexOptions.Compiled);

    /// <summary>匹配 <c>S01E02</c>、<c>S01 E02</c>、<c>S01.E02</c>。</summary>
    private static readonly Regex DotEpisodeTag = new(
        @"(?<![a-z0-9])s(?<s>\d{1,2})[ ]*e(?<e>\d{1,3})(?![0-9])",
        RegexOptions.Compiled);

    /// <summary>匹配 <c>1x02</c> 这种老写法。</summary>
    private static readonly Regex CrossEpisodeTag = new(
        @"(?<![a-z0-9])(?<s>\d{1,2})x(?<e>\d{1,3})(?![0-9])",
        RegexOptions.Compiled);

    /// <summary>判断一条候选名字对目标的吻合程度。</summary>
    /// <param name="candidateName">候选名（API 返回的文件名）。</param>
    /// <param name="target">本次检索目标；番号模式直接返回 <see cref="TitleMatchLevel.None"/>。</param>
    /// <returns>The evidence level.</returns>
    public static TitleMatchLevel Level(string? candidateName, SearchTarget target)
    {
        if (target.Mode != SubtitleSearchMode.Title)
        {
            return TitleMatchLevel.None;
        }

        var name = Normalize(candidateName);
        if (name.Length == 0)
        {
            return TitleMatchLevel.None;
        }

        // 季集标记是剧集最硬的证据：中文剧名查出来常常是英文命名的候选，
        // 片名对不上，但 S01E02 一定对得上。
        //
        // 反过来也成立：候选明确标了「别的集」时，即使片名是同一部剧，
        // 也不能算匹配 —— 否则会把 S01E03 的字幕写到 S01E02 上。
        if (target.IsEpisode && TryReadEpisodeTag(name, out var season, out var episode))
        {
            return season == target.Season && episode == target.Episode
                ? TitleMatchLevel.Title
                : TitleMatchLevel.None;
        }

        if (target.Title is { Length: > 0 } title && TitleAppears(name, title))
        {
            return TitleMatchLevel.Title;
        }

        if (target.Year is int year && HasYear(name, year))
        {
            return TitleMatchLevel.Year;
        }

        return TitleMatchLevel.None;
    }

    /// <summary>
    /// 归一化文件名：去目录、去字幕后缀、去方括号标记，把 <c>.</c> <c>_</c> <c>-</c>
    /// 之类的分隔符统一换成空格并转小写。片名比对全部在这个形式上做，
    /// 这样 <c>The.Matrix.1999</c> 与 <c>The Matrix 1999</c> 等价。
    /// </summary>
    /// <param name="name">Raw name.</param>
    /// <returns>The normalized name.</returns>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var text = name.Trim();

        var slash = text.LastIndexOfAny(['\\', '/']);
        if (slash >= 0)
        {
            text = text[(slash + 1)..];
        }

        foreach (var extension in SubtitleExtensions)
        {
            if (text.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                text = text[..^extension.Length];
                break;
            }
        }

        text = Bracketed.Replace(text, " ");

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            builder.Append(ch switch
            {
                '.' or '_' or '-' or '·' or '—' or '–' or '－' => ' ',
                _ => char.IsWhiteSpace(ch) ? ' ' : char.ToLowerInvariant(ch)
            });
        }

        return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
    }

    /// <summary>
    /// 判断归一化后的文件名里是否出现目标片名。
    /// 含中日韩字符时按「去掉空格后的子串」比较；纯拉丁片名按词比较：
    /// 长词（≥4 字符）必须全部出现，「The」这类短词可以缺省。
    /// </summary>
    /// <param name="normalizedName">A name processed by <see cref="Normalize"/>.</param>
    /// <param name="title">The target title.</param>
    /// <returns><c>true</c> when the title appears.</returns>
    public static bool TitleAppears(string normalizedName, string title)
    {
        var cleanTitle = Normalize(title);
        if (cleanTitle.Length == 0 || normalizedName.Length == 0)
        {
            return false;
        }

        if (HasCjk(cleanTitle))
        {
            return normalizedName.Replace(" ", string.Empty, StringComparison.Ordinal)
                .Contains(cleanTitle.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        }

        var titleWords = cleanTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var significant = titleWords.Where(word => word.Length >= 4).ToArray();
        var required = significant.Length > 0
            ? significant
            : titleWords.Where(word => word.Length >= 2).ToArray();

        if (required.Length == 0)
        {
            return false;
        }

        var nameWords = new HashSet<string>(
            normalizedName.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            StringComparer.Ordinal);

        return required.All(nameWords.Contains);
    }

    private static bool TryReadEpisodeTag(string normalizedName, out int season, out int episode)
    {
        foreach (var match in DotEpisodeTag.Matches(normalizedName).Cast<Match>())
        {
            if (TryParseNumbers(match, out season, out episode))
            {
                return true;
            }
        }

        foreach (var match in CrossEpisodeTag.Matches(normalizedName).Cast<Match>())
        {
            if (TryParseNumbers(match, out season, out episode))
            {
                return true;
            }
        }

        season = 0;
        episode = 0;
        return false;
    }

    private static bool TryParseNumbers(Match match, out int season, out int episode)
    {
        var hasSeason = int.TryParse(match.Groups["s"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out season);
        var hasEpisode = int.TryParse(match.Groups["e"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out episode);
        return hasSeason && hasEpisode;
    }

    private static bool HasYear(string normalizedName, int year)
    {
        var token = year.ToString(CultureInfo.InvariantCulture);
        return normalizedName
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(word => string.Equals(word, token, StringComparison.Ordinal));
    }

    private static bool HasCjk(string text)
    {
        foreach (var ch in text)
        {
            if (ch is >= '\u3040' and <= '\u30ff'    // 日文假名
                or >= '\u3400' and <= '\u4dbf'        // 扩展 A
                or >= '\u4e00' and <= '\u9fff'        // 基本汉字
                or >= '\uf900' and <= '\ufaff')       // 兼容汉字
            {
                return true;
            }
        }

        return false;
    }
}
