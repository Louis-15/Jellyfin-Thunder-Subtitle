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

    /// <summary>True for the two languages that are allowed into the comparison.</summary>
    /// <param name="language">The detected language.</param>
    /// <returns><c>true</c> for Simplified and Traditional Chinese.</returns>
    public static bool IsChinese(SubtitleLanguage language)
    {
        return language is SubtitleLanguage.SimplifiedChinese or SubtitleLanguage.TraditionalChinese;
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
