using JellyfinTrailerPlugin.Configuration;
using JellyfinTrailerPlugin.Services;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace JellyfinTrailerPlugin;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static Plugin? Instance { get; private set; }

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override Guid Id => new("a8b0c1d2-e3f4-5678-90ab-cdef12345678");
    public override string Name => "Trailer Cinema";
    public override string Description => "Reproduce trailers en castellano antes de cada película.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
        };
    }
}

/// <summary>Registers all plugin services into the Jellyfin DI container.</summary>
public class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<YouTubeService>();
        serviceCollection.AddSingleton<YtDlpService>();
        serviceCollection.AddSingleton<TrailerCacheService>();
        // PlaybackHookService must be singleton so it subscribes once and stays alive
        serviceCollection.AddSingleton<PlaybackHookService>();
        // Force instantiation at startup so the event subscription happens immediately
        serviceCollection.AddHostedService<PluginStartupService>();
    }
}

/// <summary>
/// Ensures PlaybackHookService is instantiated (and thus subscribed to events)
/// as soon as Jellyfin starts.
/// </summary>
internal class PluginStartupService : Microsoft.Extensions.Hosting.IHostedService
{
    // Resolved from DI — instantiation is the side-effect we need
    public PluginStartupService(PlaybackHookService _) { }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
