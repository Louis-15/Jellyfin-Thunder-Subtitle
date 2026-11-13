using System;
using System.Text;
﻿using Jellyfin.Plugin.ThunderSubtitle.Core;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

public class SubtitleLanguageTests
{
    private const string Simplified =
        "看 这是今年的新人 从美国留学回来的 是吗 那么没头没脑的新人 也难让课长称心如意吧 我们说过这个问题了";

    private const string Traditional =
        "看 這是今年的新人 從美國留學回來的 是嗎 那麼沒頭沒腦的新人 也難讓課長稱心如意吧 我們說過這個問題了";

    private const string Japanese =
        "ああ 今年の新人か アメリカから留学してきたのか そうか それじゃあ課長も気に入らないだろうな";

    private const string English =
        "Look, this is the new guy this year. He's back from his studies in America. Is that right?";

    private const string BilingualJapanese =
        "ああ 今年の新人か\n看 这是今年的新人\nアメリカから留学してきたのか\n从美国留学回来的\nそうか それじゃあ課長も気に入らないだろうな";

    private const string SimplifiedLine = "这是我们说过的问题。";

    private const string TraditionalLine = "這是我們說過的問題。";

    private const string EnglishLine = "We have talked about this before.";

    [Fact]
    public void DetectsSimplified()
    {
        Assert.Equal(SubtitleLanguage.SimplifiedChinese, SubtitleLanguageDetector.Detect(Simplified));
    }

    [Fact]
    public void DetectsTraditional()
    {
        Assert.Equal(SubtitleLanguage.TraditionalChinese, SubtitleLanguageDetector.Detect(Traditional));
    }

    [Fact]
    public void DetectsJapanese()
    {
        Assert.Equal(SubtitleLanguage.Japanese, SubtitleLanguageDetector.Detect(Japanese));
    }

    [Fact]
    public void DetectsEnglish()
    {
        Assert.Equal(SubtitleLanguage.English, SubtitleLanguageDetector.Detect(English));
    }

    [Fact]
    public void BilingualJapaneseChineseCountsAsJapanese()
    {
        Assert.Equal(SubtitleLanguage.Japanese, SubtitleLanguageDetector.Detect(BilingualJapanese));
    }

    [Fact]
    public void ShortTextIsUnknown()
    {
        Assert.Equal(SubtitleLanguage.Unknown, SubtitleLanguageDetector.Detect("你好"));
        Assert.Equal(SubtitleLanguage.Unknown, SubtitleLanguageDetector.Detect(null));
    }

    [Fact]
    public void IsChineseCoversBothFormsAndBothBilingualForms()
    {
        Assert.True(SubtitleLanguageDetector.IsChinese(SubtitleLanguage.SimplifiedChinese));
        Assert.True(SubtitleLanguageDetector.IsChinese(SubtitleLanguage.TraditionalChinese));
        Assert.True(SubtitleLanguageDetector.IsChinese(SubtitleLanguage.SimplifiedChineseEnglish));
        Assert.True(SubtitleLanguageDetector.IsChinese(SubtitleLanguage.TraditionalChineseEnglish));
        Assert.False(SubtitleLanguageDetector.IsChinese(SubtitleLanguage.Japanese));
        Assert.False(SubtitleLanguageDetector.IsChinese(SubtitleLanguage.English));
        Assert.False(SubtitleLanguageDetector.IsChinese(SubtitleLanguage.Unknown));
    }

    [Fact]
    public void BilingualHelpersSeparateTheFourChineseForms()
    {
        Assert.True(SubtitleLanguageDetector.IsBilingual(SubtitleLanguage.SimplifiedChineseEnglish));
        Assert.True(SubtitleLanguageDetector.IsBilingual(SubtitleLanguage.TraditionalChineseEnglish));
        Assert.False(SubtitleLanguageDetector.IsBilingual(SubtitleLanguage.SimplifiedChinese));
        Assert.False(SubtitleLanguageDetector.IsBilingual(SubtitleLanguage.TraditionalChinese));

        Assert.True(SubtitleLanguageDetector.IsSimplified(SubtitleLanguage.SimplifiedChinese));
        Assert.True(SubtitleLanguageDetector.IsSimplified(SubtitleLanguage.SimplifiedChineseEnglish));
        Assert.False(SubtitleLanguageDetector.IsSimplified(SubtitleLanguage.TraditionalChineseEnglish));

        Assert.True(SubtitleLanguageDetector.IsTraditional(SubtitleLanguage.TraditionalChinese));
        Assert.True(SubtitleLanguageDetector.IsTraditional(SubtitleLanguage.TraditionalChineseEnglish));
        Assert.False(SubtitleLanguageDetector.IsTraditional(SubtitleLanguage.SimplifiedChineseEnglish));
    }

    [Fact]
    public void DisplayNamesCoverEveryValue()
    {
        Assert.Equal("简体", SubtitleLanguageDetector.DisplayName(SubtitleLanguage.SimplifiedChinese));
        Assert.Equal("繁体", SubtitleLanguageDetector.DisplayName(SubtitleLanguage.TraditionalChinese));
        Assert.Equal("简英双语", SubtitleLanguageDetector.DisplayName(SubtitleLanguage.SimplifiedChineseEnglish));
        Assert.Equal("繁英双语", SubtitleLanguageDetector.DisplayName(SubtitleLanguage.TraditionalChineseEnglish));
        Assert.Equal("日文", SubtitleLanguageDetector.DisplayName(SubtitleLanguage.Japanese));
    }

    [Fact]
    public void DetectsSimplifiedEnglishBilingual()
    {
        var text = BilingualSrt(SimplifiedLine, EnglishLine, 20);

        var pick = Pick("x.简英.srt", text);

        Assert.Equal(SubtitleLanguage.SimplifiedChineseEnglish, pick.Language);
    }

    [Fact]
    public void DetectsTraditionalEnglishBilingual()
    {
        var text = BilingualSrt(TraditionalLine, EnglishLine, 20);

        var pick = Pick("x.繁英.srt", text);

        Assert.Equal(SubtitleLanguage.TraditionalChineseEnglish, pick.Language);
    }

    [Fact]
    public void EnglishLineFirstIsStillBilingual()
    {
        // 英文在上、中文在下同样常见；判定只看「同一条字幕里两种语言都在」。
        var text = BilingualSrt(EnglishLine, SimplifiedLine, 20);

        var pick = Pick("x.英上简下.srt", text);

        Assert.Equal(SubtitleLanguage.SimplifiedChineseEnglish, pick.Language);
    }

    [Fact]
    public void SingleProductNameDoesNotMakeBilingual()
    {
        // 中文字幕里夹一个英文产品名很常见，不能据此判定整份字幕是双语。
        var text = BilingualSrt("我用 iPhone 拍的照片。", "iPhone", 20);

        var pick = Pick("x.简.srt", text);

        Assert.Equal(SubtitleLanguage.SimplifiedChinese, pick.Language);
    }

    [Fact]
    public void TooFewBilingualCuesStayPureChinese()
    {
        // 15 条是下限：预告片、短片段不适合按比例判定。
        var text = BilingualSrt(SimplifiedLine, EnglishLine, 5, plainCues: 20);

        var pick = Pick("x.简.srt", text);

        Assert.Equal(SubtitleLanguage.SimplifiedChinese, pick.Language);
    }

    [Fact]
    public void BilingualRatioBelowThresholdStaysPureChinese()
    {
        // 15 / (15 + 60) = 20%，低于 25%：只在个别段落做双语的版本不算双语字幕。
        var text = BilingualSrt(SimplifiedLine, EnglishLine, 15, plainCues: 60);

        var pick = Pick("x.简.srt", text);

        Assert.Equal(SubtitleLanguage.SimplifiedChinese, pick.Language);
    }

    [Fact]
    public void CueBasedDetectionMatchesPickLanguage()
    {
        var text = BilingualSrt(SimplifiedLine, EnglishLine, 20);

        var cues = SubtitleContent.Parse(text, ".srt");
        var language = SubtitleLanguageDetector.Detect(cues, SubtitleContent.StripMarkup(text));

        Assert.Equal(SubtitleLanguage.SimplifiedChineseEnglish, language);
    }

    [Fact]
    public void DetectWithoutCuesFallsBackToFullText()
    {
        Assert.Equal(
            SubtitleLanguage.SimplifiedChinese,
            SubtitleLanguageDetector.Detect(Array.Empty<SubtitleCue>(), Simplified));
    }

    /// <summary>
    /// 生成一份 SRT：前 <paramref name="bilingualCues"/> 条是中英双行，
    /// 后 <paramref name="plainCues"/> 条只有中文。
    /// </summary>
    private static string BilingualSrt(string chinese, string english, int bilingualCues, int plainCues = 0)
    {
        var builder = new StringBuilder();
        var index = 1;

        for (var i = 0; i < bilingualCues; i++)
        {
            builder.Append(index++).Append('\n');
            builder.Append($"00:00:{i:D2},000 --> 00:00:{i:D2},800\n");
            builder.Append(chinese).Append('\n');
            builder.Append(english).Append('\n');
            builder.Append('\n');
        }

        for (var i = 0; i < plainCues; i++)
        {
            builder.Append(index++).Append('\n');
            builder.Append($"00:01:{i:D2},000 --> 00:01:{i:D2},800\n");
            builder.Append(chinese).Append('\n');
            builder.Append('\n');
        }

        return builder.ToString();
    }

    [Fact]
    public void DropsNonChineseCandidates()
    {
        var candidates = new[]
        {
            Pick("a.srt", Simplified),
            Pick("b.srt", Traditional),
            Pick("c.srt", Japanese),
            Pick("d.srt", English),
            Pick("e.srt", English)
        };

        var result = SubtitleRanker.KeepAcceptable(MediaKind.JapaneseAv, candidates);

        Assert.Equal(2, result.Kept.Count);
        Assert.Equal(1, result.Dropped[SubtitleLanguage.Japanese]);
        Assert.Equal(2, result.Dropped[SubtitleLanguage.English]);
    }

    [Theory]
    [InlineData(MediaKind.Foreign)]
    [InlineData(MediaKind.Chinese)]
    [InlineData(MediaKind.JapaneseAv)]
    public void SimplifiedWinsOverTraditionalForEveryKind(MediaKind kind)
    {
        var traditional = Pick("x.繁体.srt", Traditional);
        var simplified = Pick("x.简体.srt", Simplified);

        var ranked = SubtitleRanker.Rank([traditional, simplified], kind);

        Assert.Equal(SubtitleLanguage.SimplifiedChinese, ranked[0].Language);
    }

    [Fact]
    public void EnglishFileOnDiskIsAlwaysReplaced()
    {
        var chinese = Pick("x.chi.srt", Simplified);
        var onDisk = new ExistingSubtitle(new SubtitleStats(1000, 50_000), SubtitleLanguage.English, null, 0);

        Assert.True(SubtitleRanker.IsUpgrade(chinese, null, onDisk, MediaKind.JapaneseAv));
    }

    [Fact]
    public void LowerTierReplacesHigherTierAndNeverTheOtherWayRound()
    {
        // 日本AV：简体（Tier 0）替换繁体（Tier 1），繁体永远不反过来替换简体。
        var simplified = Pick("x.chi.srt", Simplified);
        var traditionalOnDisk = new ExistingSubtitle(new SubtitleStats(10, 500), SubtitleLanguage.TraditionalChinese, null, 0);
        Assert.True(SubtitleRanker.IsUpgrade(simplified, null, traditionalOnDisk, MediaKind.JapaneseAv));

        var traditional = Pick("x.chi.srt", Traditional);
        var simplifiedOnDisk = new ExistingSubtitle(new SubtitleStats(10, 500), SubtitleLanguage.SimplifiedChinese, null, 0);
        Assert.False(SubtitleRanker.IsUpgrade(traditional, null, simplifiedOnDisk, MediaKind.JapaneseAv));
    }

    [Fact]
    public void BilingualBeatsSimplifiedOnlyForForeignFilms()
    {
        // 非中文影视：简英双语是 Tier 0，纯简体是 Tier 1。
        var bilingual = Pick("x.chi.srt", BilingualSrt(SimplifiedLine, EnglishLine, 20));
        var simplified = Pick("x.chi.srt", Simplified);

        Assert.Equal(SubtitleLanguage.SimplifiedChineseEnglish, bilingual.Language);

        var ranked = SubtitleRanker.Rank([simplified, bilingual], MediaKind.Foreign);
        Assert.Equal(SubtitleLanguage.SimplifiedChineseEnglish, ranked[0].Language);

        // 日本AV：纯简体才是 Tier 0，双语排最后。
        var avRanked = SubtitleRanker.Rank([bilingual, simplified], MediaKind.JapaneseAv);
        Assert.Equal(SubtitleLanguage.SimplifiedChinese, avRanked[0].Language);
    }

    private static SubtitlePick Pick(string name, string text) => new(name, ".srt", text, "utf-8");
}
