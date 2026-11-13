using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>
/// 影片类型。决定字幕的语言档位、排序侧重和 AI 评审的提示词。
///
/// 这个类型不是配置项，而是运行时推导出来的：
/// 用户在配置页只需要为每个影视库选「番号搜索 / 片名搜索」，
/// 番号库天然就是日本 AV，其余靠 Jellyfin 刮削的语言与地区判断中英文。
/// </summary>
public enum MediaKind
{
    /// <summary>非中文影视：原声为英/日/韩等，需要中文翻译字幕。片名搜索库的默认类型。</summary>
    Foreign,

    /// <summary>中文影视：原声为普通话/粤语等，字幕是听录或台本，重点在错别字。</summary>
    Chinese,

    /// <summary>日本 AV：按番号命名的影片，字幕多为日译中的 AI 机翻。</summary>
    JapaneseAv
}

/// <summary>
/// 把「影视库的检索方式 + 条目的语言/地区元数据」翻译成 <see cref="MediaKind"/>。
///
/// 拆成独立的静态方法是为了可测试：单元测试里没有 ILibraryManager，
/// 也不需要构造 BaseItem，只验证判定表本身。
/// </summary>
public static class MediaKindResolver
{
    /// <summary>
    /// 中文语言码。Jellyfin 的 OriginalLanguage 可能是两字母、三字母或带地区的写法，
    /// 不同刮削器写法不一致（zh / zho / chi / zh-Hans / zh_TW），
    /// 所以比较前统一转小写并把下划线换成连字符。
    /// </summary>
    private static readonly HashSet<string> ChineseLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "zh",
        "zho",
        "chi",
        "cmn",
        "yue",
        "zh-cn",
        "zh-tw",
        "zh-hk",
        "zh-hans",
        "zh-hant",
        "chinese"
    };

    /// <summary>
    /// 中文地区码。只在语言缺失时作为兜底：
    /// 新加坡、美国等地同样有大量英语片，把它们当主判据会误伤，
    /// 所以只保留华语内容占绝对多数的四个地区。
    /// </summary>
    private static readonly HashSet<string> ChineseLocations = new(StringComparer.OrdinalIgnoreCase)
    {
        "CN",
        "TW",
        "HK",
        "MO"
    };

    /// <summary>
    /// 推导影片类型。
    /// </summary>
    /// <param name="libraryMode">
    /// 条目所属影视库**显式配置**的检索方式。
    /// <c>null</c> 表示「该条目不属于任何已配置的库，或那个库从未保存过检索方式」——
    /// 这种情况绝不能当成 AV：用户定的规则是「只有番号搜索的影视库才是日本 AV」，
    /// 「不知道」不等于「番号库」。
    /// </param>
    /// <param name="originalLanguage">Jellyfin 刮削出的原始语言，如 <c>zh</c> / <c>en</c> / <c>ja</c>。</param>
    /// <param name="productionLocations">制作国家/地区码，语言缺失时用于兜底。</param>
    /// <returns>影片类型。</returns>
    public static MediaKind Resolve(
        SubtitleSearchMode? libraryMode,
        string? originalLanguage,
        IEnumerable<string>? productionLocations)
    {
        // 番号库 = AV 库。这条优先级最高：用户可能把 AV 归到各种名字的库里，
        // 但「按番号检索」是唯一稳定、且不依赖库名的信号。
        // 即使刮削器给某部片子标了 ja，也不影响这条判断。
        //
        // 反过来必须严格：只有**显式配置**成番号检索的库才算 AV。
        // null（找不到库归属 / 库没保存过设置）不等于番号库，继续按语言和地区判断。
        if (libraryMode == SubtitleSearchMode.Code)
        {
            return MediaKind.JapaneseAv;
        }

        if (IsChineseLanguage(originalLanguage))
        {
            return MediaKind.Chinese;
        }

        // 语言明确是别的（en / ja / ko / fr ...）：交给非中文影视的处理流程。
        if (!string.IsNullOrWhiteSpace(originalLanguage))
        {
            return MediaKind.Foreign;
        }

        // 语言缺失（刮削不完整）时才看地区。反过来不成立：
        // 一部美国出品的华语片会被 OriginalLanguage=zh 先接住。
        if (HasChineseLocation(productionLocations))
        {
            return MediaKind.Chinese;
        }

        // 默认非中文影视：绝大多数片名库是非中文内容，
        // 把未知当成「需要中文翻译字幕」比当成「中文影视」更安全。
        return MediaKind.Foreign;
    }

    /// <summary>判断语言码是否属于中文（含粤语）。</summary>
    /// <param name="language">原始语言码。</param>
    /// <returns><c>true</c> 表示中文。</returns>
    public static bool IsChineseLanguage(string? language)
    {
        var normalized = Normalize(language);
        return normalized.Length > 0 && ChineseLanguages.Contains(normalized);
    }

    /// <summary>判断地区码列表里是否含中文地区。</summary>
    /// <param name="locations">制作国家/地区码。</param>
    /// <returns><c>true</c> 表示至少有一个中文地区。</returns>
    public static bool HasChineseLocation(IEnumerable<string>? locations)
    {
        if (locations is null)
        {
            return false;
        }

        foreach (var location in locations)
        {
            var normalized = Normalize(location);
            if (normalized.Length > 0 && ChineseLocations.Contains(normalized))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>日志里显示用的中文名。</summary>
    /// <param name="kind">影片类型。</param>
    /// <returns>中文名。</returns>
    public static string DisplayName(MediaKind kind) => kind switch
    {
        MediaKind.Chinese => "中文影视",
        MediaKind.JapaneseAv => "日本AV",
        _ => "非中文影视"
    };

    /// <summary>
    /// 归一化语言/地区码：去空白、转小写、下划线换连字符。
    /// 刮削器写 <c>zh_TW</c> 和 <c>zh-tw</c> 的都有，不归一化会漏判。
    /// </summary>
    private static string Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().Replace('_', '-').ToLowerInvariant();
    }
}
