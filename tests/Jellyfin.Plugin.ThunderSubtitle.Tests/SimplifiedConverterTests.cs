using System;
using System.Diagnostics;
using System.Text;
using Jellyfin.Plugin.ThunderSubtitle.Core;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

/// <summary>
/// 繁转简的正确性与判定规则。
///
/// 这套映射来自 OpenCC 的 TSCharacters / TSPhrases，最容易踩的坑是「歧义字」：
/// 「乾」在单字表里映射到「干」，但「乾坤」「乾隆」必须原样保留。
/// 词组表里那些「繁 → 它自己」的保护条目就是干这个的，删掉就会出错。
/// </summary>
public class SimplifiedConverterTests
{
    [Theory]
    [InlineData("這是我們的問題", "这是我们的问题")]
    [InlineData("他說：「為什麼？」", "他说：「为什么？」")]
    [InlineData("電視機壞了", "电视机坏了")]
    [InlineData("學習繁體字", "学习繁体字")]
    public void ConvertsCommonTraditionalText(string traditional, string expected)
    {
        Assert.Equal(expected, SimplifiedConverter.ToSimplified(traditional));
    }

    [Fact]
    public void LeavesSimplifiedTextUntouched()
    {
        const string Text = "这是我们的问题，不要再说了。";

        Assert.Equal(Text, SimplifiedConverter.ToSimplified(Text));
    }

    [Fact]
    public void KeepsAmbiguousCharactersThatPhrasesProtect()
    {
        // 「乾」→「干」是单字表的默认行为，但这几个词必须原样保留。
        Assert.Equal("乾坤大挪移", SimplifiedConverter.ToSimplified("乾坤大挪移"));
        Assert.Equal("乾隆皇帝", SimplifiedConverter.ToSimplified("乾隆皇帝"));
        Assert.Equal("扭转乾坤", SimplifiedConverter.ToSimplified("扭轉乾坤"));

        // 而「乾淨」该转成「干净」。
        Assert.Equal("干净", SimplifiedConverter.ToSimplified("乾淨"));
    }

    [Fact]
    public void PrefersTheLongestPhrase()
    {
        // 「乾乾淨淨」优先于「乾淨」，整词一次转完，不会出现「干淨」这种半成品。
        Assert.Equal("干干净净", SimplifiedConverter.ToSimplified("乾乾淨淨"));
    }

    [Fact]
    public void LeavesMixedScriptContentConvertible()
    {
        // 繁简混用是最常见的实际情形：一部分被手工改过，剩下的漏了。
        const string Mixed = "這是第一句（简体部分），這是第二句（简体部分），這是第三句。";

        var converted = SimplifiedConverter.ToSimplified(Mixed);

        Assert.DoesNotContain('這', converted);
        Assert.Contains("这是第一句", converted);
    }

    [Fact]
    public void KeepsNonChineseTextAndTimingsIntact()
    {
        const string Srt =
            "1\n00:00:01,000 --> 00:00:03,000\n這是我們的問題\n\n"
            + "2\n00:00:04,000 --> 00:00:06,000\nWe never talked about it.\n";

        var converted = SimplifiedConverter.ToSimplified(Srt);

        Assert.Contains("00:00:01,000 --> 00:00:03,000", converted);
        Assert.Contains("这是我们的问题", converted);
        Assert.Contains("We never talked about it.", converted);
    }

    [Fact]
    public void DetectsWhatNeedsConverting()
    {
        Assert.True(SimplifiedConverter.ContainsTraditional("這是我們的問題"));
        Assert.False(SimplifiedConverter.ContainsTraditional("这是我们的问题"));
        Assert.False(SimplifiedConverter.ContainsTraditional(""));
        Assert.False(SimplifiedConverter.ContainsTraditional(null));
    }

    [Fact]
    public void ClassifiesCleanTraditionalAndCleanSimplified()
    {
        Assert.Equal(ChineseScript.Traditional, ChineseScriptDetector.Detect("這是我們的問題嗎我們說過這個問題了不是嗎"));
        Assert.Equal(ChineseScript.Simplified, ChineseScriptDetector.Detect("这是我们的问题吗我们说过这个问题了不是吗"));
    }

    [Fact]
    public void ClassifiesHeavyMixingAsMixed()
    {
        // 一半繁体一半简体：无论按多数派算哪边都不准确，必须单独识别出来。
        var mixed = "這是我們說過的這個問題了嗎"
                    + "这是这个问题的说法了我们没错对不对";

        Assert.Equal(ChineseScript.Mixed, ChineseScriptDetector.Detect(mixed));
    }

    [Fact]
    public void AStrayTraditionalCharacterDoesNotMakeItMixed()
    {
        // 「繁体里的个别简体」和「简体里的个别繁体」都只是瑕疵，不该改标签。
        var mostlyTraditional = new StringBuilder();
        for (var i = 0; i < 30; i++)
        {
            mostlyTraditional.Append("這是我們的問題");
        }

        mostlyTraditional.Append("这");

        Assert.Equal(ChineseScript.Traditional, ChineseScriptDetector.Detect(mostlyTraditional.ToString()));
    }

    [Fact]
    public void ShortSamplesAreNotCalledMixed()
    {
        // 只有十几个特征字的短样本，3 个字的差异占比就超过 10% 了，
        // 但那只是正常抖动 —— 绝对数量门槛拦住它。
        Assert.NotEqual(ChineseScript.Mixed, ChineseScriptDetector.Detect("這是這"));
    }

    [Fact]
    public void MixedAlsoNeedsConversion()
    {
        // 用户的原话：「部分是繁体部分简体也要触发这个功能」。
        Assert.True(ChineseScriptDetector.NeedsConversion(ChineseScript.Mixed));
        Assert.True(ChineseScriptDetector.NeedsConversion(ChineseScript.Traditional));
        Assert.False(ChineseScriptDetector.NeedsConversion(ChineseScript.Simplified));
    }

    [Fact]
    public void ConversionIsFastEnoughForAFullSubtitle()
    {
        // 1600 条、约 3 万字的真实字幕，转一次应当远低于一次网络请求的耗时。
        var builder = new StringBuilder();
        for (var i = 0; i < 400; i++)
        {
            builder.Append(i).Append('\n')
                .Append("00:00:01,000 --> 00:00:03,000\n")
                .Append("這是我們說過的這個問題了嗎我們沒有錯\n\n");
        }

        var text = builder.ToString();
        var watch = Stopwatch.StartNew();
        var converted = SimplifiedConverter.ToSimplified(text);
        watch.Stop();

        Assert.DoesNotContain('這', converted);
        Assert.True(watch.ElapsedMilliseconds < 500, $"转换耗时 {watch.ElapsedMilliseconds} ms，超出预期");
    }
}
