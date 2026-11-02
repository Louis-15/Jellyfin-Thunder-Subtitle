using Jellyfin.Plugin.ThunderSubtitle.Core;
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
    public void IsChineseOnlyForTheTwoChineseForms()
    {
        Assert.True(SubtitleLanguageDetector.IsChinese(SubtitleLanguage.SimplifiedChinese));
        Assert.True(SubtitleLanguageDetector.IsChinese(SubtitleLanguage.TraditionalChinese));
        Assert.False(SubtitleLanguageDetector.IsChinese(SubtitleLanguage.Japanese));
        Assert.False(SubtitleLanguageDetector.IsChinese(SubtitleLanguage.English));
        Assert.False(SubtitleLanguageDetector.IsChinese(SubtitleLanguage.Unknown));
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

        var result = SubtitleRanker.KeepChineseOnly(candidates);

        Assert.Equal(2, result.Kept.Count);
        Assert.Equal(1, result.Dropped[SubtitleLanguage.Japanese]);
        Assert.Equal(2, result.Dropped[SubtitleLanguage.English]);
    }

    [Fact]
    public void PrefersSimplifiedByDefault()
    {
        var traditional = Pick("x.繁体.srt", Traditional);
        var simplified = Pick("x.简体.srt", Simplified);

        var ranked = SubtitleRanker.Rank([traditional, simplified]);

        Assert.Equal(SubtitleLanguage.SimplifiedChinese, ranked[0].Language);
    }

    [Fact]
    public void PrefersTraditionalWhenConfigured()
    {
        var traditional = Pick("x.繁体.srt", Traditional);
        var simplified = Pick("x.简体.srt", Simplified);

        var ranked = SubtitleRanker.Rank([simplified, traditional], null, SubtitleLanguage.TraditionalChinese);

        Assert.Equal(SubtitleLanguage.TraditionalChinese, ranked[0].Language);
    }

    [Fact]
    public void EnglishFileOnDiskIsAlwaysReplaced()
    {
        var chinese = Pick("x.chi.srt", Simplified);
        var onDisk = new ExistingSubtitle(new SubtitleStats(1000, 50_000), SubtitleLanguage.English, null, 0);

        Assert.True(SubtitleRanker.IsUpgrade(chinese, onDisk, SubtitleLanguage.SimplifiedChinese));
    }

    [Fact]
    public void PreferredFormReplacesTheOtherForm()
    {
        var simplified = Pick("x.chi.srt", Simplified);
        var traditionalOnDisk = new ExistingSubtitle(new SubtitleStats(10, 500), SubtitleLanguage.TraditionalChinese, null, 0);

        Assert.True(SubtitleRanker.IsUpgrade(simplified, traditionalOnDisk, SubtitleLanguage.SimplifiedChinese));

        var traditional = Pick("x.chi.srt", Traditional);
        var simplifiedOnDisk = new ExistingSubtitle(new SubtitleStats(10, 500), SubtitleLanguage.SimplifiedChinese, null, 0);
        Assert.False(SubtitleRanker.IsUpgrade(traditional, simplifiedOnDisk, SubtitleLanguage.SimplifiedChinese));
    }

    private static SubtitlePick Pick(string name, string text) => new(name, ".srt", text, "utf-8");
}
