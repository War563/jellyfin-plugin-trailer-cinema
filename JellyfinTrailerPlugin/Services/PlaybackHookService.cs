using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace JellyfinTrailerPlugin.Services;

public class PlaybackHookService : IDisposable
{
    private readonly ISessionManager _sessionManager;
    private readonly TrailerCacheService _cache;
    private readonly ILogger<PlaybackHookService> _logger;

    // (sessionId, movieId) pairs where trailers have been injected.
    // Prevents re-injection if the client briefly restarts playback.
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

        // Clear suppression after a genuine viewing so trailers re-inject next time.
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

    private async Task InjectTrailersAsync(SessionInfo session, BaseItem movie)
    {
        try
        {
            if (!_cache.IsCombinedReady())
            {
                _logger.LogWarning(
                    "TrailerCinema: combined trailer not ready yet — skipping for '{Movie}'.", movie.Name);
                return;
            }

            // Stop the movie that just started.
            await _sessionManager.SendPlaystateCommand(
                session.Id,
                session.Id,
                new PlaystateRequest { Command = PlaystateCommand.Stop },
                CancellationToken.None).ConfigureAwait(false);

            await Task.Delay(800).ConfigureAwait(false);

            // Play [combined_trailers, movie].
            // The combined file contains all selected trailers as one MP4 —
            // VLC plays the file start-to-finish, then the client advances to the movie.
            var itemIds = new[] { TrailerLibraryService.CombinedItemGuid, movie.Id };

            await _sessionManager.SendPlayCommand(
                session.Id,
                session.Id,
                new PlayRequest
                {
                    PlayCommand        = PlayCommand.PlayNow,
                    ItemIds            = itemIds,
                    StartPositionTicks = 0
                },
                CancellationToken.None).ConfigureAwait(false);

            _logger.LogInformation(
                "TrailerCinema: queued combined trailer before '{Movie}' in session {S}.",
                movie.Name, session.Id);
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
