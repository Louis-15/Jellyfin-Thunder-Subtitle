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

        services.AddSingleton(_ => new ThunderClient(ThunderClient.CreateDefaultClient()));
        services.AddSingleton<ProvenanceStore>();
        services.AddSingleton<AiSubtitleJudge>();
        services.AddSingleton<SubtitleJob>();

        // Jellyfin instantiates every IScheduledTask implementation through DI at startup,
        // so registering the concrete types is enough for the tasks to show up.
        services.AddSingleton<ThunderFillTask>();
        services.AddSingleton<ThunderRepickTask>();
    }
}
