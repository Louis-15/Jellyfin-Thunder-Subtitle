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
    public void UnknownProviderFallsBackToCustom()
    {
        // 手改 XML 写成未知值时按「自定义」处理：地址必须由用户填，
        // 一运行就会明确报「Base URL 不合法」，而不是带着内置地址和会话头
        // 请求到错误的供应商、只看到一个难查的 401。
        var configuration = new PluginConfiguration { AiProvider = "SomeFutureProvider" };

        configuration.Normalize();

        Assert.Equal(PluginConfiguration.AiProviderCustom, configuration.AiProvider);
        Assert.True(configuration.IsCustomProvider);
        Assert.Equal(string.Empty, configuration.ApiRoot);
    }

    [Fact]
    public void CommandCodeProviderUsesTheBuiltInEndpointWithoutSessionHeader()
    {
        var configuration = new PluginConfiguration
        {
            AiProvider = PluginConfiguration.AiProviderCommandCode,
            AiOpenCodeApiKey = "open-secret",
            AiCommandCodeApiKey = "cmd-secret"
        };

        configuration.Normalize();
        var settings = AiSettings.From(configuration, "sess");

        Assert.Equal("https://api.commandcode.ai/provider/v1", settings.BaseUrl);
        Assert.Equal(PluginConfiguration.CommandCodeBaseUrl, settings.BaseUrl);

        // 切到 Command Code 必须用它的 Key，不能串到 OpenCode 那把。
        Assert.Equal("cmd-secret", settings.ApiKey);

        // Command Code 是标准 OpenAI 兼容接口，带了未知请求头反而可能被 400。
        Assert.False(settings.UseSessionHeader);

        // 它的模型名带厂商前缀，不能沿用 OpenCode 的 deepseek-v4.1-flash。
        Assert.Equal(PluginConfiguration.CommandCodeDefaultModel, settings.Model);
        Assert.Contains("/", settings.Model);
    }

    [Fact]
    public void EachProviderKeepsItsOwnApiKeyAndModel()
    {
        // 用户三个供应商都配过：切换下拉框后必须各读各的，不能串。
        var configuration = new PluginConfiguration
        {
            AiProvider = PluginConfiguration.AiProviderOpenCode,
            AiOpenCodeApiKey = "open-key",
            AiOpenCodeModel = "open-model",
            AiCommandCodeApiKey = "cmd-key",
            AiCommandCodeModel = "cmd-model",
            AiCustomApiKey = "custom-key",
            AiCustomModel = "custom-model"
        };

        Assert.Equal("open-key", configuration.ResolveApiKey());
        Assert.Equal("open-model", configuration.ResolveModel());

        configuration.AiProvider = PluginConfiguration.AiProviderCommandCode;
        Assert.Equal("cmd-key", configuration.ResolveApiKey());
        Assert.Equal("cmd-model", configuration.ResolveModel());
        Assert.Equal(PluginConfiguration.CommandCodeBaseUrl, configuration.ApiRoot);

        configuration.AiProvider = PluginConfiguration.AiProviderCustom;
        Assert.Equal("custom-key", configuration.ResolveApiKey());
        Assert.Equal("custom-model", configuration.ResolveModel());
    }

    [Fact]
    public void LegacyCredentialsMigrateToTheSelectedProvider()
    {
        // 1.11 及更早只有一份 AiApiKey / AiModel。升级后必须搬进当时选中的供应商，
        // 否则用户打开配置页会看到空 Key，一保存就把密钥清掉了。
        var configuration = new PluginConfiguration
        {
            AiProvider = PluginConfiguration.AiProviderCommandCode,
            AiApiKey = "legacy-key",
            AiModel = "legacy-model"
        };

        configuration.Normalize();

        Assert.Equal("legacy-key", configuration.AiCommandCodeApiKey);
        Assert.Equal("legacy-model", configuration.AiCommandCodeModel);
        Assert.Equal(string.Empty, configuration.AiApiKey);
        Assert.Equal(string.Empty, configuration.AiModel);

        // 切到别的供应商时不会把 Command Code 的凭据带过去。
        configuration.AiProvider = PluginConfiguration.AiProviderOpenCode;
        Assert.Equal(string.Empty, configuration.AiOpenCodeApiKey);
        Assert.Equal(PluginConfiguration.OpenCodeDefaultModel, configuration.ResolveModel());
    }

    [Fact]
    public void CustomProviderHasNoInventedDefaultModel()
    {
        // 自定义网关的模型名无法预知，预填一个 OpenCode 的模型只会让人更困惑。
        var configuration = new PluginConfiguration { AiProvider = PluginConfiguration.AiProviderCustom };

        configuration.Normalize();

        Assert.Equal(string.Empty, configuration.ResolveModel());
        Assert.False(configuration.IsAiUsable);
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
