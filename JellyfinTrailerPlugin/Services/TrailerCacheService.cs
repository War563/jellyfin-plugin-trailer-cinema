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

    /// <summary>
    /// Full pool refresh: fetches titles from YouTube, downloads mp4 files, syncs library items.
    /// Already-downloaded files are reused (no re-download).
    /// </summary>
    public async Task RefreshAsync(PluginConfiguration config, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _logger.LogInformation("TrailerCinema: starting pool refresh (download dir: {Dir}).", _downloadDir);
            Directory.CreateDirectory(_downloadDir);

            var fresh = await _youTubeService.GetTrailersAsync(config, ct).ConfigureAwait(false);
            if (fresh.Count == 0)
            {
                _logger.LogWarning("TrailerCinema: no videos returned from YouTube — check API key and channel ID.");
                return;
            }

            await _ytDlpService.DownloadTrailersAsync(fresh, _downloadDir, ct).ConfigureAwait(false);

            var valid = fresh.Where(t => t.IsReady).ToList();
            _pool = valid;
            _lastRefresh = DateTime.UtcNow;

            _libraryService.SyncItems(valid);
            CleanupStaleFiles(valid);

            _logger.LogInformation(
                "TrailerCinema: pool refreshed — {Valid}/{Total} trailers ready.",
                valid.Count, fresh.Count);
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
            var videoId = Path.GetFileNameWithoutExtension(file);
            if (!currentIds.Contains(videoId))
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
