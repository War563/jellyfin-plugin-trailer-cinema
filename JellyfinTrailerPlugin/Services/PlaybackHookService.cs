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

    // (sessionId, movieId) pairs where trailers have already started injecting.
    private readonly HashSet<(string, Guid)> _injectedSessions = new();

    // Per-session state: the movie to play when trailers finish + remaining trailer IDs.
    private readonly Dictionary<string, (Guid MovieId, Queue<Guid> Pending)> _trailerQueues = new();

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
            _trailerQueues.Remove(e.SessionInfo.Id);
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        // Movie stopped: clear suppression after a real viewing so trailers re-inject next time.
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

        // Non-movie stopped: advance to the next trailer (or the movie if all done).
        bool hasQueue;
        lock (_lock) { hasQueue = _trailerQueues.ContainsKey(e.Session.Id); }

        if (hasQueue)
            _ = Task.Run(() => AdvanceQueueAsync(e.Session));
    }

    private async Task AdvanceQueueAsync(SessionInfo session)
    {
        // Brief pause so the client settles before the next PlayNow.
        await Task.Delay(600).ConfigureAwait(false);

        Guid nextId;
        lock (_lock)
        {
            if (!_trailerQueues.TryGetValue(session.Id, out var state))
                return;

            if (state.Pending.Count > 0)
            {
                nextId = state.Pending.Dequeue();
                _logger.LogInformation(
                    "TrailerCinema: advancing to next trailer ({Remaining} left after this) in session {S}.",
                    state.Pending.Count, session.Id);
            }
            else
            {
                nextId = state.MovieId;
                _trailerQueues.Remove(session.Id);
                _logger.LogInformation(
                    "TrailerCinema: all trailers done — starting movie in session {S}.", session.Id);
            }
        }

        await _sessionManager.SendPlayCommand(
            session.Id,
            session.Id,
            new PlayRequest { PlayCommand = PlayCommand.PlayNow, ItemIds = [nextId], StartPositionTicks = 0 },
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

        _ = Task.Run(() => StartTrailerSequenceAsync(e.Session, e.Item));
    }

    private async Task StartTrailerSequenceAsync(SessionInfo session, BaseItem movie)
    {
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null) return;

            var allReady = await _cache.GetTrailersAsync(config, config.PoolSize, CancellationToken.None)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "TrailerCinema: pool={Pool} ready={Ready} TrailerCount={Count}",
                config.PoolSize, allReady.Count, config.TrailerCount);

            if (allReady.Count == 0)
            {
                _logger.LogWarning(
                    "TrailerCinema: pool has no ready trailers — skipping for session {S}.", session.Id);
                return;
            }

            if (config.Shuffle)
                allReady = allReady.OrderBy(_ => Random.Shared.Next()).ToList();

            var withLibItem = allReady.Where(t => t.JellyfinItemId != Guid.Empty).ToList();
            _logger.LogInformation(
                "TrailerCinema: {WithItem}/{Ready} trailers have a library item ID.",
                withLibItem.Count, allReady.Count);

            var trailerIds = withLibItem
                .Select(t => t.JellyfinItemId)
                .Take(config.TrailerCount)
                .ToList();

            if (trailerIds.Count == 0)
            {
                _logger.LogWarning(
                    "TrailerCinema: trailers ready but no library items yet — waiting for next refresh.");
                return;
            }

            // First trailer plays immediately; the rest go into the per-session queue.
            var firstId   = trailerIds[0];
            var remaining = new Queue<Guid>(trailerIds.Skip(1));

            lock (_lock)
            {
                _trailerQueues[session.Id] = (movie.Id, remaining);
            }

            // Stop the movie that just started.
            await _sessionManager.SendPlaystateCommand(
                session.Id,
                session.Id,
                new PlaystateRequest { Command = PlaystateCommand.Stop },
                CancellationToken.None).ConfigureAwait(false);

            await Task.Delay(800).ConfigureAwait(false);

            // Play ONLY the first trailer — the rest follow via OnPlaybackStopped.
            await _sessionManager.SendPlayCommand(
                session.Id,
                session.Id,
                new PlayRequest { PlayCommand = PlayCommand.PlayNow, ItemIds = [firstId], StartPositionTicks = 0 },
                CancellationToken.None).ConfigureAwait(false);

            _logger.LogInformation(
                "TrailerCinema: started trailer 1/{Total} before '{Movie}' in session {S}.",
                trailerIds.Count, movie.Name, session.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TrailerCinema: error starting trailer sequence for session {S}.", session.Id);

            lock (_lock)
            {
                _injectedSessions.Remove((session.Id, movie.Id));
                _trailerQueues.Remove(session.Id);
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
