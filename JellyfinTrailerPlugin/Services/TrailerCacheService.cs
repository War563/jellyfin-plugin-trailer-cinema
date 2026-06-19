using JellyfinTrailerPlugin.Configuration;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace JellyfinTrailerPlugin.Services;

public class TrailerCacheService
{
    private readonly YouTubeService _youTubeService;
    private readonly YtDlpService _ytDlpService;
    private readonly TrailerLibraryService _libraryService;
    private readonly ILogger<TrailerCacheService> _logger;
    private readonly string _downloadDir;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<TrailerInfo> _pool = new();
    private DateTime _lastRefresh = DateTime.MinValue;
    private volatile string? _combinedPath;

    public TrailerCacheService(
        YouTubeService youTubeService,
        YtDlpService ytDlpService,
        TrailerLibraryService libraryService,
        IApplicationPaths appPaths,
        ILogger<TrailerCacheService> logger)
    {
        _youTubeService = youTubeService;
        _ytDlpService   = ytDlpService;
        _libraryService = libraryService;
        _logger         = logger;
        _downloadDir    = Path.Combine(appPaths.DataPath, "trailercinema");
    }

    public int PoolCount => _pool.Count;
    public DateTime LastRefresh => _lastRefresh;

    public string? GetCombinedPath() => _combinedPath;
    public bool IsCombinedReady() => _combinedPath is not null && File.Exists(_combinedPath);

    /// <summary>
    /// Full pool refresh: fetches titles from YouTube, downloads mp4 files locally,
    /// concatenates TrailerCount trailers into a single combined file for VLC compatibility,
    /// then syncs Jellyfin library items.
    /// </summary>
    public async Task RefreshAsync(PluginConfiguration config, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _logger.LogInformation("TrailerCinema: starting pool refresh (dir: {Dir}).", _downloadDir);
            Directory.CreateDirectory(_downloadDir);

            var fresh = await _youTubeService.GetTrailersAsync(config, ct).ConfigureAwait(false);
            if (fresh.Count == 0)
            {
                _logger.LogWarning("TrailerCinema: no videos returned from YouTube — check API key and channel ID.");
                return;
            }

            await _ytDlpService.DownloadTrailersAsync(fresh, _downloadDir, ct).ConfigureAwait(false);

            var valid = fresh.Where(t => t.IsReady).ToList();
            _pool        = valid;
            _lastRefresh = DateTime.UtcNow;

            // Build a single combined MP4 (TrailerCount trailers in sequence).
            // This is played via VLC as one file, avoiding VLC's inability to advance Jellyfin queues.
            var selected = (config.Shuffle
                    ? valid.OrderBy(_ => Random.Shared.Next()).ToList()
                    : valid)
                .Take(config.TrailerCount)
                .ToList();

            var combinedPath = Path.Combine(_downloadDir, "combined_trailers.mp4");
            await _ytDlpService.ConcatenateAsync(
                selected.Select(t => t.LocalPath).ToList(),
                combinedPath,
                ct).ConfigureAwait(false);

            _combinedPath = File.Exists(combinedPath) ? combinedPath : null;

            var serverBaseUrl = config.ServerBaseUrl;
            _libraryService.SyncItems(valid, serverBaseUrl);

            if (_combinedPath is not null)
                _libraryService.SyncCombinedItem(serverBaseUrl);

            CleanupStaleFiles(valid);

            _logger.LogInformation(
                "TrailerCinema: pool refreshed — {Valid}/{Total} trailers ready, combined={Combined}.",
                valid.Count, fresh.Count, _combinedPath is not null ? "yes" : "no");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Returns all trailers that have a local file ready to play.</summary>
    public Task<List<TrailerInfo>> GetTrailersAsync(PluginConfiguration config, int count, CancellationToken ct)
    {
        var ready = _pool.Where(t => t.IsReady).ToList();
        return Task.FromResult(ready);
    }

    public List<TrailerInfo> GetAllCached() => new(_pool);

    private void CleanupStaleFiles(IReadOnlyList<TrailerInfo> currentPool)
    {
        if (!Directory.Exists(_downloadDir))
            return;

        var currentIds = currentPool.Select(t => t.VideoId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.GetFiles(_downloadDir, "*.mp4"))
        {
            var name = Path.GetFileNameWithoutExtension(file);

            // Never delete the combined file — it's rebuilt each refresh, not tied to a video ID.
            if (name.StartsWith("combined_", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!currentIds.Contains(name))
            {
                try
                {
                    File.Delete(file);
                    _logger.LogDebug("TrailerCinema: deleted stale file {File}.", file);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "TrailerCinema: could not delete stale file {File}.", file);
                }
            }
        }
    }
}
