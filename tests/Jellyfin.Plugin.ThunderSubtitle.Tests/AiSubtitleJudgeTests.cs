using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.ThunderSubtitle.Core;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

public class AiSubtitleJudgeTests
{
    private const string SrtA =
        "1\n00:00:01,000 --> 00:00:03,000\n看 这是今年的新人\n\n" +
        "2\n00:00:20,000 --> 00:00:23,000\n从美国回来的\n\n" +
        "3\n00:01:40,000 --> 00:01:43,000\n那么没头没脑的新人\n\n";

    private static SubtitlePick Pick(string name, string text)
    {
        return new SubtitlePick(name, ".srt", text, "utf-8");
    }

    private static AiSettings Settings(int samples = 24)
    {
        return new AiSettings("https://example.test/v1", "key", "test-model", samples, 3000, 30);
    }

    [Fact]
    public void RequestCarriesBothCandidatesAndCode()
    {
        var request = AiSubtitleJudge.BuildRequest("FSDSS-272", [Pick("a.srt", SrtA), Pick("b.srt", SrtA)], Settings());

        Assert.Equal("test-model", request.Model);
        Assert.Equal(0, request.Temperature);
        Assert.Equal(2, request.Messages.Count);
        Assert.Equal("system", request.Messages[0].Role);

        var user = request.Messages[1].Content;
        Assert.Contains("FSDSS-272", user);
        Assert.Contains("候选 1", user);
        Assert.Contains("候选 2", user);
        Assert.Contains("看 这是今年的新人", user);
    }

    [Fact]
    public void RequestSamplesAcrossTheWholeFile()
    {
        var lines = new List<string>();
        for (var i = 0; i < 200; i++)
        {
            lines.Add($"{i + 1}\n00:{i / 60:00}:{i % 60:00},000 --> 00:{i / 60:00}:{i % 60 + 1:00},000\n第{i}句台词 уникальный{i}\n");
        }

        var longText = string.Join('\n', lines);
        var request = AiSubtitleJudge.BuildRequest("X-1", [Pick("a.srt", longText), Pick("b.srt", SrtA)], Settings(6));

        var user = request.Messages[1].Content;
        Assert.Contains("第0句台词", user);
        Assert.Contains("第199句台词", user);

        // Sampling must stay cheap: 6 cues, not 200.
        Assert.DoesNotContain("第100句台词", user);
    }

    [Fact]
    public void SampleRespectsCharacterCap()
    {
        var big = SrtA.Replace("看 这是今年的新人", new string('长', 5000));
        var request = AiSubtitleJudge.BuildRequest("X-1", [Pick("a.srt", big), Pick("b.srt", SrtA)], Settings());
        var user = request.Messages[1].Content;

        Assert.True(user.Length < 8000, $"prompt was {user.Length} chars");
    }

    [Fact]
    public void ParsesOpenAiStyleReply()
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

        var verdicts = AiSubtitleJudge.ParseVerdicts(payload, 2);

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

        var verdicts = AiSubtitleJudge.ParseVerdicts(payload, 2);

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
        Assert.Null(AiSubtitleJudge.ParseVerdicts(payload, 2));
    }

    [Fact]
    public void RejectsDuplicateOrOutOfRangeIds()
    {
        const string payload = """
        {"choices":[{"message":{"content":"{\"candidates\":[{\"id\":1,\"score\":10,\"machine\":false},{\"id\":1,\"score\":20,\"machine\":false}]}"}}]}
        """;

        Assert.Null(AiSubtitleJudge.ParseVerdicts(payload, 2));
    }

    [Fact]
    public void ReplyWithWrongCandidateCountIsRejected()
    {
        const string payload = """
        {"choices":[{"message":{"content":"{\"candidates\":[{\"id\":1,\"score\":10,\"machine\":false}]}"}}]}
        """;

        Assert.Null(AiSubtitleJudge.ParseVerdicts(payload, 3));
    }

    [Fact]
    public void ClampsScores()
    {
        const string payload = """
        {"choices":[{"message":{"content":"{\"candidates\":[{\"id\":1,\"score\":150,\"machine\":false},{\"id\":2,\"score\":-5,\"machine\":true}]}"}}]}
        """;

        var verdicts = AiSubtitleJudge.ParseVerdicts(payload, 2);

        Assert.Equal(100, verdicts![1].Score);
        Assert.Equal(0, verdicts[2].Score);
    }

    [Fact]
    public void SettingsComeFromConfiguration()
    {
        var configuration = new PluginConfiguration
        {
            AiBaseUrl = "https://api.example.test/v1",
            AiApiKey = "secret",
            AiModel = "m",
            AiSampleCues = 12,
            AiMaxCharsPerCandidate = 1500,
            AiTimeoutSeconds = 45
        };

        var settings = AiSettings.From(configuration);

        Assert.Equal("https://api.example.test/v1", settings.BaseUrl);
        Assert.Equal("secret", settings.ApiKey);
        Assert.Equal(12, settings.SampleCues);
        Assert.Equal(45, settings.TimeoutSeconds);
    }

    [Fact]
    public void ConfigurationClampsAiValues()
    {
        var configuration = new PluginConfiguration
        {
            AiTopCandidates = 99,
            AiSampleCues = 1,
            AiMaxCharsPerCandidate = 1,
            AiTimeoutSeconds = 1
        };

        configuration.Normalize();

        Assert.Equal(5, configuration.AiTopCandidates);
        Assert.Equal(4, configuration.AiSampleCues);
        Assert.Equal(500, configuration.AiMaxCharsPerCandidate);
        Assert.Equal(10, configuration.AiTimeoutSeconds);
    }

    [Fact]
    public void AiIsUnusableUntilEndpointAndModelAreSet()
    {
        Assert.False(new PluginConfiguration().IsAiUsable);
        Assert.False(new PluginConfiguration { AiEnabled = true, AiModel = string.Empty }.IsAiUsable);
        Assert.True(new PluginConfiguration { AiEnabled = true, AiBaseUrl = "https://x/v1", AiModel = "m" }.IsAiUsable);
    }
}
