using Jellyfin.Plugin.ThunderSubtitle.Core;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

/// <summary>配置项到 AI 设置的映射，以及配置值的边界收敛。</summary>
public class AiSettingsTests
{
    [Fact]
    public void SettingsComeFromConfiguration()
    {
        var configuration = new PluginConfiguration
        {
            AiProvider = PluginConfiguration.AiProviderCustom,
            AiBaseUrl = "https://api.example.test/v1",
            AiApiKey = "secret",
            AiModel = "m",
            AiAlignedSegments = 7,
            AiAlignedCues = 9,
            AiMaxCharsPerCandidate = 1500,
            AiTimeoutSeconds = 45
        };

        var settings = AiSettings.From(configuration, "sess");

        Assert.Equal("https://api.example.test/v1", settings.BaseUrl);
        Assert.Equal("secret", settings.ApiKey);
        Assert.Equal(7, settings.SegmentCount);
        Assert.Equal(9, settings.CuesPerSegment);
        Assert.Equal(1500, settings.MaxCharsPerCandidate);
        Assert.Equal(45, settings.TimeoutSeconds);
        Assert.Equal("sess", settings.SessionId);
        Assert.False(settings.UseSessionHeader);
    }

    [Fact]
    public void ChineseMediaGetsADenserSample()
    {
        // 错别字是稀疏事件：按 AV 的密度抽，很可能一条都碰不到。
        var settings = new AiSettings("https://x/v1", "k", "m", 6, 8, 3000, 30, CjkCuesPerSegment: 12, CjkMaxChars: 7000);

        Assert.Equal(8, settings.CuesFor(MediaKind.Foreign));
        Assert.Equal(8, settings.CuesFor(MediaKind.JapaneseAv));
        Assert.Equal(12, settings.CuesFor(MediaKind.Chinese));

        Assert.Equal(3000, settings.MaxCharsFor(MediaKind.Foreign));
        Assert.Equal(7000, settings.MaxCharsFor(MediaKind.Chinese));
    }

    [Fact]
    public void OpenCodeProviderUsesTheBuiltInEndpointAndSessionHeader()
    {
        var configuration = new PluginConfiguration
        {
            AiProvider = PluginConfiguration.AiProviderOpenCode,
            AiBaseUrl = "https://ignored.example/v1",
            AiModel = PluginConfiguration.OpenCodeDefaultModel
        };

        var settings = AiSettings.From(configuration, "sess");

        Assert.Equal(PluginConfiguration.OpenCodeBaseUrl, settings.BaseUrl);
        Assert.Equal("https://opencode.ai/inference/go/openai/v1", settings.BaseUrl);
        Assert.True(settings.UseSessionHeader);
    }

    [Fact]
    public void UnknownProviderFallsBackToOpenCode()
    {
        var configuration = new PluginConfiguration { AiProvider = "SomeFutureProvider" };

        configuration.Normalize();

        Assert.Equal(PluginConfiguration.AiProviderOpenCode, configuration.AiProvider);
        Assert.True(configuration.IsOpenCodeProvider);
    }

    [Fact]
    public void ConfigurationClampsAiValues()
    {
        var configuration = new PluginConfiguration
        {
            AiTopCandidates = 99,
            AiPerTier = 99,
            AiAlignedSegments = 1,
            AiAlignedCues = 1,
            AiCjkAlignedCues = 99,
            AiCjkMaxChars = 1,
            AiMaxPromptChars = 1,
            AiMaxCharsPerCandidate = 1,
            AiTimeoutSeconds = 1
        };

        configuration.Normalize();

        Assert.Equal(5, configuration.AiTopCandidates);
        Assert.Equal(3, configuration.AiAlignedSegments);
        Assert.Equal(4, configuration.AiAlignedCues);
        Assert.Equal(20, configuration.AiCjkAlignedCues);
        Assert.Equal(2000, configuration.AiCjkMaxChars);
        Assert.Equal(4000, configuration.AiMaxPromptChars);
        Assert.Equal(500, configuration.AiMaxCharsPerCandidate);
        Assert.Equal(10, configuration.AiTimeoutSeconds);
    }

    [Fact]
    public void AiIsUnusableUntilEndpointAndModelAreSet()
    {
        Assert.False(new PluginConfiguration().IsAiUsable);

        var custom = new PluginConfiguration
        {
            AiEnabled = true,
            AiProvider = PluginConfiguration.AiProviderCustom,
            AiBaseUrl = string.Empty,
            AiModel = "m"
        };
        Assert.False(custom.IsAiUsable);

        custom.AiBaseUrl = "https://x/v1";
        Assert.True(custom.IsAiUsable);

        // OpenCode needs no Base URL of its own: it is built in.
        var openCode = new PluginConfiguration
        {
            AiEnabled = true,
            AiProvider = PluginConfiguration.AiProviderOpenCode,
            AiBaseUrl = string.Empty,
            AiModel = PluginConfiguration.OpenCodeDefaultModel
        };
        Assert.True(openCode.IsAiUsable);
    }
}
