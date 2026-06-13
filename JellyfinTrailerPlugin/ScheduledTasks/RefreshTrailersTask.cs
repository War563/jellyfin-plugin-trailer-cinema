using JellyfinTrailerPlugin.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace JellyfinTrailerPlugin.ScheduledTasks;

public class RefreshTrailersTask : IScheduledTask
{
    private readonly TrailerCacheService _cache;
    private readonly ILogger<RefreshTrailersTask> _logger;

    public RefreshTrailersTask(TrailerCacheService cache, ILogger<RefreshTrailersTask> logger)
    {
        _cache  = cache;
        _logger = logger;
    }

    public string Name => "Trailer Cinema: Refresh pool";
    public string Key => "TrailerCinemaRefresh";
    public string Description => "Fetches trailers from YouTube and resolves their direct stream URLs.";
    public string Category => "Trailer Cinema";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            _logger.LogWarning("TrailerCinema: plugin not ready during scheduled refresh.");
            return;
        }

        progress.Report(0);
        await _cache.RefreshAsync(config, ct).ConfigureAwait(false);
        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type          = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(6).Ticks
        };
    }
}
