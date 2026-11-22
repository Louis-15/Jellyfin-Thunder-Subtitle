using Jellyfin.Plugin.ThunderSubtitle.Api;
using Jellyfin.Plugin.ThunderSubtitle.Core;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

/// <summary>
/// 「测试连接」必须使用前端表单值，而不是已保存配置。
///
/// 这一层出错的表现很难查：用户在页面里切了供应商、点了测试，结果测的是上一个供应商，
/// 还显示「连接成功」—— 用户根本不知道请求打到了哪里。
/// </summary>
public class AiTestRequestTests
{
    [Fact]
    public void RequestValuesWinOverSavedConfiguration()
    {
        var saved = new PluginConfiguration
        {
            AiProvider = PluginConfiguration.AiProviderOpenCode,
            AiOpenCodeApiKey = "saved-open-key",
            AiOpenCodeModel = "saved-open-model",
            AiTimeoutSeconds = 300
        };

        var request = new AiTestRequest
        {
            Provider = PluginConfiguration.AiProviderCommandCode,
            CommandCodeApiKey = "cmd-key",
            CommandCodeModel = "cmd-model"
        };

        var settings = AiTestController.BuildSettings(saved, request, "sess");

        Assert.Equal(PluginConfiguration.CommandCodeBaseUrl, settings.BaseUrl);
        Assert.Equal("cmd-key", settings.ApiKey);
        Assert.Equal("cmd-model", settings.Model);

        // Command Code 是标准 OpenAI 兼容接口，不能误带 OpenCode 的会话头。
        Assert.False(settings.UseSessionHeader);
    }

    [Fact]
    public void NullRequestFallsBackToSavedConfiguration()
    {
        var saved = new PluginConfiguration
        {
            AiProvider = PluginConfiguration.AiProviderOpenCode,
            AiOpenCodeApiKey = "saved-key",
            AiOpenCodeModel = "saved-model"
        };
        saved.Normalize();

        var settings = AiTestController.BuildSettings(saved, null, "sess");

        Assert.Equal(PluginConfiguration.OpenCodeBaseUrl, settings.BaseUrl);
        Assert.Equal("saved-key", settings.ApiKey);
        Assert.Equal("saved-model", settings.Model);
        Assert.True(settings.UseSessionHeader);
    }

    [Fact]
    public void ProbeUsesCustomAddressFromTheForm()
    {
        var saved = new PluginConfiguration
        {
            AiProvider = PluginConfiguration.AiProviderOpenCode,
            AiOpenCodeApiKey = "saved-key"
        };

        var request = new AiTestRequest
        {
            Provider = PluginConfiguration.AiProviderCustom,
            BaseUrl = " http://127.0.0.1:11434/v1 ",
            CustomApiKey = "custom-key",
            CustomModel = "qwen2.5:14b"
        };

        var settings = AiTestController.BuildSettings(saved, request, "sess");

        Assert.Equal("http://127.0.0.1:11434/v1", settings.BaseUrl);
        Assert.Equal("custom-key", settings.ApiKey);
        Assert.Equal("qwen2.5:14b", settings.Model);
        Assert.False(settings.UseSessionHeader);
    }

    [Fact]
    public void ProbeTimesOutQuickly()
    {
        var saved = new PluginConfiguration { AiTimeoutSeconds = 300 };
        var request = new AiTestRequest
        {
            Provider = PluginConfiguration.AiProviderOpenCode,
            TimeoutSeconds = 300
        };

        var settings = AiTestController.BuildSettings(saved, request, "sess");

        // 测试只是一条 ping，不该跟着日常评审的 300 秒一起转圈。
        Assert.InRange(settings.TimeoutSeconds, 10, 45);
    }
}
