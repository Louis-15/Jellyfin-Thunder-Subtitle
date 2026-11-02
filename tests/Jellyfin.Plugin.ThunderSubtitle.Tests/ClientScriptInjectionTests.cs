using Jellyfin.Plugin.ThunderSubtitle.Core;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

/// <summary>
/// The preview button only exists because the web shell is rewritten on its way out. These are
/// the pure parts of that rewrite: it must be idempotent, preserve a base path, and leave
/// anything that is not the shell alone.
/// </summary>
public class ClientScriptInjectionTests
{
    private const string Shell = "<html><body><div id=\"reactRoot\"></div></body></html>";

    [Theory]
    [InlineData("/web/index.html", true)]
    [InlineData("/web/", true)]
    [InlineData("/web", true)]
    [InlineData("/jellyfin/web/index.html", true)]
    [InlineData("/web/index.html?x=1", false)]
    [InlineData("/web/main.js", false)]
    [InlineData("/Items/abc", false)]
    [InlineData("", false)]
    public void OnlyTheWebShellIsRewritten(string path, bool expected)
    {
        Assert.Equal(expected, ClientScriptInjectionFilter.IsIndexRequest(path));
    }

    [Theory]
    [InlineData("/web/index.html", "")]
    [InlineData("/web/", "")]
    [InlineData("/jellyfin/web/", "/jellyfin")]
    [InlineData("/a/b/web/index.html", "/a/b")]
    public void BasePathSurvivesTheRewrite(string path, string expected)
    {
        Assert.Equal(expected, ClientScriptInjectionFilter.ResolveBasePath(path));
    }

    [Fact]
    public void ScriptTagLandsBeforeTheClosingBody()
    {
        var html = ClientScriptInjectionFilter.Rewrite(Shell, string.Empty);

        Assert.Contains(ClientScriptInjectionFilter.Marker, html);
        Assert.Contains("/ThunderSubtitle/Client.js", html);
        Assert.True(html.IndexOf("</body>", System.StringComparison.Ordinal) > html.IndexOf("Client.js", System.StringComparison.Ordinal));
    }

    [Fact]
    public void ScriptTagUsesTheServerBasePath()
    {
        var html = ClientScriptInjectionFilter.Rewrite(Shell, "/jellyfin");

        Assert.Contains("src=\"/jellyfin/ThunderSubtitle/Client.js\"", html);
    }

    [Fact]
    public void RewriteIsIdempotent()
    {
        var once = ClientScriptInjectionFilter.Rewrite(Shell, string.Empty);
        var twice = ClientScriptInjectionFilter.Rewrite(once, string.Empty);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void DocumentWithoutABodyIsLeftAlone()
    {
        const string fragment = "<div>no shell here</div>";

        Assert.Equal(fragment, ClientScriptInjectionFilter.Rewrite(fragment, string.Empty));
    }
}
