using JellyfinTrailerPlugin.Configuration;
using Microsoft.Extensions.Logging;

namespace JellyfinTrailerPlugin.Services;

/// <summary>
/// Holds the in-memory pool of resolved trailers. The pool is refreshed by
/// RefreshTrailersTask on a schedule; individual expired URLs are renewed lazily.
/// </summary>
public class TrailerCacheService
{
    private readonly YouTubeService _youTubeService;
    private readonly YtDlpService _ytDlpService;
    private readonly ILogger<TrailerCacheService> _logger;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<TrailerInfo> _pool = new List<TrailerInfo>();
    private DateTime _lastRefresh = DateTime.MinValue;

    public TrailerCacheService(
        YouTubeService youTubeService,
        YtDlpService ytDlpService,
        ILogger<TrailerCacheService> logger)
    {
        _youTubeService = youTubeService;
        _ytDlpService = ytDlpService;
        _logger = logger;
    }

    public int PoolCount => _pool.Count;
    public DateTime LastRefresh => _lastRefresh;

    /// <summary>Full pool refresh: re-fetch from YouTube + resolve all URLs.</summary>
    public async Task RefreshAsync(PluginConfiguration config, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            _logger.LogInformation("TrailerCinema: starting full pool refresh.");
            var fresh = await _youTubeService.GetTrailersAsync(config, ct);
            await _ytDlpService.ResolveUrlsAsync(fresh, ct);

            var valid = fresh.Where(t => !string.IsNullOrEmpty(t.StreamUrl)).ToList();
            _pool = valid;
            _lastRefresh = DateTime.UtcNow;

            _logger.LogInformation("TrailerCinema: pool refreshed — {Valid}/{Total} trailers with valid URLs.",
                valid.Count, fresh.Count);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Returns up to <paramref name="count"/> trailers, shuffling if configured.
    /// Expired URLs are renewed in the background without blocking the caller.
    /// </summary>
    public async Task<List<TrailerInfo>> GetTrailersAsync(PluginConfiguration config, int count, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        List<TrailerInfo> candidates;
        try
        {
            candidates = config.Shuffle
                ? _pool.OrderBy(_ => Random.Shared.Next()).ToList()
                : new List<TrailerInfo>(_pool);
        }
        finally
        {
            _lock.Release();
        }

        var selected = candidates
            .Where(t => !string.IsNullOrEmpty(t.StreamUrl))
            .Take(count)
            .ToList();

        // Renew any expired URLs asynchronously so the next caller gets fresh ones
        var expired = selected.Where(t => t.IsExpired).ToList();
        if (expired.Count > 0)
        {
            _ = Task.Run(() => _ytDlpService.ResolveUrlsAsync(expired, CancellationToken.None), CancellationToken.None);
        }

        return selected;
    }

    public List<TrailerInfo> GetAllCached() => new List<TrailerInfo>(_pool);
}
