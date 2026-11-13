using System;
using System.IO;
using Jellyfin.Plugin.ThunderSubtitle.Core;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

/// <summary>
/// AI 评分缓存：自动补全和自动择优会对同一批候选发两次一模一样的请求，
/// 一次 6 份约 7000 token，重复计费没有意义。
/// </summary>
public class AiScoreCacheTests : IDisposable
{
    private const string Srt =
        "1\n00:00:01,000 --> 00:00:03,000\n我从来没像现在这样害怕过\n\n" +
        "2\n00:00:20,000 --> 00:00:23,000\n他不会明白的\n\n";

    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        "thunder-ai-cache-" + Guid.NewGuid().ToString("N") + ".json");

    private static SubtitlePick Pick => new("a.srt", ".srt", Srt, "utf-8");

    private static AiVerdict Verdict => new(1, 88, false, "口语自然", "human", 0, "我从来没像现在这样害怕过", true);

    public void Dispose()
    {
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void StoredScoreComesBack()
    {
        var cache = new AiScoreCache(_path);
        var key = AiScoreCache.Key(Pick, MediaKind.Foreign, null, "model-a");

        cache.Set(key, Verdict);
        var restored = cache.Get(key, 30);

        Assert.NotNull(restored);
        Assert.Equal(88, restored!.Score);
        Assert.Equal("human", restored.EffectiveSource);
        Assert.True(restored.EvidenceVerified);

        // Id 是「本次送审的第几份」，缓存不保留它。
        Assert.Equal(0, restored.Id);
    }

    [Fact]
    public void DifferentModelMisses()
    {
        // 不同模型的分数不可比，换模型必须重新评。
        var cache = new AiScoreCache(_path);
        cache.Set(AiScoreCache.Key(Pick, MediaKind.Foreign, null, "model-a"), Verdict);

        Assert.Null(cache.Get(AiScoreCache.Key(Pick, MediaKind.Foreign, null, "model-b"), 30));
    }

    [Fact]
    public void DifferentMediaKindMisses()
    {
        // 同一份字幕，按 AV 的标准和按中文影视的标准评出来的分不是一回事。
        var cache = new AiScoreCache(_path);
        cache.Set(AiScoreCache.Key(Pick, MediaKind.JapaneseAv, null, "m"), Verdict);

        Assert.Null(cache.Get(AiScoreCache.Key(Pick, MediaKind.Chinese, null, "m"), 30));
    }

    [Fact]
    public void DifferentHintMisses()
    {
        // 带「用词粗粝一点」评出来的分数，不能用来给默认尺度下的搜索排序。
        var cache = new AiScoreCache(_path);
        cache.Set(AiScoreCache.Key(Pick, MediaKind.Foreign, null, "m"), Verdict);

        Assert.Null(cache.Get(AiScoreCache.Key(Pick, MediaKind.Foreign, "用词粗粝一点", "m"), 30));
        Assert.NotNull(cache.Get(AiScoreCache.Key(Pick, MediaKind.Foreign, "  ", "m"), 30));
    }

    [Fact]
    public void ExpiredEntryIsIgnored()
    {
        var now = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        var cache = new AiScoreCache(_path, () => now);
        var key = AiScoreCache.Key(Pick, MediaKind.Foreign, null, "m");

        cache.Set(key, Verdict);
        now = now.AddDays(29);
        Assert.NotNull(cache.Get(key, 30));

        now = now.AddDays(2);
        Assert.Null(cache.Get(key, 30));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void CacheSurvivesAProcessRestart()
    {
        var key = AiScoreCache.Key(Pick, MediaKind.Foreign, null, "m");
        new AiScoreCache(_path).Set(key, Verdict);

        // 新实例从磁盘读，模拟重启。
        Assert.NotNull(new AiScoreCache(_path).Get(key, 30));
    }

    [Fact]
    public void CapacityDropsTheOldestEntries()
    {
        var now = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        var cache = new AiScoreCache(_path, () => now);

        for (var i = 0; i < AiScoreCache.MaxEntries + 200; i++)
        {
            now = now.AddSeconds(1);
            cache.Set(i.ToString("X64"), Verdict);
        }

        Assert.True(cache.Count <= AiScoreCache.MaxEntries, $"存了 {cache.Count} 条，超过上限");
    }
}
