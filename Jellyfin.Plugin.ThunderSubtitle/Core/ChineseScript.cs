using System;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>
/// 一份字幕用的是什么字形。与 <see cref="SubtitleLanguage"/> 分开是刻意的：
///
/// <see cref="SubtitleLanguage"/> 决定语言档位（简 / 繁 / 双语各自排第几），
/// 已经贯通排序、短名单、质量降级整条链路，往里加一个 <c>Mixed</c> 值会让
/// <see cref="LanguagePolicy.TierOf"/> 拿到无法映射的输入，直接掉进「不可接受」。
///
/// 这个枚举只管「要不要显示紫色标签、能不能繁转简」，是独立的一维属性。
/// </summary>
public enum ChineseScript
{
    /// <summary>已经是简体，或没有可判定的汉字。</summary>
    Simplified = 0,

    /// <summary>繁体（含繁英双语）。</summary>
    Traditional,

    /// <summary>
    /// 繁简混用：同一份字幕里既有大量繁体字也有大量简体字。
    ///
    /// 这在外挂字幕里很常见 —— 字幕组做了一版繁体，后来手工改了一部分成简体，
    /// 剩下的漏了；或者用工具批量转简时只转了常用字，生僻字还是繁体。
    /// 这类字幕同样需要「繁转简」：转完才是干净的简体。
    /// </summary>
    Mixed
}

/// <summary>
/// 判定一份字幕用的是简体、繁体还是繁简混用。
///
/// 与 <see cref="SubtitleLanguageDetector"/> 的区别：
/// 那边只回答「简体还是繁体」（谁多算谁），用于决定语言档位，够用就行；
/// 这边要回答「要不要提示用户转换」，所以必须把「混用」单独识别出来 ——
/// 一份 60% 繁体 40% 简体的字幕，按多数派会被判成繁体，
/// 但它既不是干净的繁体也不是干净的简体，标签上写「繁体」是误导。
/// </summary>
public static class ChineseScriptDetector
{
    /// <summary>
    /// 少数派至少要占这么多比例，才算是「混用」。
    ///
    /// 10% 的依据：一份繁体字幕里偶尔有几个字被误转（或反之）很常见，
    /// 那属于「有瑕疵的繁体」，不该改变标签。超过一成就不一样了 ——
    /// 一眼看过去就能发现两种字形并存。
    /// </summary>
    internal const double MixedRatio = 0.10;

    /// <summary>
    /// 少数派至少要这么多「特征字」（繁体专用字或简体专用字的总和）才算混用。
    ///
    /// 占比门槛之外再加绝对数量门槛：一份只有 20 个特征字的短字幕（预告片、
    /// 片段），3 个字的差异就超过 10% 了，但那只是正常抖动，不是「混用」。
    /// </summary>
    internal const int MixedMinimumSignals = 5;

    /// <summary>判定一份文本的字形。</summary>
    /// <param name="text">字幕全文（已剥离 markup）。</param>
    /// <returns>简体 / 繁体 / 混用。</returns>
    public static ChineseScript Detect(string? text)
    {
        var (traditional, simplified) = SimplifiedConverter.CountSignals(text);

        if (traditional == 0)
        {
            return ChineseScript.Simplified;
        }

        if (simplified == 0)
        {
            return ChineseScript.Traditional;
        }

        var total = traditional + simplified;
        var minority = Math.Min(traditional, simplified);

        if (minority >= MixedMinimumSignals && minority >= total * MixedRatio)
        {
            return ChineseScript.Mixed;
        }

        return traditional > simplified
            ? ChineseScript.Traditional
            : ChineseScript.Simplified;
    }

    /// <summary>显示名，用于日志与标签。</summary>
    /// <param name="script">The script.</param>
    /// <returns>中文字形名称。</returns>
    public static string DisplayName(ChineseScript script) => script switch
    {
        ChineseScript.Traditional => "繁体",
        ChineseScript.Mixed => "简繁混用",
        _ => "简体"
    };

    /// <summary>
    /// 这份字幕要不要提供「繁转简」。混用也要提供 —— 用户要的正是一键统一成简体。
    /// </summary>
    /// <param name="script">The script.</param>
    /// <returns>繁体或混用返回 true。</returns>
    public static bool NeedsConversion(ChineseScript script) =>
        script is ChineseScript.Traditional or ChineseScript.Mixed;
}
