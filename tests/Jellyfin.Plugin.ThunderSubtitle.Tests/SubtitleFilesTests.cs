using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ThunderSubtitle.Core;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

/// <summary>
/// 找出影片旁边已有的中文字幕文件。
///
/// 这个判定同时被「自动补全」和「自动择优」用到：补全靠它避开已有字幕，
/// 择优靠它知道该跟谁比。判宽了会漏掉真正该补的影片，判窄了会把英文外挂字幕
/// 当成「已有中文字幕」，用户打开就是一行英文字母。
/// </summary>
public class SubtitleFilesTests
{
    [Fact]
    public void FindsEveryChineseVariant()
    {
        var dir = Path.Combine(Path.GetTempPath(), "thunder-subs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var media = Path.Combine(dir, "ABF-363.strm");
            File.WriteAllText(media, "http://example.com/x.mp4");

            var expected = new[]
            {
                Path.Combine(dir, "ABF-363.chi.srt"),
                Path.Combine(dir, "ABF-363.chi.ass"),
                Path.Combine(dir, "ABF-363.chi.0.srt"),
                Path.Combine(dir, "ABF-363.zh-CN.vtt")
            };
            foreach (var path in expected)
            {
                File.WriteAllText(path, "x");
            }

            // 英文、别的影片、以及「china」这种只是前缀相同的名字，都不算。
            foreach (var other in new[]
            {
                Path.Combine(dir, "ABF-363.en.srt"),
                Path.Combine(dir, "ABF-363.china.srt"),
                Path.Combine(dir, "OTHER-001.chi.srt")
            })
            {
                File.WriteAllText(other, "x");
            }

            var found = SubtitleFiles.FindChinese(media);

            Assert.Equal(expected.Length, found.Count);
            Assert.All(expected, path => Assert.Contains(path, found));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
