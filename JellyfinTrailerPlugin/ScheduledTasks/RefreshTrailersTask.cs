using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace JellyfinTrailerPlugin.ScheduledTasks;

public class RefreshTrailersTask : IScheduledTask
{
    private readonly ILogger<RefreshTrailersTask> _logger;

    public RefreshTrailersTask(ILogger<RefreshTrailersTask> logger)
    {
        _logger = logger;
    }

    public string Name => "Trailer Cinema: Refresh pool";
    public string Key => "TrailerCinemaRefresh";
    public string Description => "Fetches trailers from YouTube and resolves their direct stream URLs.";
    public string Category => "Trailer Cinema";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        var cache  = PluginEntryPoint.Current?.Cache;

        if (config is null || cache is null)
        {
            _logger.LogWarning("TrailerCinema: plugin not ready during scheduled refresh.");
            return;
        }

        progress.Report(0);
        await cache.RefreshAsync(config, ct).ConfigureAwait(false);
        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerInterval, IntervalTicks = TimeSpan.FromHours(6).Ticks };
    }
}
