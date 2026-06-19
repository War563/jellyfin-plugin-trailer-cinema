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

    // (sessionId, movieId) that have already had trailers injected. Prevents re-injection.
    private readonly HashSet<(string, Guid)> _injectedSessions = new();

    // Per-session state while trailers are playing.
    // CurrentIndex = which trailer in Queue is currently playing.
    // TimerCts cancels the timer-fallback task when PlaybackStopped arrives first.
    private sealed record SessionState(
        Guid MovieId,
        IReadOnlyList<TrailerInfo> Queue,
        int CurrentIndex,
        CancellationTokenSource TimerCts);

    private readonly Dictionary<string, SessionState> _sessions = new();
    private readonly object _lock = new();

    private static readonly long ThirtySecondsTicks = TimeSpan.FromSeconds(30).Ticks;
    // Used when ffprobe couldn't determine a trailer's duration.
    private const long FallbackTimerMs = 15 * 60 * 1000L;

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

    // ── Event handlers ──────────────────────────────────────────────────────

    private void OnSessionEnded(object? sender, SessionEventArgs e)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(e.SessionInfo.Id, out var state))
            {
                state.TimerCts.Cancel();
                _sessions.Remove(e.SessionInfo.Id);
            }
            _injectedSessions.RemoveWhere(k => k.Item1 == e.SessionInfo.Id);
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        var sessionId = e.Session.Id;

        if (e.Item is Movie)
        {
            // After a genuine viewing (>30 s), clear suppression so trailers re-inject next time.
            if ((e.PlaybackPositionTicks ?? 0) > ThirtySecondsTicks)
                lock (_lock) { _injectedSessions.Remove((sessionId, e.Item.Id)); }
            return;
        }

        // Check whether the stopped item is the trailer we're currently tracking.
        int currentIndex;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out var state)) return;
            if (state.Queue[state.CurrentIndex].JellyfinItemId != e.Item.Id) return;
            currentIndex = state.CurrentIndex;
        }

        _ = Task.Run(() => AdvanceSessionAsync(sessionId, currentIndex, e.Session));
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        if (e.Item is not Movie) return;

        lock (_lock)
        {
            if (!_injectedSessions.Add((e.Session.Id, e.Item.Id))) return;
        }

        _ = Task.Run(() => InjectTrailersAsync(e.Session, e.Item));
    }

    // ── Injection flow ───────────────────────────────────────────────────────

    private async Task InjectTrailersAsync(SessionInfo session, BaseItem movie)
    {
        try
        {
            var count    = Plugin.Instance?.Configuration.TrailerCount ?? 2;
            var trailers = _cache.GetRandomTrailers(count);

            if (trailers.Count == 0)
            {
                _logger.LogWarning(
                    "TrailerCinema: no ready trailers in pool — skipping for '{Movie}'.", movie.Name);
                return;
            }

            lock (_lock)
            {
                if (_sessions.TryGetValue(session.Id, out var old)) old.TimerCts.Cancel();
                _sessions[session.Id] = new SessionState(movie.Id, trailers, 0, new CancellationTokenSource());
            }

            // Stop the movie that just started.
            await _sessionManager.SendPlaystateCommand(
                session.Id, session.Id,
                new PlaystateRequest { Command = PlaystateCommand.Stop },
                CancellationToken.None).ConfigureAwait(false);

            await Task.Delay(800).ConfigureAwait(false);

            await PlayTrailerAtIndexAsync(session, 0).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TrailerCinema: inject failed for session {S}.", session.Id);
            lock (_lock)
            {
                _injectedSessions.Remove((session.Id, movie.Id));
                if (_sessions.TryGetValue(session.Id, out var s)) s.TimerCts.Cancel();
                _sessions.Remove(session.Id);
            }
        }
    }

    // ── Per-trailer play + timer fallback ────────────────────────────────────

    private async Task PlayTrailerAtIndexAsync(SessionInfo session, int index)
    {
        TrailerInfo trailer;
        CancellationToken timerCt;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(session.Id, out var state)) return;
            if (index >= state.Queue.Count || state.CurrentIndex != index) return;
            trailer = state.Queue[index];
            timerCt = state.TimerCts.Token;
        }

        await _sessionManager.SendPlayCommand(
            session.Id, session.Id,
            new PlayRequest
            {
                PlayCommand        = PlayCommand.PlayNow,
                ItemIds            = [trailer.JellyfinItemId],
                StartPositionTicks = 0
            },
            CancellationToken.None).ConfigureAwait(false);

        _logger.LogInformation(
            "TrailerCinema: [{I}/{N}] '{Title}' ({Dur}s) → session {S}.",
            index + 1, GetQueueCount(session.Id), trailer.Title, trailer.DurationMs / 1000, session.Id);

        // Timer fallback: if PlaybackStopped never arrives (Fire TV sticks on the last frame),
        // the server advances automatically after the known video duration + a safety buffer.
        var delayMs = trailer.DurationMs > 0 ? trailer.DurationMs + 12_000 : FallbackTimerMs;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay((int)Math.Min(delayMs, int.MaxValue), timerCt).ConfigureAwait(false);
                _logger.LogInformation(
                    "TrailerCinema: timer fallback fired for '{Title}' in session {S}.", trailer.Title, session.Id);
                await AdvanceSessionAsync(session.Id, index, session).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* PlaybackStopped fired — timer not needed */ }
        });
    }

    // ── Advance to next trailer or movie ─────────────────────────────────────

    private async Task AdvanceSessionAsync(string sessionId, int expectedIndex, SessionInfo session)
    {
        Guid movieId;
        bool playMovie;
        int  nextIndex;

        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out var state)) return;
            if (state.CurrentIndex != expectedIndex) return; // Already advanced by the other path.

            state.TimerCts.Cancel();

            nextIndex = expectedIndex + 1;
            playMovie = nextIndex >= state.Queue.Count;
            movieId   = state.MovieId;

            if (!playMovie)
                _sessions[sessionId] = state with { CurrentIndex = nextIndex, TimerCts = new CancellationTokenSource() };
            else
                _sessions.Remove(sessionId);
        }

        await Task.Delay(500).ConfigureAwait(false);

        if (!playMovie)
            await PlayTrailerAtIndexAsync(session, nextIndex).ConfigureAwait(false);
        else
            await PlayMovieAsync(session, movieId).ConfigureAwait(false);
    }

    private async Task PlayMovieAsync(SessionInfo session, Guid movieId)
    {
        await _sessionManager.SendPlayCommand(
            session.Id, session.Id,
            new PlayRequest { PlayCommand = PlayCommand.PlayNow, ItemIds = [movieId], StartPositionTicks = 0 },
            CancellationToken.None).ConfigureAwait(false);

        _logger.LogInformation("TrailerCinema: starting movie {Id} in session {S}.", movieId, session.Id);
    }

    private int GetQueueCount(string sessionId)
    {
        lock (_lock)
            return _sessions.TryGetValue(sessionId, out var s) ? s.Queue.Count : 0;
    }

    // ── IDisposable ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        _sessionManager.PlaybackStart   -= OnPlaybackStart;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _sessionManager.SessionEnded    -= OnSessionEnded;

        lock (_lock)
        {
            foreach (var s in _sessions.Values) s.TimerCts.Cancel();
            _sessions.Clear();
        }

        GC.SuppressFinalize(this);
    }
}
