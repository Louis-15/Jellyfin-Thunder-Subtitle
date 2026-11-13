using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>
/// 影视库的字幕检索方式。每个影视库各自选一种，互斥：
/// 番号库按番号查，普通影视库按片名查。
/// </summary>
public enum SubtitleSearchMode
{
    /// <summary>按番号检索，例如 <c>ABF-363</c>。</summary>
    Code,

    /// <summary>按片名（+ 年份 / 剧集标记）检索，例如 <c>Inception 2010</c>。</summary>
    Title
}

/// <summary>
/// 一次检索的输入：走哪种模式、查什么词、用什么做候选核对。
/// 番号模式和片名模式共用同一套「下载 → 语言过滤 → 排序 → AI 评审」管线，
/// 差别只在查询词和候选的可信度判断。
/// </summary>
public sealed record SearchTarget
{
    /// <summary>匹配 <c>S01E02</c> 这类季集标记。</summary>
    private static readonly Regex EpisodeTag = new(
        @"S(?<s>\d{1,2})[ ._-]?E(?<e>\d{1,3})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>去掉方括号、圆括号里的发布组 / 字幕组标记。</summary>
    private static readonly Regex Bracketed = new(@"\[[^\]]*\]|【[^】]*】|\([^)]*\)", RegexOptions.Compiled);

    private SearchTarget()
    {
    }

    /// <summary>Gets the mode this target was built for.</summary>
    public SubtitleSearchMode Mode { get; private init; }

    /// <summary>Gets the text sent to the Xunlei API.</summary>
    public string Query { get; private init; } = string.Empty;

    /// <summary>Gets a short human readable label used in logs and the AI prompt.</summary>
    public string Label { get; private init; } = string.Empty;

    /// <summary>Gets the release code, when this is a code search.</summary>
    public string? Code { get; private init; }

    /// <summary>Gets the title used to verify candidates, when this is a title search.</summary>
    public string? Title { get; private init; }

    /// <summary>Gets the production year, used as weaker evidence when the title cannot be matched.</summary>
    public int? Year { get; private init; }

    /// <summary>Gets the season/episode numbers, when the target is a TV episode.</summary>
    public int? Season { get; private init; }

    /// <summary>Gets the episode number, when the target is a TV episode.</summary>
    public int? Episode { get; private init; }

    /// <summary>Gets a value indicating whether the target is a TV episode.</summary>
    public bool IsEpisode => Season.HasValue && Episode.HasValue;

    /// <summary>
    /// Builds a code search. The code itself is the query; the API understands it directly.
    /// </summary>
    /// <param name="code">The normalized code, e.g. <c>ABF-363</c>.</param>
    /// <returns>The target.</returns>
    public static SearchTarget ForCode(string code)
    {
        return new SearchTarget
        {
            Mode = SubtitleSearchMode.Code,
            Query = code,
            Label = code,
            Code = code
        };
    }

    /// <summary>
    /// Builds a title search.
    /// </summary>
    /// <remarks>
    /// 查询词优先用「剧名 + S01E02」或「片名 + 年份」，实测这两种写法命中率最高：
    /// <c>Breaking Bad S01E01</c> 返回 54 条，<c>权力的游戏 S01E01</c> 也能返回英文命名的候选。
    /// 只给片名也查得到，但候选里混进同名影片的概率更高，所以年份能带上就带上。
    /// </remarks>
    /// <param name="title">The movie / series title.</param>
    /// <param name="year">Production year, when known.</param>
    /// <param name="season">Season number, when this is a TV episode.</param>
    /// <param name="episode">Episode number, when this is a TV episode.</param>
    /// <returns>The target, or <c>null</c> when the title is empty.</returns>
    public static SearchTarget? ForTitle(string? title, int? year = null, int? season = null, int? episode = null)
    {
        var clean = CleanQueryTitle(title);
        if (clean.Length == 0)
        {
            return null;
        }

        string query;
        string label;

        if (season.HasValue && episode.HasValue)
        {
            var tag = string.Format(CultureInfo.InvariantCulture, "S{0:00}E{1:00}", season.Value, episode.Value);
            query = $"{clean} {tag}";
            label = query;
        }
        else if (year is > 0)
        {
            query = $"{clean} {year.Value}";
            label = query;
        }
        else
        {
            query = clean;
            label = clean;
        }

        return new SearchTarget
        {
            Mode = SubtitleSearchMode.Title,
            Query = query,
            Label = label,
            Title = clean,
            Year = year is > 0 ? year : null,
            Season = season,
            Episode = episode
        };
    }

    /// <summary>
    /// Cleans a Jellyfin item name into something the subtitle index understands.
    /// 去掉方括号标记、把常见的分隔符换成空格，并裁掉末尾孤立出现的年份
    /// （年份会由 <see cref="ForTitle"/> 按需要重新拼到查询词里）。
    /// </summary>
    /// <param name="title">Raw title.</param>
    /// <returns>The cleaned title, possibly empty.</returns>
    public static string CleanQueryTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var text = Bracketed.Replace(title, " ").Replace('．', '.').Replace('　', ' ');
        text = text.Replace('.', ' ').Replace('_', ' ').Replace('·', ' ');

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            builder.Append(char.IsWhiteSpace(ch) || ch == '-' ? ' ' : ch);
        }

        var collapsed = Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
        return collapsed;
    }

    /// <summary>Extracts the <c>S01E02</c> marker from a file name, when it carries one.</summary>
    /// <param name="name">File name or item name.</param>
    /// <returns>The numbers, or <c>null</c>.</returns>
    public static (int Season, int Episode)? ParseEpisodeTag(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var match = EpisodeTag.Match(name);
        if (!match.Success)
        {
            return null;
        }

        return (int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["e"].Value, CultureInfo.InvariantCulture));
    }
}
