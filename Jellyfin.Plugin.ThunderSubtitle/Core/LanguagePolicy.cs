using System;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>
/// 语言档位：把「影片类型 + 字幕语言形式」映射成 0/1/2 三档。
///
/// 0 是首选，数字越大越靠后；返回 <see cref="Unacceptable"/> 表示这份字幕不该进入比较。
/// 排序时档位是第一排序键 —— 只要首选档存在，次选档的候选就不会被选中
/// （唯一的例外是日本AV 的质量降级，见 SubtitleFinder）。
/// </summary>
public static class LanguagePolicy
{
    /// <summary>表示「这种语言形式不接受」的档位。</summary>
    public const int Unacceptable = int.MaxValue;

    /// <summary>
    /// 计算某个影片类型下，某种字幕语言形式的档位。
    /// </summary>
    /// <param name="kind">影片类型。</param>
    /// <param name="language">字幕语言形式。</param>
    /// <returns>0/1/2，或不接受时的 <see cref="Unacceptable"/>。</returns>
    public static int TierOf(MediaKind kind, SubtitleLanguage language) => (kind, language) switch
    {
        // 非中文影视：中英双语 > 纯简体 > 繁体（含繁英双语）
        // 用户的原话：「优先选中英双语的，如果没有中英双语的，就选纯简体中文的」。
        (MediaKind.Foreign, SubtitleLanguage.SimplifiedChineseEnglish) => 0,
        (MediaKind.Foreign, SubtitleLanguage.SimplifiedChinese) => 1,
        (MediaKind.Foreign, SubtitleLanguage.TraditionalChinese) => 2,
        (MediaKind.Foreign, SubtitleLanguage.TraditionalChineseEnglish) => 2,

        // 中文影视：纯简体 > 纯繁体 > 双语
        // 双语放兜底而不是排除：英文对中文影视是多余的，但「有字幕」比「没字幕」好。
        (MediaKind.Chinese, SubtitleLanguage.SimplifiedChinese) => 0,
        (MediaKind.Chinese, SubtitleLanguage.TraditionalChinese) => 1,
        (MediaKind.Chinese, SubtitleLanguage.SimplifiedChineseEnglish) => 2,
        (MediaKind.Chinese, SubtitleLanguage.TraditionalChineseEnglish) => 2,

        // 日本AV：纯简体 > 纯繁体 > 双语
        // AV 里双语字幕少见，双行还会遮挡画面，所以排最后。
        (MediaKind.JapaneseAv, SubtitleLanguage.SimplifiedChinese) => 0,
        (MediaKind.JapaneseAv, SubtitleLanguage.TraditionalChinese) => 1,
        (MediaKind.JapaneseAv, SubtitleLanguage.SimplifiedChineseEnglish) => 2,
        (MediaKind.JapaneseAv, SubtitleLanguage.TraditionalChineseEnglish) => 2,

        _ => Unacceptable
    };

    /// <summary>判断某种语言形式是否可以进入比较。</summary>
    /// <param name="kind">影片类型。</param>
    /// <param name="language">字幕语言形式。</param>
    /// <returns><c>true</c> 表示可以比较。</returns>
    public static bool IsAcceptable(MediaKind kind, SubtitleLanguage language)
    {
        return TierOf(kind, language) != Unacceptable;
    }

    /// <summary>日志里显示用的档位名，例如「首选：简英双语」。</summary>
    /// <param name="kind">影片类型。</param>
    /// <param name="tier">档位。</param>
    /// <returns>中文名。</returns>
    public static string TierName(MediaKind kind, int tier) => (kind, tier) switch
    {
        (MediaKind.Foreign, 0) => "简英双语",
        (MediaKind.Foreign, 1) => "纯简体",
        (MediaKind.Foreign, 2) => "繁体",
        (_, 0) => "纯简体",
        (_, 1) => "纯繁体",
        (_, 2) => "双语",
        _ => "不接受"
    };
}
