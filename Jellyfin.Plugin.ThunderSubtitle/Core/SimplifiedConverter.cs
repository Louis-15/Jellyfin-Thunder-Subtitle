using System;
using System.Collections.Generic;
using System.Text;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>
/// 繁转简转换器。映射数据来自 OpenCC（见 <see cref="SimplifiedTable"/>）。
///
/// 为什么自己实现而不引 NuGet 包：插件的 <c>CopyLocalLockFileAssemblies=false</c>，
/// 打包脚本只复制主 dll，任何第三方程序集都不会跟过去 —— 引了也是运行时找不到。
/// 而繁转简本身只是查表，逻辑不到百行，内嵌 30 KB 数据的代价远比改打包流程小。
///
/// 判定顺序与 OpenCC 一致：**先查词组（最长优先），查不到再逐字查单字表**。
/// 这个顺序不是可选优化，而是正确性的前提：
/// 「乾」在单字表里映射到「干」，但「乾坤」「乾隆」必须原样保留 ——
/// 词组表里那几条「繁体 → 它自己」的保护条目就是干这个的。
/// </summary>
internal static class SimplifiedConverter
{
    /// <summary>繁 → 简 单字表。</summary>
    private static readonly Dictionary<char, char> SingleMap;

    /// <summary>繁体词组 → 简体词组，含「原样保留」的保护条目。</summary>
    private static readonly Dictionary<string, string> PhraseMap;

    /// <summary>词组首字集合：不在其中的字直接查单字表，省掉大部分无用匹配。</summary>
    private static readonly HashSet<char> PhraseFirstChars = [];

    /// <summary>词组表里最长的条目长度，决定每次向前尝试几个字符。</summary>
    private static readonly int MaxPhraseLength;

    /// <summary>
    /// 繁体特征字：简体文本里不会出现的字（单字表的键，3221 个）。
    ///
    /// 注意「乾」这类歧义字也在里面 —— 它在标准简体里确实很少见（「乾隆」除外）。
    /// 用于混用判定时这点偏差可以接受：一两个歧义字不足以翻转整份字幕的判定。
    /// </summary>
    private static readonly HashSet<char> TraditionalSignal;

    /// <summary>
    /// 简体特征字：只可能出现在简体文本里的字（单字表的值，去掉同时也是繁体键的那些）。
    /// 「这」「们」「说」在这里；「的」「人」「是」不在任何一边，算中性。
    /// </summary>
    private static readonly HashSet<char> SimplifiedSignal;

    static SimplifiedConverter()
    {
        SingleMap = new Dictionary<char, char>(SimplifiedTable.Characters.Length / 2);
        for (var i = 0; i + 1 < SimplifiedTable.Characters.Length; i += 2)
        {
            SingleMap[SimplifiedTable.Characters[i]] = SimplifiedTable.Characters[i + 1];
        }

        PhraseMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var longest = 0;
        foreach (var line in SimplifiedTable.Phrases.Split('\n'))
        {
            var tab = line.IndexOf('\t');
            if (tab <= 0)
            {
                continue;
            }

            var traditional = line[..tab];
            var simplified = line[(tab + 1)..];
            PhraseMap[traditional] = simplified;
            PhraseFirstChars.Add(traditional[0]);
            if (traditional.Length > longest)
            {
                longest = traditional.Length;
            }
        }

        MaxPhraseLength = longest;

        TraditionalSignal = new HashSet<char>(SingleMap.Keys);

        // 简繁同形的字（如「体」既是「體」的简体，也可能在繁体文本里当别的字用）
        // 不算简体特征：把它们算进去会让繁体文本被误判成混用。
        var simplifiedSignals = new HashSet<char>();
        foreach (var pair in SingleMap)
        {
            if (!SingleMap.ContainsKey(pair.Value))
            {
                simplifiedSignals.Add(pair.Value);
            }
        }

        SimplifiedSignal = simplifiedSignals;
    }

    /// <summary>单字映射表里有多少条。</summary>
    internal static int SingleCount => SingleMap.Count;

    /// <summary>词组映射表里有多少条。</summary>
    internal static int PhraseCount => PhraseMap.Count;

    /// <summary>
    /// 把繁体中文转成简体。非中文、已经是简体的部分原样保留。
    ///
    /// 一次扫描完成：遇到可能是词组开头的位置才向前尝试匹配，
    /// 其余位置直接查单字表。10 万字的字幕约 15 ms。
    /// </summary>
    /// <param name="text">原始文本。</param>
    /// <returns>简体文本；<paramref name="text"/> 为空时返回空字符串。</returns>
    internal static string ToSimplified(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var index = 0;

        while (index < text.Length)
        {
            var current = text[index];

            if (PhraseFirstChars.Contains(current))
            {
                // 最长优先：长的词组更具体（「乾乾淨淨」优先于「乾淨」）。
                var longest = Math.Min(MaxPhraseLength, text.Length - index);
                var matched = false;

                for (var length = longest; length >= 2; length--)
                {
                    if (PhraseMap.TryGetValue(text.Substring(index, length), out var replacement))
                    {
                        builder.Append(replacement);
                        index += length;
                        matched = true;
                        break;
                    }
                }

                if (matched)
                {
                    continue;
                }
            }

            builder.Append(SingleMap.TryGetValue(current, out var simple) ? simple : current);
            index++;
        }

        return builder.ToString();
    }

    /// <summary>
    /// 文本里有没有需要转换的繁体字。
    /// 只看单字表就够：词组表的每条都至少含一个繁体字，否则它也不会被收进去。
    /// </summary>
    /// <param name="text">待检查的文本。</param>
    /// <returns>含繁体字返回 true。</returns>
    internal static bool ContainsTraditional(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var ch in text)
        {
            if (TraditionalSignal.Contains(ch))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>数一份文本里的繁体特征字与简体特征字各有多少。</summary>
    /// <param name="text">待统计的文本。</param>
    /// <returns>两类特征字的计数。</returns>
    internal static (int Traditional, int Simplified) CountSignals(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (0, 0);
        }

        int traditional = 0, simplified = 0;

        foreach (var ch in text)
        {
            if (TraditionalSignal.Contains(ch))
            {
                traditional++;
            }
            else if (SimplifiedSignal.Contains(ch))
            {
                simplified++;
            }
        }

        return (traditional, simplified);
    }
}
