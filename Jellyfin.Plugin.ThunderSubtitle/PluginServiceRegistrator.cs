using Jellyfin.Plugin.ThunderSubtitle.Core;
using Jellyfin.Plugin.ThunderSubtitle.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
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

        // Preview button in Jellyfin's own subtitle dialog: the text is parked in memory (never
        // written next to the video) and the script is injected into the web shell at request time.
        services.AddSingleton<PreviewTextCache>();
        services.AddSingleton<IStartupFilter, ClientScriptInjectionFilter>();

        // Jellyfin picks this up through GetExports<ISubtitleProvider>, which fills the
        // per-item "修改字幕 -> 搜索字幕" dialog.
        services.AddSingleton<MediaBrowser.Controller.Subtitles.ISubtitleProvider, Providers.ThunderSubtitleProvider>();

        // Jellyfin instantiates every IScheduledTask implementation through DI at startup,
        // so registering the concrete types is enough for the tasks to show up.
        services.AddSingleton<ThunderFillTask>();
        services.AddSingleton<ThunderRepickTask>();

        // 实时入库监听：新片刮削完成后立刻补一次字幕，不扫全库。
        services.AddHostedService<ItemWatchService>();
    }
}
