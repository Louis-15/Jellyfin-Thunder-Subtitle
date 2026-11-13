using Jellyfin.Plugin.ThunderSubtitle.Core;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

/// <summary>
/// 影视库归属的默认值。
///
/// 这里的每一条都在守同一个规则：**只有显式配置成番号检索的库才是日本 AV**。
/// 「找不到库」、「库没保存过设置」、「路径对不上任何库」都不等于番号库。
/// </summary>
public class LibraryContextTests
{
    [Fact]
    public void ItemWithoutLibraryFallsBackToTitleSearch()
    {
        // ILibraryManager 传 null：这几条路径都不应该碰它。
        var context = new LibraryContext(null!);

        Assert.Equal(SubtitleSearchMode.Title, context.ModeForItem(null));
        Assert.Equal(MediaKind.Foreign, context.MediaKindForItem(null));
    }

    [Fact]
    public void EmptyPathFallsBackToTitleSearch()
    {
        var context = new LibraryContext(null!);

        Assert.Equal(SubtitleSearchMode.Title, context.ModeForPath(null));
        Assert.Equal(SubtitleSearchMode.Title, context.ModeForPath("   "));
        Assert.Equal(MediaKind.Foreign, context.MediaKindForPath(null));
    }

    [Fact]
    public void UnknownPathIsNeverTreatedAsAv()
    {
        // 路径对不上任何已配置的库：插件本来就不管它，不能假设是 AV。
        var context = new LibraryContext(null!);
        Assert.Equal(MediaKind.Foreign, context.MediaKindForPath(null));
    }
}
