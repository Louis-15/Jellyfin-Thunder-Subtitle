using Jellyfin.Plugin.ThunderSubtitle.Core;
using Jellyfin.Plugin.ThunderSubtitle.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ThunderSubtitle;

/// <summary>
/// Registers the plugin's services. Jellyfin resolves <see cref="MediaBrowser.Model.Tasks.IScheduledTask"/>
/// implementations from the container, so the tasks have to be added here.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        SubtitleText.RegisterEncodings();

        services.AddSingleton<ThunderClient>();
        services.AddSingleton<ProvenanceStore>();
        services.AddSingleton<AiSubtitleJudge>();
        services.AddSingleton<ISubtitleFinder, SubtitleFinder>();
        services.AddSingleton<SubtitleJob>();

        // Jellyfin picks this up through GetExports<ISubtitleProvider>, which fills the
        // per-item "修改字幕 -> 搜索字幕" dialog.
        services.AddSingleton<MediaBrowser.Controller.Subtitles.ISubtitleProvider, Providers.ThunderSubtitleProvider>();

        // Jellyfin instantiates every IScheduledTask implementation through DI at startup,
        // so registering the concrete types is enough for the tasks to show up.
        services.AddSingleton<ThunderFillTask>();
        services.AddSingleton<ThunderRepickTask>();
    }
}
