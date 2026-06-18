using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace JellyfinTrailerPlugin.Services;

public class PlaybackHookService : IDisposable
{
    private readonly ISessionManager _sessionManager;
    private readonly TrailerCacheService _cache;
    private readonly ILogger<PlaybackHookService> _logger;

    // Tracks (sessionId, movieId) pairs where trailers have already been injected.
    // Prevents re-injection if the client briefly restarts playback.
    // Cleared when the movie finishes after >30 s so trailers play again next time.
    private readonly HashSet<(string, Guid)> _injectedSessions = new();
    private readonly object _lock = new();

    private static readonly long ThirtySecondsTicks = TimeSpan.FromSeconds(30).Ticks;

    public PlaybackHookService(
        ISessionManager sessionManager,
        TrailerCacheService cache,
        ILogger<PlaybackHookService> logger)
    {
        _sessionManager = sessionManager;
        _cache          = cache;
        _logger         = logger;

        _sessionManager.PlaybackStart   += OnPlaybackStart;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _sessionManager.SessionEnded    += OnSessionEnded;
    }

    private void OnSessionEnded(object? sender, SessionEventArgs e)
    {
        lock (_lock)
        {
            _injectedSessions.RemoveWhere(k => k.Item1 == e.SessionInfo.Id);
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        if (e.Item is not Movie) return;

        // Only clear suppression after a real watch session (>30 s played).
        if ((e.PlaybackPositionTicks ?? 0) > ThirtySecondsTicks)
        {
            lock (_lock)
            {
                _injectedSessions.Remove((e.Session.Id, e.Item.Id));
            }
        }
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        if (e.Item is not Movie) return;

        lock (_lock)
        {
            if (!_injectedSessions.Add((e.Session.Id, e.Item.Id)))
                return;
        }

        _ = Task.Run(() => InjectTrailersAsync(e.Session, e.Item));
    }

    private async Task InjectTrailersAsync(SessionInfo session, MediaBrowser.Controller.Entities.BaseItem movie)
    {
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null) return;

            var allReady = await _cache.GetTrailersAsync(config, config.PoolSize, CancellationToken.None)
                .ConfigureAwait(false);

            if (allReady.Count == 0)
            {
                _logger.LogInformation(
                    "TrailerCinema: pool has no ready trailers — skipping for session {S}.", session.Id);
                return;
            }

            // Shuffle here so every call gets a different order.
            if (config.Shuffle)
                allReady = allReady.OrderBy(_ => Random.Shared.Next()).ToList();

            var trailerIds = allReady
                .Where(t => t.JellyfinItemId != Guid.Empty)
                .Select(t => t.JellyfinItemId)
                .Take(config.TrailerCount)
                .ToList();

            if (trailerIds.Count == 0)
            {
                _logger.LogWarning(
                    "TrailerCinema: trailers ready but no library items yet — waiting for next refresh.");
                return;
            }

            // Stop the movie that just started.
            await _sessionManager.SendPlaystateCommand(
                session.Id,
                session.Id,
                new PlaystateRequest { Command = PlaystateCommand.Stop },
                CancellationToken.None).ConfigureAwait(false);

            await Task.Delay(800).ConfigureAwait(false);

            // Queue all trailers followed by the original movie in a single PlayNow.
            var allIds = trailerIds.Append(movie.Id).ToArray();

            await _sessionManager.SendPlayCommand(
                session.Id,
                session.Id,
                new PlayRequest
                {
                    PlayCommand        = PlayCommand.PlayNow,
                    ItemIds            = allIds,
                    StartPositionTicks = 0
                },
                CancellationToken.None).ConfigureAwait(false);

            _logger.LogInformation(
                "TrailerCinema: queued {N} trailer(s) before '{Movie}' in session {S}.",
                trailerIds.Count, movie.Name, session.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TrailerCinema: error injecting trailers for session {S}.", session.Id);

            lock (_lock)
            {
                _injectedSessions.Remove((session.Id, movie.Id));
            }
        }
    }

    public void Dispose()
    {
        _sessionManager.PlaybackStart   -= OnPlaybackStart;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _sessionManager.SessionEnded    -= OnSessionEnded;
        GC.SuppressFinalize(this);
    }
}
