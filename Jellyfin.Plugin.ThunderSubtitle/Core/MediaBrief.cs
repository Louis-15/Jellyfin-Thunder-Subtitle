using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>
/// 发给 AI 的影片元数据卡。
///
/// 用户要求「考虑此影片的题材、年代，选用最合适的用词」——
/// 模型必须先知道这是 1970 年代的黑帮片，才能判断「绝绝子」出不出戏。
/// 这段文本会拼在 user prompt 的最前面，约占 200 字。
/// </summary>
/// <param name="Title">显示名（通常是中文名）。</param>
/// <param name="OriginalTitle">原名，与显示名相同时为 null。</param>
/// <param name="Year">制作年份。</param>
/// <param name="ContentType">电影 / 剧集 / 动画 / 影视。</param>
/// <param name="Genres">题材标签，最多取 5 个。</param>
/// <param name="ProductionLocations">制作国家/地区码。</param>
/// <param name="Overview">简介，已截断。</param>
/// <param name="RuntimeMinutes">正片时长（分钟），用于时间轴覆盖检测。</param>
public sealed record MediaBrief(
    string Title,
    string? OriginalTitle,
    int? Year,
    string ContentType,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> ProductionLocations,
    string? Overview,
    int? RuntimeMinutes)
{
    /// <summary>简介最多保留的字数。再长也不会提升判断质量，只会烧 token。</summary>
    public const int MaxOverviewChars = 120;

    /// <summary>没有元数据时的空卡片，prompt 里会自动省略。</summary>
    public static MediaBrief Empty { get; } = new(
        string.Empty,
        null,
        null,
        "影视",
        Array.Empty<string>(),
        Array.Empty<string>(),
        null,
        null);

    /// <summary>
    /// 常见制作地区的中文名。模型看得懂 <c>US</c>，但「美国」能让它在判断用词时更准确。
    /// 未收录的地区直接显示原码，不影响判断。
    /// </summary>
    private static readonly Dictionary<string, string> CountryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CN"] = "中国",
        ["TW"] = "台湾",
        ["HK"] = "香港",
        ["MO"] = "澳门",
        ["US"] = "美国",
        ["GB"] = "英国",
        ["JP"] = "日本",
        ["KR"] = "韩国",
        ["FR"] = "法国",
        ["DE"] = "德国",
        ["IT"] = "意大利",
        ["ES"] = "西班牙",
        ["IN"] = "印度",
        ["TH"] = "泰国",
        ["RU"] = "俄罗斯",
        ["CA"] = "加拿大",
        ["AU"] = "澳大利亚",
        ["NZ"] = "新西兰",
        ["SG"] = "新加坡",
        ["MY"] = "马来西亚"
    };

    /// <summary>
    /// 拼成 prompt 里的三行：
    /// <code>
    /// 影片：《盗梦空间》 Inception (2010)
    /// 类型：电影 · 科幻 / 悬疑 / 动作 · 美国
    /// 简介：一个能潜入他人梦境的窃贼……
    /// </code>
    /// </summary>
    /// <returns>The prompt text, empty when the brief carries nothing.</returns>
    public string ToPromptText()
    {
        // 没有片名就没有可用信息：只输出「类型：影视」对模型毫无帮助，还白占 token。
        if (string.IsNullOrWhiteSpace(Title))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();

        builder.Append("影片：《").Append(Title).Append('》');
        if (!string.IsNullOrWhiteSpace(OriginalTitle))
        {
            builder.Append(' ').Append(OriginalTitle);
        }

        if (Year is > 0)
        {
            builder.Append(" (").Append(Year.Value).Append(')');
        }

        builder.Append('\n');

        var facts = new List<string> { ContentType };
        if (Genres.Count > 0)
        {
            facts.Add(string.Join(" / ", Genres));
        }

        var countries = ProductionLocations
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(CountryName)
            .ToArray();

        if (countries.Length > 0)
        {
            facts.Add(string.Join(" / ", countries));
        }

        builder.Append("类型：").Append(string.Join(" · ", facts)).Append('\n');

        if (!string.IsNullOrWhiteSpace(Overview))
        {
            builder.Append("简介：").Append(Overview).Append('\n');
        }

        return builder.ToString();
    }

    private static string CountryName(string code)
    {
        return CountryNames.TryGetValue(code.Trim(), out var name) ? name : code.Trim();
    }
}
