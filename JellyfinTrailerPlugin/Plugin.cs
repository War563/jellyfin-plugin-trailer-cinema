using JellyfinTrailerPlugin.Channels;
using JellyfinTrailerPlugin.Configuration;
using JellyfinTrailerPlugin.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Plugins;
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
            Name                 = Name,
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
        };
    }
}

public class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<YouTubeService>();
        serviceCollection.AddSingleton<YtDlpService>();
        serviceCollection.AddSingleton<TrailerCacheService>();

        // TrailerChannel is registered both as concrete type (for direct injection)
        // and as IChannel so Jellyfin's ChannelManager discovers it automatically.
        serviceCollection.AddSingleton<TrailerChannel>();
        serviceCollection.AddSingleton<IChannel>(sp => sp.GetRequiredService<TrailerChannel>());

        serviceCollection.AddSingleton<PlaybackHookService>();
        serviceCollection.AddHostedService<PluginStartupService>();
    }
}

internal class PluginStartupService : IHostedService
{
    private readonly PlaybackHookService _hook;
    private readonly TrailerCacheService _cache;

    public PluginStartupService(PlaybackHookService hook, TrailerCacheService cache)
    {
        _hook  = hook;
        _cache = cache;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is not null)
            await _cache.RefreshAsync(config, cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _hook.Dispose();
        return Task.CompletedTask;
    }
}
