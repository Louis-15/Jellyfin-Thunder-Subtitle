using System;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using Jellyfin.Plugin.ThunderSubtitle.Core;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

public class LibrarySettingTests
{
    private static readonly Guid AvLibrary = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid MovieLibrary = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void OldConfigurationDefaultsToCodeSearchAndAutomationOn()
    {
        // 升级前的配置只有 LibraryIds，没有 LibrarySettings。
        var config = new PluginConfiguration { LibraryIds = [AvLibrary.ToString("N")] };
        config.Normalize();

        Assert.Equal(SubtitleSearchMode.Code, config.ResolveSearchMode(AvLibrary));
        Assert.True(config.IsAutoFillEnabled(AvLibrary));
        Assert.True(config.IsAutoRepickEnabled(AvLibrary));
    }

    [Fact]
    public void PerLibrarySettingsAreResolved()
    {
        var config = new PluginConfiguration
        {
            LibraryIds = [AvLibrary.ToString("N"), MovieLibrary.ToString("N")],
            LibrarySettings =
            [
                new LibrarySetting
                {
                    LibraryId = AvLibrary.ToString("N"),
                    Mode = PluginConfiguration.SearchModeCode,
                    AutoFill = true,
                    AutoRepick = false
                },
                new LibrarySetting
                {
                    LibraryId = MovieLibrary.ToString("N"),
                    Mode = PluginConfiguration.SearchModeTitle,
                    AutoFill = false,
                    AutoRepick = true
                }
            ]
        };
        config.Normalize();

        Assert.Equal(SubtitleSearchMode.Code, config.ResolveSearchMode(AvLibrary));
        Assert.True(config.IsAutoFillEnabled(AvLibrary));
        Assert.False(config.IsAutoRepickEnabled(AvLibrary));

        Assert.Equal(SubtitleSearchMode.Title, config.ResolveSearchMode(MovieLibrary));
        Assert.False(config.IsAutoFillEnabled(MovieLibrary));

        // 自动补全关掉时择优也不应该跑：不自动下载就无所谓替换。
        Assert.False(config.IsAutoRepickEnabled(MovieLibrary));
    }

    [Fact]
    public void NormalizeDropsInvalidEntriesAndUnknownModes()
    {
        var config = new PluginConfiguration
        {
            LibrarySettings =
            [
                new LibrarySetting { LibraryId = "not-a-guid", Mode = PluginConfiguration.SearchModeTitle },
                new LibrarySetting { LibraryId = MovieLibrary.ToString("N"), Mode = "SomethingElse" }
            ]
        };
        config.Normalize();

        var entry = Assert.Single(config.LibrarySettings);
        Assert.Equal(MovieLibrary.ToString("N"), entry.LibraryId);
        Assert.Equal(PluginConfiguration.SearchModeCode, entry.Mode);
        Assert.True(entry.AutoFill);
        Assert.True(entry.AutoRepick);
    }

    [Fact]
    public void LibrarySettingsSurviveNullFromAnOldConfigFile()
    {
        var config = new PluginConfiguration { LibrarySettings = null! };
        config.Normalize();

        Assert.Empty(config.LibrarySettings);
    }

    [Fact]
    public void LibrarySettingsSurviveXmlRoundTrip()
    {
        // Jellyfin 用 XmlSerializer 存插件配置：数组里放自定义类必须真的能往返，
        // 否则界面上选好的设置重启后就丢了。
        var config = new PluginConfiguration
        {
            LibraryIds = [MovieLibrary.ToString("N")],
            LibrarySettings =
            [
                new LibrarySetting
                {
                    LibraryId = MovieLibrary.ToString("N"),
                    Mode = PluginConfiguration.SearchModeTitle,
                    AutoFill = true,
                    AutoRepick = false
                }
            ]
        };

        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();
        serializer.Serialize(writer, config);

        using var reader = new StringReader(writer.ToString());
        var restored = Assert.IsType<PluginConfiguration>(serializer.Deserialize(reader));
        restored.Normalize();

        var entry = Assert.Single(restored.LibrarySettings);
        Assert.Equal(MovieLibrary.ToString("N"), entry.LibraryId);
        Assert.True(entry.AutoFill);
        Assert.False(entry.AutoRepick);
        Assert.Equal(SubtitleSearchMode.Title, restored.ResolveSearchMode(MovieLibrary));
    }
}
