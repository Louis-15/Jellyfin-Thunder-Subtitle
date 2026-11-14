using System;
using System.IO;
using System.Text;
using Jellyfin.Plugin.ThunderSubtitle.Core;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

/// <summary>
/// 「我的字幕」卡片依赖台账里的 AI 评语 / 翻译方式 / 原字形。
/// 这些字段是后加的，必须保证旧 provenance.json 仍能读出来（硬约束 12）。
/// </summary>
public class ProvenanceStoreTests
{
    private static string TempPath()
    {
        return Path.Combine(Path.GetTempPath(), "ts-provenance-" + Guid.NewGuid().ToString("N") + ".json");
    }

    [Fact]
    public void NewCardFieldsSurviveARoundTrip()
    {
        var path = TempPath();
        try
        {
            new ProvenanceStore(path).Set(new SubtitleProvenance(
                "abc",
                "ABF-363",
                "/media/x.chi.srt",
                "x.srt",
                1234,
                0,
                DateTimeOffset.UtcNow,
                "JapaneseAv",
                "SimplifiedChinese",
                74,
                "ai",
                0,
                3,
                "整体口语尚可",
                true,
                true,
                "简繁混用"));

            var record = new ProvenanceStore(path).Get("abc");

            Assert.NotNull(record);
            Assert.Equal("整体口语尚可", record!.AiReason);
            Assert.True(record.AiMachineTranslated);
            Assert.True(record.AiAbsolute);
            Assert.Equal("简繁混用", record.ConvertedFrom);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OldEntriesWithoutTheNewFieldsLoadWithDefaults()
    {
        var path = TempPath();
        try
        {
            // 这就是 1.10.0.0 之前写出来的格式：只有前七个字段。
            File.WriteAllText(
                path,
                """{"abc":{"ItemId":"abc","Code":"ABF-363","File":"/media/x.chi.srt","CandidateName":"x.srt","Bytes":1,"AiMarkers":0,"WrittenAt":"2026-11-03T19:01:55.6927091+00:00"}}""",
                Encoding.UTF8);

            var record = new ProvenanceStore(path).Get("abc");

            Assert.NotNull(record);
            Assert.Equal(string.Empty, record!.AiReason);
            Assert.Null(record.AiMachineTranslated);
            Assert.False(record.AiAbsolute);
            Assert.Equal(string.Empty, record.ConvertedFrom);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
