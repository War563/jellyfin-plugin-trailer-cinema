using JellyfinTrailerPlugin.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace JellyfinTrailerPlugin;

/// <summary>
/// Entry point discovered automatically by Jellyfin.
/// Owns all plugin services and wires up event subscriptions.
/// </summary>
public class PluginEntryPoint : IServerEntryPoint
{
    /// <summary>Accessible by scheduled tasks and API controllers.</summary>
    public static PluginEntryPoint? Current { get; private set; }

    public TrailerCacheService Cache { get; }

    private readonly PlaybackHookService _hook;

    public PluginEntryPoint(
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        ILoggerFactory loggerFactory)
    {
        var youTubeService  = new YouTubeService(loggerFactory.CreateLogger<YouTubeService>());
        var ytDlpService    = new YtDlpService(loggerFactory.CreateLogger<YtDlpService>());
        Cache               = new TrailerCacheService(youTubeService, ytDlpService, loggerFactory.CreateLogger<TrailerCacheService>());

        _hook = new PlaybackHookService(sessionManager, libraryManager, Cache, loggerFactory.CreateLogger<PlaybackHookService>());

        Current = this;
    }

    public async Task RunAsync()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is not null)
            await Cache.RefreshAsync(config, CancellationToken.None).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _hook.Dispose();
        GC.SuppressFinalize(this);
    }
}
