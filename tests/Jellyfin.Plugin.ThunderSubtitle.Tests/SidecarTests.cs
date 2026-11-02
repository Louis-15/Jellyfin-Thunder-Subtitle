using Jellyfin.Plugin.ThunderSubtitle.Core;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

public class SidecarTests
{
    private const string Media = "/media/strm/日本大姐姐/ABF-363-U.strm";

    [Fact]
    public void BuildsTargetNextToTheStrm()
    {
        var directory = Path.GetDirectoryName(Media)!;
        Assert.Equal(Path.Combine(directory, "ABF-363-U.chi.srt"), Sidecar.TargetPath(Media, "srt"));
        Assert.Equal(Path.Combine(directory, "ABF-363-U.chi.ass"), Sidecar.TargetPath(Media, ".ass"));
    }

    [Theory]
    [InlineData("ABF-363-U.chi.srt", true)]
    [InlineData("ABF-363-U.chi.ass", true)]
    [InlineData("ABF-363-U.chi.0.srt", false)]
    [InlineData("ABF-363-U.srt", false)]
    [InlineData("ABF-363-U.zh.srt", false)]
    [InlineData("ABF-363-U.chi.srt.bak", false)]
    [InlineData("ABF-363-U.en.chi.srt", false)]
    public void RecognisesOnlyPluginOwnedSidecars(string name, bool expected)
    {
        var path = Path.Combine(Path.GetDirectoryName(Media)!, name);
        Assert.Equal(expected, Sidecar.IsPluginSubtitle(Media, path));
    }

    [Fact]
    public void FindsSubtitlesAndSeparatesPluginFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "thunder-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var media = Path.Combine(directory, "ABF-363-U.strm");
            File.WriteAllText(media, "http://example.com/video.mp4");
            File.WriteAllText(Path.Combine(directory, "ABF-363-U.srt"), "user");
            File.WriteAllText(Path.Combine(directory, "ABF-363-U.chi.ass"), "plugin");
            File.WriteAllText(Path.Combine(directory, "ABF-363-U.chi.0.srt"), "conflict copy");
            File.WriteAllText(Path.Combine(directory, "ABF-363-U.nfo"), "ignored");

            var all = Sidecar.FindSubtitles(media);
            Assert.Equal(3, all.Count);

            var plugin = Sidecar.FindPluginSubtitles(media);
            Assert.Single(plugin);
            Assert.EndsWith("ABF-363-U.chi.ass", plugin[0]);

            var removed = Sidecar.RemoveStalePluginSubtitles(media, Path.Combine(directory, "ABF-363-U.chi.srt"));
            Assert.Single(removed);
            Assert.False(File.Exists(Path.Combine(directory, "ABF-363-U.chi.ass")));
            Assert.True(File.Exists(Path.Combine(directory, "ABF-363-U.srt")));
            Assert.True(File.Exists(Path.Combine(directory, "ABF-363-U.chi.0.srt")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
