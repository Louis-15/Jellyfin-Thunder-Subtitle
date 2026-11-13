using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>Script/language of a subtitle, as far as it can be told from the text.</summary>
public enum SubtitleLanguage
{
    /// <summary>Too little text to decide.</summary>
    Unknown = 0,

    /// <summary>Simplified Chinese.</summary>
    SimplifiedChinese,

    /// <summary>Traditional Chinese.</summary>
    TraditionalChinese,

    /// <summary>Simplified Chinese + English, one line each (bilingual subtitle).</summary>
    SimplifiedChineseEnglish,

    /// <summary>Traditional Chinese + English, one line each (bilingual subtitle).</summary>
    TraditionalChineseEnglish,

    /// <summary>Japanese, including bilingual Japanese/Chinese files.</summary>
    Japanese,

    /// <summary>Korean.</summary>
    Korean,

    /// <summary>Latin script.</summary>
    English,

    /// <summary>Anything else.</summary>
    Other
}

/// <summary>
/// Works out which language a subtitle is in. The Xunlei API happily returns English or
/// Japanese subtitles for the same code, and English carries more "letters" per kilobyte
/// than Chinese, so without this filter a character-count ranking actively prefers the
/// wrong language.
/// </summary>
public static class SubtitleLanguageDetector
{
    // Characters that only exist in one of the two Chinese orthographies. Deliberately
    // excludes characters that both use (里, 面, 后 in the sense of 皇后...) so the counts
    // stay meaningful.
    private const string SimplifiedOnly =
        "这说国时会对们开关发现实际语话电视听觉让从过还学习见问题谢谁么无数样种头长万与业东两严举义乐乡书买乱争亏云亚产亲亿仅仓仪价众优伟传伤体侠债倾偿储儿兰兴养兽军农冲决净减凤划刘则刚创别劝办务动劳势医华协单卖卫历压厌县参双变叶号叹吗员呜响哑唤喷嘱团园围图圆圣场坏块坚坛垒垦";

    private const string TraditionalOnly =
        "這說國時會對們開關發現實際語話電視聽覺讓從過還學習見問題謝誰麼無數樣種頭長萬與業東兩嚴舉義樂鄉書買亂爭虧雲亞產親億僅倉儀價眾優偉傳傷體俠債傾償儲兒蘭興養獸軍農衝決淨減鳳劃劉則剛創別勸辦務動勞勢醫華協單賣衛歷壓厭縣參雙變葉號嘆嗎員嗚響啞喚噴囑團園圍圖圓聖場壞塊堅壇壘墾";

    private static readonly HashSet<char> SimplifiedSet = SimplifiedOnly.ToHashSet();

    private static readonly HashSet<char> TraditionalSet = TraditionalOnly.ToHashSet();

    /// <summary>
    /// 判定一份字幕算不算「双语」的两个门槛。
    ///
    /// 15 条下限：太短的样本（预告片、片段）不适合按比例判定。
    /// 25% 比例：有些字幕组只在对话密集处做双语，动作场景只有中文，
    /// 要求 50% 会把它们漏掉；25% 又足以排除「偶尔夹一个人名」的普通中文字幕。
    /// </summary>
    private const int BilingualMinimumCues = 15;

    private const double BilingualMinimumRatio = 0.25;

    /// <summary>
    /// 单个 cue 判定为「双语」的条件：至少 2 个汉字、3 个英文单词、12 个英文字母。
    ///
    /// 为什么要 3 个单词而不是 1 个：中文字幕里出现「iPhone」「WiFi」「OK」很常见，
    /// 单个人名或品牌不能证明这是双语字幕；3 个单词加 12 个字母，
    /// 基本已经是一句完整的英文了。
    /// </summary>
    private const int BilingualMinimumWords = 3;

    private const int BilingualMinimumLetters = 12;

    /// <summary>True for the four languages that carry Chinese dialogue.</summary>
    /// <param name="language">The detected language.</param>
    /// <returns><c>true</c> for both Chinese forms, with or without English lines.</returns>
    public static bool IsChinese(SubtitleLanguage language)
    {
        return language is SubtitleLanguage.SimplifiedChinese
            or SubtitleLanguage.TraditionalChinese
            or SubtitleLanguage.SimplifiedChineseEnglish
            or SubtitleLanguage.TraditionalChineseEnglish;
    }

    /// <summary>True when the subtitle carries a Chinese line and an English line.</summary>
    /// <param name="language">The detected language.</param>
    /// <returns><c>true</c> for the two bilingual forms.</returns>
    public static bool IsBilingual(SubtitleLanguage language)
    {
        return language is SubtitleLanguage.SimplifiedChineseEnglish or SubtitleLanguage.TraditionalChineseEnglish;
    }

    /// <summary>True when the Chinese part is written in Simplified characters.</summary>
    /// <param name="language">The detected language.</param>
    /// <returns><c>true</c> for SimplifiedChinese and SimplifiedChineseEnglish.</returns>
    public static bool IsSimplified(SubtitleLanguage language)
    {
        return language is SubtitleLanguage.SimplifiedChinese or SubtitleLanguage.SimplifiedChineseEnglish;
    }

    /// <summary>True when the Chinese part is written in Traditional characters.</summary>
    /// <param name="language">The detected language.</param>
    /// <returns><c>true</c> for TraditionalChinese and TraditionalChineseEnglish.</returns>
    public static bool IsTraditional(SubtitleLanguage language)
    {
        return language is SubtitleLanguage.TraditionalChinese or SubtitleLanguage.TraditionalChineseEnglish;
    }

    /// <summary>Short Chinese name used in logs and prompts.</summary>
    /// <param name="language">The detected language.</param>
    /// <returns>The display name.</returns>
    public static string DisplayName(SubtitleLanguage language) => language switch
    {
        SubtitleLanguage.SimplifiedChinese => "简体",
        SubtitleLanguage.TraditionalChinese => "繁体",
        SubtitleLanguage.SimplifiedChineseEnglish => "简英双语",
        SubtitleLanguage.TraditionalChineseEnglish => "繁英双语",
        SubtitleLanguage.Japanese => "日文",
        SubtitleLanguage.Korean => "韩文",
        SubtitleLanguage.English => "英文",
        SubtitleLanguage.Unknown => "未知",
        _ => "其他"
    };

    /// <summary>
    /// Detects the language of a subtitle, using the parsed cues when available.
    ///
    /// 用户要求「优先中英双语」，但双语在全文统计里会被算成纯中文（汉字占比高），
    /// 所以必须逐条 cue 判断：只有一条字幕里同时出现「中文行」和「英文行」，
    /// 才算双语。按全文统计会把「这个叫 John 的人」误判成双语。
    /// </summary>
    /// <param name="cues">Parsed cues; may be empty when the format could not be parsed.</param>
    /// <param name="fallbackText">Markup-stripped full text, used for the plain-text fallback.</param>
    /// <returns>The detected language.</returns>
    public static SubtitleLanguage Detect(IReadOnlyList<SubtitleCue>? cues, string? fallbackText)
    {
        if (cues is { Count: > 0 })
        {
            int bilingual = 0, chinese = 0, english = 0;

            foreach (var cue in cues)
            {
                if (IsBilingualCue(cue.Text))
                {
                    bilingual++;
                }
                else if (ContainsHan(cue.Text))
                {
                    chinese++;
                }
                else if (CountLatinWords(cue.Text) >= 2)
                {
                    english++;
                }
            }

            var considered = bilingual + chinese + english;
            if (considered > 0
                && bilingual >= BilingualMinimumCues
                && bilingual >= considered * BilingualMinimumRatio)
            {
                // 简繁只看中文部分：英文里的字母不参与繁简特征字计数，
                // 所以直接用全文本调用 DetectChineseForm 是安全的。
                return DetectChineseForm(fallbackText ?? string.Empty) == SubtitleLanguage.TraditionalChinese
                    ? SubtitleLanguage.TraditionalChineseEnglish
                    : SubtitleLanguage.SimplifiedChineseEnglish;
            }
        }

        return Detect(fallbackText);
    }

    /// <summary>Detects the language of a subtitle.</summary>
    /// <param name="text">The subtitle text, markup allowed.</param>
    /// <returns>The detected language.</returns>
    public static SubtitleLanguage Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return SubtitleLanguage.Unknown;
        }

        int han = 0, kana = 0, hangul = 0, latin = 0, other = 0;

        foreach (var ch in text)
        {
            if (IsKana(ch))
            {
                kana++;
            }
            else if (IsHan(ch))
            {
                han++;
            }
            else if (ch >= '\uAC00' && ch <= '\uD7AF')
            {
                hangul++;
            }
            else if (ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            {
                latin++;
            }
            else if (char.IsLetter(ch))
            {
                other++;
            }
        }

        var total = han + kana + hangul + latin + other;
        if (total < 20)
        {
            return SubtitleLanguage.Unknown;
        }

        if (hangul >= 0.30 * total)
        {
            return SubtitleLanguage.Korean;
        }

        // Chinese subtitles essentially never contain kana; a Japanese file is full of it.
        // Bilingual JP/CN files land here too, which is intended: they are Japanese files.
        if (kana >= 10 && kana >= 0.10 * (kana + han))
        {
            return SubtitleLanguage.Japanese;
        }

        if (han >= 0.20 * total)
        {
            return DetectChineseForm(text);
        }

        if (latin >= 0.50 * total)
        {
            return SubtitleLanguage.English;
        }

        return SubtitleLanguage.Other;
    }

    /// <summary>
    /// 判断单个 cue 是否「中英双语」：至少 2 个汉字，且英文部分够长够多。
    ///
    /// SRT 的两行字幕在解析阶段被合并成一行（「你好 Hello」），
    /// 所以这里按整条 cue 统计，而不是按行拆分 —— 两种排版都能覆盖。
    /// </summary>
    private static bool IsBilingualCue(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        int han = 0, letters = 0, words = 0;
        var inWord = false;

        foreach (var ch in text)
        {
            if (IsHan(ch))
            {
                han++;
                inWord = false;
            }
            else if (ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            {
                letters++;
                if (!inWord)
                {
                    words++;
                    inWord = true;
                }
            }
            else if (ch != '\'' && ch != '’')
            {
                // 撇号不算断词：「I'm」「don't」应当算一个单词。
                inWord = false;
            }
        }

        return han >= 2 && words >= BilingualMinimumWords && letters >= BilingualMinimumLetters;
    }

    /// <summary>True when the line contains at least one Han character.</summary>
    private static bool ContainsHan(string text)
    {
        foreach (var ch in text)
        {
            if (IsHan(ch))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Counts Latin-script words, treating an apostrophe as part of the word.</summary>
    private static int CountLatinWords(string text)
    {
        var words = 0;
        var inWord = false;

        foreach (var ch in text)
        {
            if (ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            {
                if (!inWord)
                {
                    words++;
                    inWord = true;
                }
            }
            else if (ch != '\'' && ch != '’')
            {
                inWord = false;
            }
        }

        return words;
    }

    private static SubtitleLanguage DetectChineseForm(string text)
    {
        int simplified = 0, traditional = 0;

        foreach (var ch in text)
        {
            if (SimplifiedSet.Contains(ch))
            {
                simplified++;
            }
            else if (TraditionalSet.Contains(ch))
            {
                traditional++;
            }
        }

        // A Traditional file uses 這/們/說 where a Simplified one uses 这/们/说, so any
        // lead is decisive. With no distinguishing character at all the text is shared by
        // both orthographies, and Simplified is the far more common source.
        return traditional > simplified
            ? SubtitleLanguage.TraditionalChinese
            : SubtitleLanguage.SimplifiedChinese;
    }

    private static bool IsKana(char ch)
    {
        // Half-width katakana is excluded on purpose: it shows up when Shift-JIS decodes
        // GB18030 bytes, i.e. as mojibake in an otherwise Chinese file.
        return ch is >= '\u3040' and <= '\u309F' or >= '\u30A0' and <= '\u30FF' or '\u30FC';
    }

    private static bool IsHan(char ch)
    {
        return ch is >= '\u3400' and <= '\u4DBF'
            or >= '\u4E00' and <= '\u9FFF'
            or >= '\uF900' and <= '\uFAFF';
    }
}
