using System;
using Jellyfin.Plugin.ThunderSubtitle.Core;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

/// <summary>
/// 「测试连接」按钮和正式 AI 评审共用同一套端点补全规则。
///
/// 两边规则不一致会产生最难排查的一类问题：配置页点「测试连接」显示成功，
/// 真跑任务时却一直连不上（或者反过来）。所以把规则本身固定下来。
/// </summary>
public class AiEndpointTests
{
    [Theory]
    [InlineData("https://opencode.ai/inference/go/openai/v1", "https://opencode.ai/inference/go/openai/v1/chat/completions")]
    [InlineData("https://host/v1/", "https://host/v1/chat/completions")]
    [InlineData("http://192.168.1.10:11434/v1", "http://192.168.1.10:11434/v1/chat/completions")]
    public void AppendsTheCompletionPath(string configured, string expected)
    {
        Assert.True(AiSubtitleJudge.TryBuildEndpoint(configured, out var endpoint));
        Assert.Equal(expected, endpoint.ToString());
    }

    [Fact]
    public void KeepsAFullEndpointUnchanged()
    {
        // 用户直接粘完整地址时不能再补一次，否则会变成 .../chat/completions/chat/completions。
        Assert.True(AiSubtitleJudge.TryBuildEndpoint("https://host/v1/chat/completions", out var endpoint));
        Assert.Equal("https://host/v1/chat/completions", endpoint.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("localhost:11434")]
    [InlineData("ftp://host/v1")]
    [InlineData("file:///tmp/v1")]
    public void RejectsWhatIsNotAnHttpEndpoint(string configured)
    {
        Assert.False(AiSubtitleJudge.TryBuildEndpoint(configured, out _));
    }
}
