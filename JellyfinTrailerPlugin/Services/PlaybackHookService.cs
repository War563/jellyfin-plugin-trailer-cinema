using MediaBrowser.Controller.Entities;
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

    // (sessionId, movieId) → trailers already injected. Prevents re-injection.
    private readonly HashSet<(string, Guid)> _injectedSessions = new();

    // sessionId → movieId to play once the combined trailer file finishes.
    private readonly Dictionary<string, Guid> _pendingMovies = new();

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
            _pendingMovies.Remove(e.SessionInfo.Id);
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        // Movie stopped: clear suppression after a genuine viewing so trailers re-inject next time.
        if (e.Item is Movie)
        {
            if ((e.PlaybackPositionTicks ?? 0) > ThirtySecondsTicks)
            {
                lock (_lock)
                {
                    _injectedSessions.Remove((e.Session.Id, e.Item.Id));
                }
            }
            return;
        }

        // Combined trailer finished: play the queued movie.
        // Neither Android TV nor VLC advance the Jellyfin queue client-side,
        // so the server must push PlayNow [movie] explicitly.
        if (e.Item.Id == TrailerLibraryService.CombinedItemGuid)
        {
            Guid movieId;
            lock (_lock)
            {
                if (!_pendingMovies.TryGetValue(e.Session.Id, out movieId))
                    return;
                _pendingMovies.Remove(e.Session.Id);
            }

            _logger.LogInformation(
                "TrailerCinema: combined trailer finished — starting movie {Id} in session {S}.",
                movieId, e.Session.Id);

            _ = Task.Run(() => PlayMovieAsync(e.Session, movieId));
        }
    }

    private async Task PlayMovieAsync(SessionInfo session, Guid movieId)
    {
        // Brief pause so the player settles before the next command.
        await Task.Delay(600).ConfigureAwait(false);

        await _sessionManager.SendPlayCommand(
            session.Id,
            session.Id,
            new PlayRequest { PlayCommand = PlayCommand.PlayNow, ItemIds = [movieId], StartPositionTicks = 0 },
            CancellationToken.None).ConfigureAwait(false);
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
                    "TrailerCinema: combined trailer not ready — skipping for '{Movie}'.", movie.Name);
                return;
            }

            // Register the movie to play once the combined trailer stops.
            lock (_lock)
            {
                _pendingMovies[session.Id] = movie.Id;
            }

            // Stop the movie that just started.
            await _sessionManager.SendPlaystateCommand(
                session.Id,
                session.Id,
                new PlaystateRequest { Command = PlaystateCommand.Stop },
                CancellationToken.None).ConfigureAwait(false);

            await Task.Delay(800).ConfigureAwait(false);

            // Play ONLY the combined trailer file. When it finishes, OnPlaybackStopped
            // fires and PlayMovieAsync sends PlayNow [movie] to the client.
            await _sessionManager.SendPlayCommand(
                session.Id,
                session.Id,
                new PlayRequest
                {
                    PlayCommand        = PlayCommand.PlayNow,
                    ItemIds            = [TrailerLibraryService.CombinedItemGuid],
                    StartPositionTicks = 0
                },
                CancellationToken.None).ConfigureAwait(false);

            _logger.LogInformation(
                "TrailerCinema: playing combined trailer before '{Movie}' in session {S}.",
                movie.Name, session.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TrailerCinema: error for session {S}.", session.Id);

            lock (_lock)
            {
                _injectedSessions.Remove((session.Id, movie.Id));
                _pendingMovies.Remove(session.Id);
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
