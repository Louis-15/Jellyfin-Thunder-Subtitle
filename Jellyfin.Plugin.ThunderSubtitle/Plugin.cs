using System;
using System.Globalization;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ThunderSubtitle;

/// <summary>
/// Xunlei (迅雷) subtitle plugin: finds code-named movies without subtitles and writes
/// <c>&lt;name&gt;.chi.srt</c>/<c>.ass</c> next to the media file.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>The plugin id, also used by <c>meta.json</c>.</summary>
    public static readonly Guid PluginId = Guid.Parse("e4b2d7a1-9c3f-4f86-8d15-2a7b9e6c1f03");

    /// <summary>Initializes a new instance of the <see cref="Plugin"/> class.</summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">Serializer for the configuration file.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets the current plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Thunder Subtitle";

    /// <inheritdoc />
    public override string Description =>
        "自动为影视库补中文外挂字幕：普通影视按片名搜索，按番号命名的影片按番号搜索。"
        + "每个影视库可分别开关「自动补全」与「自动择优」，网盘同步的字幕和内嵌字幕永远不会被覆盖。";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <summary>
    /// Gets the configuration to use right now. Scheduled tasks may run while the
    /// plugin instance is not available yet, so a default instance is returned then.
    /// </summary>
    public static PluginConfiguration Current => Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = "thundersubtitle",
                EmbeddedResourcePath = GetType().Namespace + ".Web.configPage.html"
            }
        ];
    }

    /// <summary>
    /// Formats a timestamp the way the server-side scripts log it (Asia/Shanghai),
    /// so lines in the Jellyfin log can be lined up with the other subtitle tools.
    /// </summary>
    /// <param name="value">The timestamp to format.</param>
    /// <returns>The formatted timestamp.</returns>
    internal static string FormatBeijingTime(DateTimeOffset value)
    {
        return TimeZoneInfo.ConvertTime(value, BeijingTimeZone)
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static readonly TimeZoneInfo BeijingTimeZone = ResolveBeijingTimeZone();

    private static TimeZoneInfo ResolveBeijingTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.CreateCustomTimeZone("CST+8", TimeSpan.FromHours(8), "CST+8", "CST+8");
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.CreateCustomTimeZone("CST+8", TimeSpan.FromHours(8), "CST+8", "CST+8");
        }
    }
}
