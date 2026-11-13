using System;
using System.Linq;
using Jellyfin.Plugin.ThunderSubtitle.Core;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

/// <summary>
/// 附加提示词的暂存行为。要保证三件事：能取回、不会串到别的影片、过期后绝不复用。
/// </summary>
public class HintStoreTests
{
    private static readonly Guid ItemA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ItemB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void StoredHintComesBack()
    {
        var store = new HintStore();
        store.Set(ItemA, "用词粗粝一点");

        Assert.Equal("用词粗粝一点", store.Take(ItemA));
    }

    [Fact]
    public void HintIsNotSharedBetweenItems()
    {
        var store = new HintStore();
        store.Set(ItemA, "给 A 的要求");

        Assert.Null(store.Take(ItemB));
        Assert.Equal("给 A 的要求", store.Take(ItemA));
    }

    [Fact]
    public void ReadingDoesNotConsumeTheHint()
    {
        // 用户可能连点几次搜索，或者先搜一次看看再改。
        var store = new HintStore();
        store.Set(ItemA, "x");

        Assert.Equal("x", store.Take(ItemA));
        Assert.Equal("x", store.Take(ItemA));
    }

    [Fact]
    public void EmptyHintClearsTheStoredOne()
    {
        var store = new HintStore();
        store.Set(ItemA, "先写点东西");
        store.Set(ItemA, "   ");

        Assert.Null(store.Take(ItemA));
    }

    [Fact]
    public void HintExpiresAfterTenMinutes()
    {
        var now = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        var store = new HintStore(() => now);

        store.Set(ItemA, "过期测试");
        now = now.AddMinutes(9);
        Assert.Equal("过期测试", store.Take(ItemA));

        now = now.AddMinutes(2);
        Assert.Null(store.Take(ItemA));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void CapacityDropsTheOldestEntries()
    {
        var now = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        var store = new HintStore(() => now);

        for (var i = 0; i < 40; i++)
        {
            now = now.AddSeconds(1);
            store.Set(Guid.NewGuid(), "第 " + i + " 条");
        }

        // 上限 32：最早的几条被丢掉，最近的还在。
        Assert.True(store.Count <= 32, $"存了 {store.Count} 条，超过上限");
    }

    [Fact]
    public void GuidFormatsResolveToTheSameEntry()
    {
        // 前端传的是带连字符的 D 格式，后端从 Guid 直接拿的是 N 格式。
        var store = new HintStore();
        store.Set(ItemA.ToString("D"), "格式测试");

        Assert.Equal("格式测试", store.Take(ItemA.ToString("N")));
        Assert.Equal("格式测试", store.Take(ItemA));
    }

    [Fact]
    public void LongHintIsTruncated()
    {
        var store = new HintStore();
        store.Set(ItemA, new string('长', 800));

        var stored = store.Take(ItemA);

        Assert.NotNull(stored);
        Assert.Equal(HintStore.MaxChars, stored!.Length);
    }

    [Fact]
    public void ControlCharactersAndRepeatedSpacesAreFolded()
    {
        var store = new HintStore();
        store.Set(ItemA, "  1970 年代\r\n的\t黑帮片   ");

        Assert.Equal("1970 年代 的 黑帮片", store.Take(ItemA));
    }

    [Fact]
    public void UnknownItemIdReturnsNull()
    {
        var store = new HintStore();

        Assert.Null(store.Take(Guid.Empty));
        Assert.Null(store.Take((string?)null));
    }
}
