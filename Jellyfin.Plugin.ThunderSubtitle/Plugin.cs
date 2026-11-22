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
        MigrateAiCredentials();
    }

    /// <summary>
    /// 把 1.11 及更早版本的单一 AI 凭据迁移到「当前供应商」的独立槽位。
    ///
    /// 旧版只有一组 <c>AiApiKey</c> / <c>AiModel</c>；1.12 起每个供应商各存一份。
    /// 必须在服务端加载时完成并写回：配置页读的是新槽位，老配置的新槽位是空的，
    /// 用户升级后打开页面点一次「保存」就会把旧字段清空 —— 密钥就永久丢了。
    /// </summary>
    private void MigrateAiCredentials()
    {
        if (string.IsNullOrWhiteSpace(Configuration.AiApiKey)
            && string.IsNullOrWhiteSpace(Configuration.AiModel))
        {
            return;
        }

        try
        {
            // Normalize 内部会调用 MigrateLegacyCredentials，把旧值搬进槽位并清空旧字段。
            Configuration.Normalize();
            UpdateConfiguration(Configuration);
        }
        catch (Exception)
        {
            // 迁移失败不该阻止插件加载：运行期每条链路还会再 Normalize 一次，
            // 最坏情况是用户到配置页重新填一次 Key。
        }
    }

    /// <summary>Gets the current plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Thunder Subtitle";

    /// <inheritdoc />
    public override string Description =>
        "自动为影视库检索中文外挂字幕：按影片类型分化语言策略，可选大模型横向评审择优。"
        + "每个影视库可分别开关「自动补全」与「自动择优」，网盘同步的字幕和内嵌字幕永远不会被覆盖；"
        + "也支持手动搜索，以三行卡片查看 AI 评分与评语、预览全文、就地繁转简后再下载。";

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
