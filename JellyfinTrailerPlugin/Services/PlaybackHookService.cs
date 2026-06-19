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

    private readonly HashSet<(string, Guid)> _injectedSessions = new();

    // Per-session state while trailers are playing.
    // ItemStartedAt is set by us (server clock) when the client reports PlaybackStart
    // for a trailer. We use elapsed wall-clock time — not PlaybackPositionTicks from
    // the client — to distinguish natural end from "user pressed Back", because
    // Fire TV / Android TV often reports positionTicks = 0 on natural video end.
    private sealed record SessionState(
        Guid MovieId,
        IReadOnlyList<TrailerInfo> Queue,
        int CurrentIndex,
        CancellationTokenSource TimerCts,
        DateTime ItemStartedAt);

    private readonly Dictionary<string, SessionState> _sessions = new();
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

    // ── Event handlers ───────────────────────────────────────────────────────

    private void OnSessionEnded(object? sender, SessionEventArgs e)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(e.SessionInfo.Id, out var s))
            {
                s.TimerCts.Cancel();
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
            if ((e.PlaybackPositionTicks ?? 0) > ThirtySecondsTicks)
                lock (_lock) { _injectedSessions.Remove((sessionId, e.Item.Id)); }
            return;
        }

        // A trailer stopped. We need to distinguish:
        //   • Natural end  → the timer will advance the queue; do nothing.
        //   • User Back    → cancel the timer so we don't start the next trailer.
        //
        // Fire TV / Android TV frequently reports positionTicks = 0 even when the
        // video reached the end, so we cannot trust PlaybackPositionTicks here.
        // Instead we compare the server-side wall-clock elapsed time against the
        // known trailer duration.
        var stoppedId = e.Item.Id;
        _ = Task.Run(async () =>
        {
            long durationMs;
            double elapsedMs;
            int currentIdx;

            lock (_lock)
            {
                if (!_sessions.TryGetValue(sessionId, out var state)) return;
                int idx = IndexOf(state.Queue, stoppedId);
                if (idx < 0 || state.CurrentIndex != idx) return;

                durationMs  = state.Queue[idx].DurationMs;
                elapsedMs   = (DateTime.UtcNow - state.ItemStartedAt).TotalMilliseconds;
                currentIdx  = idx;
            }

            // If the video played for >= 75 % of its known duration this is a natural
            // end — the timer will handle advancement, so bail out here.
            if (durationMs > 0 && elapsedMs >= durationMs * 0.75)
            {
                _logger.LogDebug(
                    "TrailerCinema: natural end for trailer [{I}] in session {S} (elapsed {E:F0} ms / {D} ms).",
                    currentIdx, sessionId, elapsedMs, durationMs);
                return;
            }

            // Elapsed < 75 % → user likely pressed Back or Next.
            // Wait briefly: if the client moved to the next item (user pressed Next),
            // OnPlaybackStart will have updated CurrentIndex and we should NOT cancel.
            await Task.Delay(2500).ConfigureAwait(false);

            lock (_lock)
            {
                if (!_sessions.TryGetValue(sessionId, out var state)) return;
                if (state.Queue[state.CurrentIndex].JellyfinItemId != stoppedId) return; // already advanced → Next
                // Still on the stopped item → user pressed Back → abort
                state.TimerCts.Cancel();
                _sessions.Remove(sessionId);
                _logger.LogInformation(
                    "TrailerCinema: user pressed Back in session {S} — aborting trailer sequence.", sessionId);
            }
        });
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        var sessionId = e.Session.Id;

        if (e.Item is Movie)
        {
            lock (_lock)
            {
                if (_sessions.TryGetValue(sessionId, out var old))
                {
                    old.TimerCts.Cancel();
                    _sessions.Remove(sessionId);
                }
                if (!_injectedSessions.Add((sessionId, e.Item.Id))) return;
            }
            _ = Task.Run(() => InjectTrailersAsync(e.Session, e.Item));
            return;
        }

        if (e.Item is not Video) return;

        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out var state)) return;
            int newIndex = IndexOf(state.Queue, e.Item.Id);
            if (newIndex < 0) return;

            state.TimerCts.Cancel();
            var newCts = new CancellationTokenSource();
            _sessions[sessionId] = state with
            {
                CurrentIndex  = newIndex,
                TimerCts      = newCts,
                ItemStartedAt = DateTime.UtcNow   // server clock; reliable even if client reports 0
            };

            ScheduleAdvance(sessionId, newIndex, state.Queue, state.MovieId,
                state.Queue[newIndex].DurationMs, newCts.Token);
        }
    }

    // ── Injection ────────────────────────────────────────────────────────────

    private async Task InjectTrailersAsync(SessionInfo session, BaseItem movie)
    {
        try
        {
            var count    = Plugin.Instance?.Configuration.TrailerCount ?? 2;
            var trailers = _cache.GetRandomTrailers(count);

            if (trailers.Count == 0)
            {
                _logger.LogWarning(
                    "TrailerCinema: no ready trailers — skipping for '{Movie}'.", movie.Name);
                return;
            }

            var cts    = new CancellationTokenSource();
            var allIds = trailers.Select(t => t.JellyfinItemId).Append(movie.Id).ToArray();

            lock (_lock)
            {
                _sessions[session.Id] = new SessionState(
                    movie.Id, trailers, 0, cts, DateTime.UtcNow);
            }

            await _sessionManager.SendPlaystateCommand(
                session.Id, session.Id,
                new PlaystateRequest { Command = PlaystateCommand.Stop },
                CancellationToken.None).ConfigureAwait(false);

            await Task.Delay(800).ConfigureAwait(false);

            // Send full queue [t1 … tN, movie] so the client shows navigation arrows.
            await _sessionManager.SendPlayCommand(
                session.Id, session.Id,
                new PlayRequest { PlayCommand = PlayCommand.PlayNow, ItemIds = allIds, StartPositionTicks = 0 },
                CancellationToken.None).ConfigureAwait(false);

            // Bootstrap timer for t1. OnPlaybackStart will cancel this and restart it
            // once the client actually reports that t1 has begun playing, giving us an
            // accurate ItemStartedAt and a properly-reset timer.
            ScheduleAdvance(session.Id, 0, trailers, movie.Id, trailers[0].DurationMs, cts.Token);

            _logger.LogInformation(
                "TrailerCinema: queued {N} trailer(s) + movie '{M}' (session {S}).",
                trailers.Count, movie.Name, session.Id);
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

    // ── Timer-based advance ───────────────────────────────────────────────────

    private void ScheduleAdvance(
        string sessionId,
        int expectedIndex,
        IReadOnlyList<TrailerInfo> queue,
        Guid movieId,
        long durationMs,
        CancellationToken ct)
    {
        var fallbackMs = (long)((Plugin.Instance?.Configuration.MaxDurationSeconds ?? 300) * 1000 + 15_000);
        var delayMs    = durationMs > 0 ? durationMs + 3_000 : fallbackMs;

        if (durationMs == 0)
            _logger.LogWarning(
                "TrailerCinema: trailer[{I}] has no duration — using {D}s fallback timer. " +
                "Trigger a manual Refresh to populate durations.",
                expectedIndex, delayMs / 1000);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay((int)Math.Min(delayMs, int.MaxValue), ct).ConfigureAwait(false);

                Guid[]? remaining = null;
                lock (_lock)
                {
                    if (!_sessions.TryGetValue(sessionId, out var state)) return;
                    if (state.CurrentIndex != expectedIndex) return; // already advanced

                    remaining = queue
                        .Skip(expectedIndex + 1)
                        .Select(t => t.JellyfinItemId)
                        .Append(movieId)
                        .ToArray();
                }

                if (remaining is null) return;

                _logger.LogInformation(
                    "TrailerCinema: timer → PlayNow [{R} item(s)] after trailer[{I}] in session {S}.",
                    remaining.Length, expectedIndex, sessionId);

                await _sessionManager.SendPlayCommand(
                    sessionId, sessionId,
                    new PlayRequest { PlayCommand = PlayCommand.PlayNow, ItemIds = remaining, StartPositionTicks = 0 },
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        });
    }

    private static int IndexOf(IReadOnlyList<TrailerInfo> queue, Guid itemId)
    {
        for (int i = 0; i < queue.Count; i++)
            if (queue[i].JellyfinItemId == itemId) return i;
        return -1;
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
