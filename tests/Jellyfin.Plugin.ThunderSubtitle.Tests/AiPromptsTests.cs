using Jellyfin.Plugin.ThunderSubtitle.Core;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

/// <summary>
/// 三类影片的 system prompt 必须各有侧重：串味会让模型去评不该评的东西
/// （例如给中文影视挑翻译腔，或给 AV 数字幕错别字）。
/// </summary>
public class AiPromptsTests
{
    [Fact]
    public void EachKindGetsItsOwnSystemPrompt()
    {
        var foreign = AiPrompts.System(MediaKind.Foreign);
        var chinese = AiPrompts.System(MediaKind.Chinese);
        var av = AiPrompts.System(MediaKind.JapaneseAv);

        Assert.NotEqual(foreign, chinese);
        Assert.NotEqual(chinese, av);
        Assert.NotEqual(foreign, av);
    }

    [Fact]
    public void OpeningPlaceholderIsAlwaysReplaced()
    {
        // 占位符漏替换意味着模型会看到「{OPENING}」这种莫名其妙的字面量。
        foreach (var kind in new[] { MediaKind.Foreign, MediaKind.Chinese, MediaKind.JapaneseAv })
        {
            Assert.DoesNotContain("{OPENING}", AiPrompts.System(kind), System.StringComparison.Ordinal);
            Assert.DoesNotContain("{OPENING}", AiPrompts.System(kind, singleCandidate: true), System.StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MultiCandidatePromptKeepsTheComparisonWording()
    {
        var prompt = AiPrompts.System(MediaKind.Foreign);

        Assert.Contains("若干份", prompt);
        Assert.Contains("请横向对比", prompt);
    }

    [Fact]
    public void SingleCandidatePromptAsksForAnAbsoluteScore()
    {
        // 只有一份候选时还叫模型「横向对比，判断哪一份更好」，它会不知所措。
        // 换成「直接评估这一份」之后，评分锚点（90-100 像原创 / 0-39 机翻严重）仍然适用。
        var prompt = AiPrompts.System(MediaKind.Foreign, singleCandidate: true);

        Assert.DoesNotContain("请横向对比", prompt);
        Assert.Contains("只有这一份，不需要比较", prompt);
        Assert.Contains("请直接评估它的翻译质量", prompt);

        // 评分锚点必须原样保留 —— 单份能评出有意义的分数，全靠这几档绝对标准。
        Assert.Contains("90-100", prompt);
        Assert.Contains("0-39", prompt);
    }

    [Fact]
    public void SingleCandidateChinesePromptDoesNotSaySeveralCopies()
    {
        var prompt = AiPrompts.System(MediaKind.Chinese, singleCandidate: true);

        Assert.DoesNotContain("若干份", prompt);
        Assert.Contains("一份字幕", prompt);
        Assert.Contains("错别字", prompt);
    }

    [Fact]
    public void AvPromptNeedsNoSingleCandidateVariant()
    {
        // AV 的提示词本来就没提「几份」，单份与多份共用同一段文本。
        Assert.Equal(AiPrompts.System(MediaKind.JapaneseAv), AiPrompts.System(MediaKind.JapaneseAv, singleCandidate: true));
    }

    [Fact]
    public void ForeignPromptJudgesHowChineseItReads()
    {
        var prompt = AiPrompts.System(MediaKind.Foreign);

        Assert.Contains("母语感", prompt);
        Assert.Contains("上下文", prompt);
        Assert.Contains("题材、年代", prompt);

        // 非中文影视不看错别字，那项统计会分散注意力。
        Assert.DoesNotContain("错别字率", prompt);
    }

    [Fact]
    public void ChinesePromptCountsTyposAndIgnoresTheEnglishLine()
    {
        var prompt = AiPrompts.System(MediaKind.Chinese);

        Assert.Contains("错别字", prompt);
        Assert.Contains("只看中文行", prompt);
        Assert.Contains("英文行不参与错别字统计", prompt);

        // 中文影视的字幕就是台词本身，不该评「翻译得像不像中文」。
        Assert.DoesNotContain("母语感", prompt);
    }

    [Fact]
    public void AvPromptKeepsTheMachineTranslationCheckButRanksByQuality()
    {
        var prompt = AiPrompts.System(MediaKind.JapaneseAv);

        Assert.Contains("机翻特征", prompt);
        Assert.Contains("source", prompt);

        // 关键：机翻只是来源标记。只因为「像 AI 翻译」就压分，会把能用的机翻全筛掉。
        Assert.Contains("不要因为「看起来是 AI 翻译」就直接压到低分", prompt);
    }

    [Theory]
    [InlineData(MediaKind.Foreign)]
    [InlineData(MediaKind.Chinese)]
    [InlineData(MediaKind.JapaneseAv)]
    public void EveryPromptAsksForQuotedEvidence(MediaKind kind)
    {
        var prompt = AiPrompts.System(kind);

        // evidence 是让模型真读文本的抓手：必须原样复制一句，本地会核对。
        Assert.Contains("evidence", prompt);
        Assert.Contains("原样复制", prompt);
        Assert.Contains("JSON", prompt);
    }
}
