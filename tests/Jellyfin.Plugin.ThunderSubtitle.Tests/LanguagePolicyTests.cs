using System.Collections.Generic;
using System.Text;
using Jellyfin.Plugin.ThunderSubtitle.Core;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

/// <summary>
/// 语言档位与质量降级的单元测试。
///
/// 档位表（0 最好）：
/// - 非中文影视：简英双语 0 / 纯简体 1 / 繁体 2
/// - 中文影视：  纯简体 0 / 纯繁体 1 / 双语 2
/// - 日本AV：    纯简体 0 / 纯繁体 1 / 双语 2
/// </summary>
public class LanguagePolicyTests
{
    private const string SimplifiedLine = "这是我们说过的问题。";

    private const string TraditionalLine = "這是我們說過的問題。";

    private const string EnglishLine = "We have talked about this before.";

    [Fact]
    public void ForeignPrefersBilingualThenSimplifiedThenTraditional()
    {
        Assert.Equal(0, LanguagePolicy.TierOf(MediaKind.Foreign, SubtitleLanguage.SimplifiedChineseEnglish));
        Assert.Equal(1, LanguagePolicy.TierOf(MediaKind.Foreign, SubtitleLanguage.SimplifiedChinese));
        Assert.Equal(2, LanguagePolicy.TierOf(MediaKind.Foreign, SubtitleLanguage.TraditionalChinese));
        Assert.Equal(2, LanguagePolicy.TierOf(MediaKind.Foreign, SubtitleLanguage.TraditionalChineseEnglish));
    }

    [Theory]
    [InlineData(MediaKind.Chinese)]
    [InlineData(MediaKind.JapaneseAv)]
    public void ChineseAndAvPreferSimplifiedThenTraditionalThenBilingual(MediaKind kind)
    {
        Assert.Equal(0, LanguagePolicy.TierOf(kind, SubtitleLanguage.SimplifiedChinese));
        Assert.Equal(1, LanguagePolicy.TierOf(kind, SubtitleLanguage.TraditionalChinese));
        Assert.Equal(2, LanguagePolicy.TierOf(kind, SubtitleLanguage.SimplifiedChineseEnglish));
        Assert.Equal(2, LanguagePolicy.TierOf(kind, SubtitleLanguage.TraditionalChineseEnglish));
    }

    [Theory]
    [InlineData(MediaKind.Foreign)]
    [InlineData(MediaKind.Chinese)]
    [InlineData(MediaKind.JapaneseAv)]
    public void NonChineseLanguagesAreNotAcceptable(MediaKind kind)
    {
        Assert.Equal(LanguagePolicy.Unacceptable, LanguagePolicy.TierOf(kind, SubtitleLanguage.Japanese));
        Assert.Equal(LanguagePolicy.Unacceptable, LanguagePolicy.TierOf(kind, SubtitleLanguage.Korean));
        Assert.Equal(LanguagePolicy.Unacceptable, LanguagePolicy.TierOf(kind, SubtitleLanguage.English));
        Assert.Equal(LanguagePolicy.Unacceptable, LanguagePolicy.TierOf(kind, SubtitleLanguage.Unknown));
        Assert.False(LanguagePolicy.IsAcceptable(kind, SubtitleLanguage.English));
        Assert.True(LanguagePolicy.IsAcceptable(kind, SubtitleLanguage.SimplifiedChinese));
    }

    [Fact]
    public void TierNamesMatchEachMediaKind()
    {
        Assert.Equal("简英双语", LanguagePolicy.TierName(MediaKind.Foreign, 0));
        Assert.Equal("纯简体", LanguagePolicy.TierName(MediaKind.Foreign, 1));
        Assert.Equal("繁体", LanguagePolicy.TierName(MediaKind.Foreign, 2));
        Assert.Equal("纯简体", LanguagePolicy.TierName(MediaKind.JapaneseAv, 0));
        Assert.Equal("纯繁体", LanguagePolicy.TierName(MediaKind.JapaneseAv, 1));
        Assert.Equal("双语", LanguagePolicy.TierName(MediaKind.JapaneseAv, 2));
    }

    [Fact]
    public void AvDemotesSimplifiedWhenItsBestScoreIsBelowThreshold()
    {
        // 简体 50 分（低于阈值 60），繁体 70 分 —— 这就是「找不到合适的简体」。
        var simplified = Pick("a.简体.srt", SimplifiedLine);
        var traditional = Pick("b.繁体.srt", TraditionalLine);
        var verdicts = Verdicts(50, 70);

        var ranked = SubtitleRanker.ApplyAiVerdicts(
            [simplified, traditional],
            verdicts,
            MediaKind.JapaneseAv,
            simplifiedAcceptScore: 60);

        Assert.Equal(SubtitleLanguage.TraditionalChinese, ranked[0].Language);
        Assert.Equal(SubtitleLanguage.SimplifiedChinese, ranked[1].Language);
    }

    [Fact]
    public void AvKeepsSimplifiedWhenItReachesTheThreshold()
    {
        // 简体 65 分已达到合格线，即使繁体 70 分更高也仍然用简体。
        var simplified = Pick("a.简体.srt", SimplifiedLine);
        var traditional = Pick("b.繁体.srt", TraditionalLine);
        var verdicts = Verdicts(65, 70);

        var ranked = SubtitleRanker.ApplyAiVerdicts(
            [simplified, traditional],
            verdicts,
            MediaKind.JapaneseAv,
            simplifiedAcceptScore: 60);

        Assert.Equal(SubtitleLanguage.SimplifiedChinese, ranked[0].Language);
    }

    [Fact]
    public void AvKeepsSimplifiedWhenFallbackIsNotBetter()
    {
        // 简体 40 分不合格，但繁体只有 35 分：降级没有意义，仍然保留简体在前。
        var simplified = Pick("a.简体.srt", SimplifiedLine);
        var traditional = Pick("b.繁体.srt", TraditionalLine);
        var verdicts = Verdicts(40, 35);

        var ranked = SubtitleRanker.ApplyAiVerdicts(
            [simplified, traditional],
            verdicts,
            MediaKind.JapaneseAv,
            simplifiedAcceptScore: 60);

        Assert.Equal(SubtitleLanguage.SimplifiedChinese, ranked[0].Language);
    }

    [Fact]
    public void ForeignKeepsBilingualWhenTierOverrideIsDisabled()
    {
        // 跨档分差填 0 = 关闭择优：仍然是无条件「有中英双语就用双语」。
        var bilingual = Pick("a.简英.srt", Bilingual(20));
        var simplified = Pick("b.简体.srt", SimplifiedLine);
        var verdicts = Verdicts(40, 90);

        var ranked = SubtitleRanker.ApplyAiVerdicts(
            [bilingual, simplified],
            verdicts,
            MediaKind.Foreign,
            simplifiedAcceptScore: 60,
            tierOverrideGap: 0);

        Assert.Equal(SubtitleLanguage.SimplifiedChineseEnglish, ranked[0].Language);
    }

    [Fact]
    public void ForeignPromotesLowerTierWhenItScoresWellAbove()
    {
        // 《加勒比海盗》实测的场景：国配台本 81 分，最好的双语版 73 分，差 8 分。
        // 这时候该用国配台本 —— 它的译文质量高到值得放弃双语对照。
        var bilingual = Pick("a.简英.srt", Bilingual(20));
        var simplified = Pick("b.简体.srt", SimplifiedLine);
        var verdicts = Verdicts(73, 81);

        var ranked = SubtitleRanker.ApplyAiVerdicts(
            [bilingual, simplified],
            verdicts,
            MediaKind.Foreign,
            tierOverrideGap: 8);

        Assert.Equal(SubtitleLanguage.SimplifiedChinese, ranked[0].Language);
    }

    [Fact]
    public void ForeignKeepsBilingualWhenTheGapIsTooSmall()
    {
        // 差 4 分不足以推翻语言偏好：AI 打分有主观波动，
        // 同一份字幕换个批次可能就差三五分。
        var bilingual = Pick("a.简英.srt", Bilingual(20));
        var simplified = Pick("b.简体.srt", SimplifiedLine);
        var verdicts = Verdicts(77, 81);

        var ranked = SubtitleRanker.ApplyAiVerdicts(
            [bilingual, simplified],
            verdicts,
            MediaKind.Foreign,
            tierOverrideGap: 8);

        Assert.Equal(SubtitleLanguage.SimplifiedChineseEnglish, ranked[0].Language);
    }

    [Fact]
    public void ForeignPromotesOnlyTheBestChallenger()
    {
        // 只提升分数最高的那一份。同档位 60 分的兄弟不能被 81 分带上来 ——
        // 否则它会挤掉本该优先的双语版本。
        var bilingual = Pick("a.简英.srt", Bilingual(20));
        var strong = Pick("b.简体强.srt", SimplifiedLine);
        var weak = Pick("c.简体弱.srt", TraditionalLine);
        var verdicts = new Dictionary<int, AiVerdict>
        {
            [1] = new AiVerdict(1, 60, false, "双语"),
            [2] = new AiVerdict(2, 90, false, "国配台本"),
            [3] = new AiVerdict(3, 55, false, "另一份简体")
        };

        var ranked = SubtitleRanker.ApplyAiVerdicts(
            [bilingual, strong, weak],
            verdicts,
            MediaKind.Foreign,
            tierOverrideGap: 8);

        Assert.Equal("b.简体强.srt", ranked[0].CandidateName);
        Assert.Equal("a.简英.srt", ranked[1].CandidateName);
        Assert.Equal("c.简体弱.srt", ranked[2].CandidateName);
    }

    [Fact]
    public void ZeroThresholdDisablesDemotion()
    {
        var simplified = Pick("a.简体.srt", SimplifiedLine);
        var traditional = Pick("b.繁体.srt", TraditionalLine);
        var verdicts = Verdicts(10, 90);

        var ranked = SubtitleRanker.ApplyAiVerdicts(
            [simplified, traditional],
            verdicts,
            MediaKind.JapaneseAv,
            simplifiedAcceptScore: 0);

        Assert.Equal(SubtitleLanguage.SimplifiedChinese, ranked[0].Language);
    }

    private static Dictionary<int, AiVerdict> Verdicts(double first, double second)
    {
        return new Dictionary<int, AiVerdict>
        {
            [1] = new AiVerdict(1, first, true, "第一份"),
            [2] = new AiVerdict(2, second, false, "第二份")
        };
    }

    /// <summary>
    /// 构造候选。传单行台词时自动包成 30 条 SRT —— 语言检测要求至少 20 个汉字，
    /// 单行短句会被判成 <see cref="SubtitleLanguage.Unknown"/>，那样就测不到档位了。
    /// 传完整 SRT（双语用例）时原样使用。
    /// </summary>
    private static SubtitlePick Pick(string name, string text)
    {
        var body = text.Contains("-->", System.StringComparison.Ordinal) ? text : Srt(text);
        return new SubtitlePick(name, ".srt", body, "utf-8");
    }

    private static string Srt(string line, int cues = 30)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < cues; i++)
        {
            builder.Append(i + 1).Append('\n');
            builder.Append($"00:00:{i % 60:D2},000 --> 00:00:{i % 60:D2},800\n");
            builder.Append(line).Append("\n\n");
        }

        return builder.ToString();
    }

    private static string Bilingual(int cues)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < cues; i++)
        {
            builder.Append(i + 1).Append('\n');
            builder.Append($"00:00:{i % 60:D2},000 --> 00:00:{i % 60:D2},800\n");
            builder.Append(SimplifiedLine).Append('\n');
            builder.Append(EnglishLine).Append("\n\n");
        }

        return builder.ToString();
    }
}
