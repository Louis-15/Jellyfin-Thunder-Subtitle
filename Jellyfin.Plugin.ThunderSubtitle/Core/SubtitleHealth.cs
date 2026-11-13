using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>
/// 一份候选字幕的「健康度」：把能自动数出来的毛病列成清单，用来判断它值不值得送审。
///
/// 为什么需要这个东西：送 AI 之前必须先缩小范围，原先用「总字数」从多到少取前几名，
/// 但字数只反映体量，不反映质量 ——
/// 双语字幕夹着整行英文，字数天然翻倍；实测《加勒比海盗》里一份 4.4 万字的候选，
/// 实际台词只有一半（另一半是英文对照），AI 评它「大段缺失」只给 52 分。
/// 反过来，用户手工挑中的国配台本只有 1.2 万字（纯中文、没有英文对照），
/// 按字数排它连送审名单都进不去。
///
/// 所以换成数毛病，四条红线各自对应一种具体的坏：
/// 1. 含中文条数占比太低 —— 这其实是外挂英文/日文字幕，中文只是零星嵌了几条；
/// 2. 未翻译的方括号标注太多 —— 「[Jack sighs]」这种音效/动作提示没做本地化；
/// 3. 相邻台词之间的最大空白太大 —— 超过 5 分钟的空档意味着这段剧情整段没翻；
/// 4. 平均每条台词时长太长 —— 超过 10 秒说明把好几句挤进一条，断句粗到没法看。
///
/// 本类只能回答「这份是不是烂」，回答不了「这份是不是最好」。后者必须读台词才知道：
/// 实测有一份四项指标全绿，但时间轴整体错位到了别的情节上（AI 判 48 分），
/// 那种错误任何本地统计都测不出来。所以健康度只用于淘汰，不用于排名 ——
/// 排名交给 AI，反正 token 不是瓶颈。
/// </summary>
public sealed record SubtitleHealth
{
    /// <summary>
    /// 含中文条数占比的下限。低于它就不是「中文字幕」，而是零星嵌了几条中文的外挂字幕。
    /// 定 0.90 而不是 0.99：正常的中文字幕里会有少量纯英文行（人名标牌、歌曲、
    /// 或者原样保留的英文台词），不该因此被判死。
    /// </summary>
    public const double MinimumChineseRatio = 0.90;

    /// <summary>
    /// 未翻译方括号标注的容许条数。一部两小时的片子有几条漏网很正常，
    /// 超过 20 条说明整份字幕的音效/动作提示都没本地化。
    /// </summary>
    public const int MaximumUntranslatedCues = 20;

    /// <summary>
    /// 相邻台词之间的最大容许空白（秒）。5 分钟没有任何台词，
    /// 正常的影片不会静默这么久 —— 那是有一段剧情整段没翻。
    /// </summary>
    public const double MaximumGapSeconds = 300;

    /// <summary>
    /// 平均每条台词的容许时长（秒）。超过它说明是多句台词挤进了同一条 cue，
    /// 播放时一句话挂十几秒，断句粗到没法跟读。
    /// </summary>
    public const double MaximumAverageCueSeconds = 10;

    /// <summary>Initializes a new instance of the <see cref="SubtitleHealth"/> class.</summary>
    /// <param name="cueCount">台词条数。</param>
    /// <param name="chineseCues">含中文的台词条数。</param>
    /// <param name="untranslatedCues">含未翻译方括号标注的条数。</param>
    /// <param name="halfWidthPunctuation">中文后用半角标点的条数。</param>
    /// <param name="maxGapSeconds">相邻台词之间的最大空白（秒）。</param>
    /// <param name="averageCueSeconds">平均每条台词时长（秒）。</param>
    /// <param name="defects">触发的红线数量，0 表示没有明显毛病。</param>
    /// <param name="defectReason">触发的原因，逗号分隔，写给日志看。</param>
    public SubtitleHealth(
        int cueCount,
        int chineseCues,
        int untranslatedCues,
        int halfWidthPunctuation,
        double maxGapSeconds,
        double averageCueSeconds,
        int defects,
        string defectReason)
    {
        CueCount = cueCount;
        ChineseCues = chineseCues;
        UntranslatedCues = untranslatedCues;
        HalfWidthPunctuation = halfWidthPunctuation;
        MaxGapSeconds = maxGapSeconds;
        AverageCueSeconds = averageCueSeconds;
        Defects = defects;
        DefectReason = defectReason;
    }

    /// <summary>Gets the number of dialogue lines that were scanned.</summary>
    public int CueCount { get; }

    /// <summary>Gets how many cues contain at least one Chinese character.</summary>
    public int ChineseCues { get; }

    /// <summary>Gets how many cues carry an untranslated bracketed annotation such as <c>[Jack sighs]</c>.</summary>
    public int UntranslatedCues { get; }

    /// <summary>
    /// Gets how many cues put a half-width <c>, . ! ?</c> right after a Chinese character.
    /// 只统计、不参与红线：国配台本这类专业译制稿反而半角标点偏多（原文照抄台本的排版），
    /// 按标点淘汰会把最好的那份筛掉。这个数字只写进日志，供排查时参考。
    /// </summary>
    public int HalfWidthPunctuation { get; }

    /// <summary>Gets the largest silence between two consecutive cues, in seconds.</summary>
    public double MaxGapSeconds { get; }

    /// <summary>Gets the average cue duration in seconds.</summary>
    public double AverageCueSeconds { get; }

    /// <summary>Gets how many red lines this candidate crossed.</summary>
    public int Defects { get; }

    /// <summary>Gets the human readable reason list, empty when nothing was crossed.</summary>
    public string DefectReason { get; }

    /// <summary>Gets the share of cues that contain Chinese.</summary>
    public double ChineseRatio => CueCount == 0 ? 0 : (double)ChineseCues / CueCount;

    /// <summary>Gets a value indicating whether this candidate may enter the comparison.</summary>
    public bool IsUsable => Defects == 0;

    /// <summary>
    /// 扫一份已解析的字幕，数出它自身有多少毛病。
    /// 一次遍历完成全部统计：1600 条的 ASS 约 0.2 ms，25 份候选合计不到 10 ms。
    /// </summary>
    /// <param name="cues">已经解析好的台词，markup 已剥掉。</param>
    /// <returns>统计结果；解析不出台词时返回一条「不可用」记录。</returns>
    public static SubtitleHealth Scan(IReadOnlyList<SubtitleCue> cues)
    {
        if (cues.Count == 0)
        {
            return new SubtitleHealth(0, 0, 0, 0, 0, 0, 1, "解析不出台词");
        }

        // ASS 的 Dialogue 行不保证按时间排序，而「空白间隔」只有按开始时间排过才准。
        // 复制一份再排：Cues() 的顺序本身要留给抽样用，不能被这里改掉。
        var ordered = new SubtitleCue[cues.Count];
        for (var i = 0; i < cues.Count; i++)
        {
            ordered[i] = cues[i];
        }

        Array.Sort(ordered, static (left, right) => left.Start.CompareTo(right.Start));

        int chinese = 0, untranslated = 0, halfWidth = 0, timed = 0;
        double maxGap = 0, totalSeconds = 0;
        TimeSpan? previousEnd = null;

        foreach (var cue in ordered)
        {
            var text = cue.Text ?? string.Empty;

            if (ContainsCjk(text))
            {
                chinese++;
            }

            if (HasLatinBracket(text))
            {
                untranslated++;
            }

            if (HasHalfWidthPunctuationAfterCjk(text))
            {
                halfWidth++;
            }

            var seconds = (cue.End - cue.Start).TotalSeconds;
            if (seconds > 0)
            {
                totalSeconds += seconds;
                timed++;
            }

            if (previousEnd is { } end && cue.Start > end)
            {
                var gap = (cue.Start - end).TotalSeconds;
                if (gap > maxGap)
                {
                    maxGap = gap;
                }
            }

            // 相邻两条可能时间重叠（同一句的双语/双行版本），取较大的结束时间才不会
            // 把重叠部分算成空白。
            if (previousEnd is null || cue.End > previousEnd.Value)
            {
                previousEnd = cue.End;
            }
        }

        var average = timed == 0 ? 0 : totalSeconds / timed;
        var ratio = (double)chinese / ordered.Length;

        var defects = 0;
        List<string>? reasons = null;

        if (ratio < MinimumChineseRatio)
        {
            defects++;
            (reasons ??= []).Add($"含中文 {ratio:P0}");
        }

        if (untranslated > MaximumUntranslatedCues)
        {
            defects++;
            (reasons ??= []).Add($"未译标注 {untranslated} 条");
        }

        if (maxGap > MaximumGapSeconds)
        {
            defects++;
            (reasons ??= []).Add($"空白 {maxGap:F0} 秒");
        }

        if (average > MaximumAverageCueSeconds)
        {
            defects++;
            (reasons ??= []).Add($"均长 {average:F1} 秒");
        }

        return new SubtitleHealth(
            ordered.Length,
            chinese,
            untranslated,
            halfWidth,
            maxGap,
            average,
            defects,
            reasons is null ? string.Empty : string.Join("，", reasons));
    }

    /// <summary>True when the text contains at least one Chinese character.</summary>
    private static bool ContainsCjk(string text)
    {
        foreach (var ch in text)
        {
            if (SubtitleText.IsCjk(ch))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 判断方括号标注里装的是不是没翻译的拉丁字母。
    ///
    /// 只看第一个 <c>[</c> 后面第一个非空格字符：
    /// <c>[Jack sighs]</c> 算未翻译，<c>[众人笑声]</c> 不算（那是本地化过的）。
    /// 全角方括号「［」不参与判断 —— 中文排版里那是正常标点。
    /// </summary>
    private static bool HasLatinBracket(string text)
    {
        var open = text.IndexOf('[');
        if (open < 0)
        {
            return false;
        }

        for (var i = open + 1; i < text.Length; i++)
        {
            var ch = text[i];

            if (ch is ' ' or '\t')
            {
                continue;
            }

            if (ch == ']')
            {
                return false;
            }

            return ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
        }

        return false;
    }

    /// <summary>
    /// 判断是否有「中文字后面紧跟半角标点」。
    /// 只看紧邻的那一个字符：<c>Mr. Smith</c>、「3.5」这类正常英文/数字用法不会命中，
    /// 因为它们的前一个字符不是中文。
    /// </summary>
    private static bool HasHalfWidthPunctuationAfterCjk(string text)
    {
        for (var i = 1; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is not (',' or '.' or '!' or '?'))
            {
                continue;
            }

            if (SubtitleText.IsCjk(text[i - 1]))
            {
                return true;
            }
        }

        return false;
    }
}
