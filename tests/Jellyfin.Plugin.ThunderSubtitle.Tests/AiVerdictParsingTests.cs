using Jellyfin.Plugin.ThunderSubtitle.Core;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

/// <summary>
/// 模型回复的解析容错，以及 evidence（引用原句）的本地核对。
///
/// evidence 是抑制「全给 80 分」的抓手：模型必须复制一句真实存在的话，
/// 复制不出来就说明它没认真读，日志里一眼能看出来。
/// </summary>
public class AiVerdictParsingTests
{
    private const string SrtA =
        "1\n00:00:10,000 --> 00:00:13,000\n我从来没像现在这样害怕过\n\n" +
        "2\n00:00:20,000 --> 00:00:23,000\n他不会明白的\n\n";

    private const string SrtB =
        "1\n00:00:10,000 --> 00:00:13,000\n我从来没有像现在一样恐惧\n\n" +
        "2\n00:00:20,000 --> 00:00:23,000\n他是不会明白的\n\n";

    private static readonly SubtitlePick[] Shortlist =
    [
        new SubtitlePick("a.srt", ".srt", SrtA, "utf-8"),
        new SubtitlePick("b.srt", ".srt", SrtB, "utf-8")
    ];

    private static string Reply(string content)
    {
        return "{\"choices\":[{\"message\":{\"content\":" + System.Text.Json.JsonSerializer.Serialize(content) + "}}]}";
    }

    [Fact]
    public void ParsesOpenAiStyleReplyWrappedInAMarkdownFence()
    {
        const string payload = """
        {
          "id": "x",
          "choices": [
            { "index": 0, "message": { "role": "assistant",
              "content": "```json\n{\"candidates\":[{\"id\":1,\"score\":40,\"machine\":true,\"reason\":\"直译生硬\"},{\"id\":2,\"score\":88,\"machine\":false,\"reason\":\"口语自然\"}]}\n```" } }
          ]
        }
        """;

        var verdicts = AiReplyParser.Parse(payload, 2);

        Assert.NotNull(verdicts);
        Assert.Equal(2, verdicts!.Count);
        Assert.True(verdicts[1].MachineTranslated);
        Assert.Equal(40, verdicts[1].Score);
        Assert.False(verdicts[2].MachineTranslated);
        Assert.Equal("口语自然", verdicts[2].Reason);
    }

    [Fact]
    public void ParsesContentPartArray()
    {
        const string payload = """
        {"choices":[{"message":{"content":[{"type":"text","text":"{\"candidates\":[{\"id\":1,\"score\":10,\"machine\":true,\"reason\":\"a\"},{\"id\":2,\"score\":20,\"machine\":false,\"reason\":\"b\"}]}"}]}}]}
        """;

        var verdicts = AiReplyParser.Parse(payload, 2);

        Assert.NotNull(verdicts);
        Assert.Equal(20, verdicts![2].Score);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"choices\":[]}")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"没有 json\"}}]}")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"{\\\"candidates\\\":[{\\\"id\\\":1,\\\"score\\\":10}]}\"}}]}")]
    public void ReturnsNullOnUnusableReply(string payload)
    {
        Assert.Null(AiReplyParser.Parse(payload, 2));
    }

    [Fact]
    public void RejectsDuplicateOrOutOfRangeIds()
    {
        var payload = Reply("{\"candidates\":[{\"id\":1,\"score\":10},{\"id\":1,\"score\":20}]}");

        Assert.Null(AiReplyParser.Parse(payload, 2));
    }

    [Fact]
    public void ReplyWithWrongCandidateCountIsRejected()
    {
        var payload = Reply("{\"candidates\":[{\"id\":1,\"score\":10}]}");

        Assert.Null(AiReplyParser.Parse(payload, 3));
    }

    [Fact]
    public void ClampsScores()
    {
        var payload = Reply("{\"candidates\":[{\"id\":1,\"score\":150},{\"id\":2,\"score\":-5}]}");

        var verdicts = AiReplyParser.Parse(payload, 2);

        Assert.Equal(100, verdicts![1].Score);
        Assert.Equal(0, verdicts[2].Score);
    }

    [Theory]
    [InlineData("human", false)]
    [InlineData("ai", true)]
    [InlineData("asr", true)]
    [InlineData("mixed", true)]
    [InlineData("unexpected", false)]
    public void SourceIsNormalisedIntoTheMachineFlag(string source, bool machine)
    {
        // 一份「mixed」（机翻后人工校对）仍然带机翻成分，排序时按机翻对待，
        // 否则人工校对过的机翻会被当成纯人工，挤掉真正的人翻。
        var payload = Reply(
            "{\"candidates\":[{\"id\":1,\"score\":80,\"source\":\"" + source + "\"},{\"id\":2,\"score\":70}]}");

        var verdicts = AiReplyParser.Parse(payload, 2);

        Assert.Equal(source == "unexpected" ? "human" : source, verdicts![1].EffectiveSource);
        Assert.Equal(machine, verdicts[1].MachineTranslated);
    }

    [Fact]
    public void LegacyReplyWithoutOptionalFieldsStillParses()
    {
        var payload = Reply(
            "{\"candidates\":[{\"id\":1,\"score\":66,\"machine\":true,\"reason\":\"直译\"},{\"id\":2,\"score\":77,\"machine\":false,\"reason\":\"自然\"}]}");

        var verdicts = AiReplyParser.Parse(payload, 2, Shortlist);

        Assert.Equal("ai", verdicts![1].EffectiveSource);
        Assert.Equal("human", verdicts[2].EffectiveSource);
        Assert.Equal(0, verdicts[1].Typos);
        Assert.Equal(string.Empty, verdicts[1].Evidence);
        Assert.False(verdicts[1].EvidenceVerified);
    }

    [Fact]
    public void TyposAreParsedForChineseMedia()
    {
        var payload = Reply(
            "{\"candidates\":[{\"id\":1,\"score\":55,\"typos\":7,\"reason\":\"「以经」应为「已经」\"},{\"id\":2,\"score\":93,\"typos\":0}]}");

        var verdicts = AiReplyParser.Parse(payload, 2);

        Assert.Equal(7, verdicts![1].Typos);
        Assert.Equal(0, verdicts[2].Typos);
    }

    [Fact]
    public void EvidenceIsVerifiedAgainstTheSampledLines()
    {
        var payload = Reply(
            "{\"candidates\":[{\"id\":1,\"score\":85,\"source\":\"human\",\"evidence\":\"我从来没像现在这样害怕过\"},"
            + "{\"id\":2,\"score\":70,\"source\":\"ai\",\"evidence\":\"他是不会明白的\"}]}");

        var verdicts = AiReplyParser.Parse(payload, 2, Shortlist);

        Assert.True(verdicts![1].EvidenceVerified);
        Assert.Equal("我从来没像现在这样害怕过", verdicts[1].Evidence);
        Assert.True(verdicts[2].EvidenceVerified);
    }

    [Fact]
    public void FabricatedEvidenceIsDroppedButTheScoreSurvives()
    {
        var payload = Reply(
            "{\"candidates\":[{\"id\":1,\"score\":85,\"evidence\":\"这句话根本不在字幕里\"},"
            + "{\"id\":2,\"score\":70,\"evidence\":\"我从来没像现在这样害怕过\"}]}");

        var verdicts = AiReplyParser.Parse(payload, 2, Shortlist);

        Assert.False(verdicts![1].EvidenceVerified);
        Assert.Equal(string.Empty, verdicts[1].Evidence);

        // 第二份引用了第一份的台词，交叉核对也不能通过。
        Assert.False(verdicts[2].EvidenceVerified);

        // 分数保留：证据缺失是可信度问题，不该让排序直接失效。
        Assert.Equal(85, verdicts[1].Score);
    }

    [Fact]
    public void EvidenceMatchIgnoresPunctuationAndWidth()
    {
        // 模型常把全角引号写成半角、自己补个句号；严格匹配会误伤。
        var payload = Reply(
            "{\"candidates\":[{\"id\":1,\"score\":85,\"evidence\":\"「我从来没像现在这样害怕过。」\"},"
            + "{\"id\":2,\"score\":70,\"evidence\":\"我从来没有像现在一样恐惧！\"}]}");

        var verdicts = AiReplyParser.Parse(payload, 2, Shortlist);

        Assert.True(verdicts![1].EvidenceVerified);
        Assert.True(verdicts[2].EvidenceVerified);
    }

    [Fact]
    public void EvidenceIsNotVerifiedWhenTheShortlistIsUnknown()
    {
        // 老调用方只传 expectedCount：没有原始文本可核对，就不该假装验证过。
        var payload = Reply(
            "{\"candidates\":[{\"id\":1,\"score\":85,\"evidence\":\"我从来没像现在这样害怕过\"},{\"id\":2,\"score\":70}]}");

        var verdicts = AiReplyParser.Parse(payload, 2);

        Assert.False(verdicts![1].EvidenceVerified);
        Assert.Equal(string.Empty, verdicts[1].Evidence);
    }
}
