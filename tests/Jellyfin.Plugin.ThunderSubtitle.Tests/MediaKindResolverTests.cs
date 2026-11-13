using Jellyfin.Plugin.ThunderSubtitle.Core;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

/// <summary>
/// 影片类型判定表的单元测试。
///
/// 判定规则（优先级从高到低）：
/// 1. 番号库 → 日本AV
/// 2. OriginalLanguage 是中文 → 中文影视
/// 3. OriginalLanguage 是其他明确语言 → 非中文影视
/// 4. 语言为空 + 中文地区 → 中文影视
/// 5. 其余 → 非中文影视
/// </summary>
public class MediaKindResolverTests
{
    [Fact]
    public void CodeLibraryIsAlwaysJapaneseAv()
    {
        // 番号库即使刮削出了中文语言，也按 AV 处理：
        // 「按番号检索」是唯一不依赖库名的稳定信号。
        Assert.Equal(
            MediaKind.JapaneseAv,
            MediaKindResolver.Resolve(SubtitleSearchMode.Code, "zh", ["CN"]));

        Assert.Equal(
            MediaKind.JapaneseAv,
            MediaKindResolver.Resolve(SubtitleSearchMode.Code, null, null));
    }

    [Fact]
    public void UnknownLibraryIsNeverTreatedAsAv()
    {
        // 用户定的规则：只有显式配置成番号检索的库才是日本 AV。
        // null 表示「条目不属于任何已配置的库」或「那个库从未保存过设置」，
        // 这两种情况都要继续按语言和地区判断，绝不能猜成 AV。
        Assert.Equal(MediaKind.Foreign, MediaKindResolver.Resolve(null, null, null));
        Assert.Equal(MediaKind.Foreign, MediaKindResolver.Resolve(null, "en", null));
        Assert.Equal(MediaKind.Chinese, MediaKindResolver.Resolve(null, "zh", null));
        Assert.Equal(MediaKind.Chinese, MediaKindResolver.Resolve(null, null, ["HK"]));
    }

    [Theory]
    [InlineData("zh")]
    [InlineData("zho")]
    [InlineData("chi")]
    [InlineData("cmn")]
    [InlineData("yue")]
    [InlineData("zh-CN")]
    [InlineData("zh_TW")]
    [InlineData("zh-Hans")]
    [InlineData("ZH")]
    public void ChineseLanguageMeansChineseFilm(string language)
    {
        Assert.Equal(
            MediaKind.Chinese,
            MediaKindResolver.Resolve(SubtitleSearchMode.Title, language, null));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("fr")]
    public void OtherLanguageMeansForeignFilm(string language)
    {
        Assert.Equal(
            MediaKind.Foreign,
            MediaKindResolver.Resolve(SubtitleSearchMode.Title, language, null));
    }

    [Fact]
    public void LanguageWinsOverLocation()
    {
        // 中日合拍片：原声是日语，制作地区含中国，仍然需要中文翻译字幕。
        Assert.Equal(
            MediaKind.Foreign,
            MediaKindResolver.Resolve(SubtitleSearchMode.Title, "ja", ["CN"]));
    }

    [Theory]
    [InlineData("CN")]
    [InlineData("TW")]
    [InlineData("HK")]
    [InlineData("MO")]
    [InlineData("cn")]
    public void ChineseLocationIsUsedOnlyWhenLanguageIsMissing(string location)
    {
        Assert.Equal(
            MediaKind.Chinese,
            MediaKindResolver.Resolve(SubtitleSearchMode.Title, null, [location]));
    }

    [Fact]
    public void OtherLocationWithMissingLanguageDefaultsToForeign()
    {
        Assert.Equal(
            MediaKind.Foreign,
            MediaKindResolver.Resolve(SubtitleSearchMode.Title, null, ["US"]));

        Assert.Equal(
            MediaKind.Foreign,
            MediaKindResolver.Resolve(SubtitleSearchMode.Title, null, ["SG"]));

        Assert.Equal(
            MediaKind.Foreign,
            MediaKindResolver.Resolve(SubtitleSearchMode.Title, null, []));
    }

    [Fact]
    public void MissingEverythingDefaultsToForeign()
    {
        Assert.Equal(
            MediaKind.Foreign,
            MediaKindResolver.Resolve(SubtitleSearchMode.Title, null, null));

        Assert.Equal(
            MediaKind.Foreign,
            MediaKindResolver.Resolve(SubtitleSearchMode.Title, "   ", null));
    }

    [Fact]
    public void MultipleLocationsMatchWhenAnyIsChinese()
    {
        Assert.Equal(
            MediaKind.Chinese,
            MediaKindResolver.Resolve(SubtitleSearchMode.Title, null, ["US", "HK"]));
    }

    [Fact]
    public void DisplayNamesAreChinese()
    {
        Assert.Equal("中文影视", MediaKindResolver.DisplayName(MediaKind.Chinese));
        Assert.Equal("日本AV", MediaKindResolver.DisplayName(MediaKind.JapaneseAv));
        Assert.Equal("非中文影视", MediaKindResolver.DisplayName(MediaKind.Foreign));
    }
}
