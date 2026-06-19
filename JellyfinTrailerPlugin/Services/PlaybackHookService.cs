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

    // (sessionId, movieId) already injected — prevents re-injection when movie starts.
    private readonly HashSet<(string, Guid)> _injectedSessions = new();

    // Per-session state while trailers are playing.
    // The full queue [t1…tN, movie] is sent via PlayNow so the client shows navigation arrows.
    // CurrentIndex tracks which trailer is playing. TimerCts cancels the advance timer.
    private sealed record SessionState(
        Guid MovieId,
        IReadOnlyList<TrailerInfo> Queue,
        int CurrentIndex,
        CancellationTokenSource TimerCts);

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

        // A trailer stopped explicitly (user pressed Back or Next).
        // Wait 2.5 s: if PlaybackStart fires for the next item in that window the client
        // self-advanced (Next was pressed) and state already updated → do nothing.
        // If nothing fires → user pressed Back → cancel timers.
        var stoppedId = e.Item.Id;
        _ = Task.Run(async () =>
        {
            await Task.Delay(2500).ConfigureAwait(false);
            lock (_lock)
            {
                if (!_sessions.TryGetValue(sessionId, out var state)) return;
                if (state.Queue[state.CurrentIndex].JellyfinItemId != stoppedId) return; // already advanced
                // Still on the stopped item → user pressed Back
                state.TimerCts.Cancel();
                _sessions.Remove(sessionId);
                _logger.LogInformation("TrailerCinema: user pressed Back in session {S} — aborting.", sessionId);
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
                // Movie starting → cancel any pending trailer timer.
                if (_sessions.TryGetValue(sessionId, out var old))
                {
                    old.TimerCts.Cancel();
                    _sessions.Remove(sessionId);
                }
                if (!_injectedSessions.Add((sessionId, e.Item.Id))) return; // suppressed
            }
            _ = Task.Run(() => InjectTrailersAsync(e.Session, e.Item));
            return;
        }

        if (e.Item is not Video) return;

        // Client started one of our managed trailers (auto-advance or user pressed Next).
        // Update CurrentIndex and reset the advance timer from the actual start of playback.
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out var state)) return;

            int newIndex = IndexOf(state.Queue, e.Item.Id);
            if (newIndex < 0) return; // not one of our trailers

            state.TimerCts.Cancel();
            var newCts = new CancellationTokenSource();
            _sessions[sessionId] = state with { CurrentIndex = newIndex, TimerCts = newCts };

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
            // Full queue [t1, t2, …, tN, movie] → client shows navigation arrows for manual skip.
            var allIds = trailers.Select(t => t.JellyfinItemId).Append(movie.Id).ToArray();

            lock (_lock)
            {
                _sessions[session.Id] = new SessionState(movie.Id, trailers, 0, cts);
            }

            await _sessionManager.SendPlaystateCommand(
                session.Id, session.Id,
                new PlaystateRequest { Command = PlaystateCommand.Stop },
                CancellationToken.None).ConfigureAwait(false);

            await Task.Delay(800).ConfigureAwait(false);

            await _sessionManager.SendPlayCommand(
                session.Id, session.Id,
                new PlayRequest { PlayCommand = PlayCommand.PlayNow, ItemIds = allIds, StartPositionTicks = 0 },
                CancellationToken.None).ConfigureAwait(false);

            // Bootstrap timer for the first trailer.
            // OnPlaybackStart will cancel this and restart it once t1 actually begins playing.
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

    // ── Timer-based advance (PlayNow remaining queue) ────────────────────────
    //
    // Fire TV / Android TV often does not report PlaybackStopped when a video
    // ends naturally (it sticks on the last frame). NextTrack is also unreliable
    // on those clients. So after duration + buffer, we send PlayNow with the
    // remaining items so the server drives queue progression.

    private void ScheduleAdvance(
        string sessionId,
        int expectedIndex,
        IReadOnlyList<TrailerInfo> queue,
        Guid movieId,
        long durationMs,
        CancellationToken ct)
    {
        // Fallback when ffprobe couldn't measure duration: use MaxDurationSeconds from config.
        var fallbackMs = (long)((Plugin.Instance?.Configuration.MaxDurationSeconds ?? 300) * 1000 + 15_000);
        var delayMs    = durationMs > 0 ? durationMs + 12_000 : fallbackMs;

        if (durationMs == 0)
            _logger.LogWarning(
                "TrailerCinema: DurationMs=0 for trailer at index {I} — using {D}s fallback. " +
                "Run a manual Refresh to populate durations.",
                expectedIndex, delayMs / 1000);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay((int)Math.Min(delayMs, int.MaxValue), ct).ConfigureAwait(false);

                // Build the remaining queue (trailers after expectedIndex + movie).
                // Sending PlayNow [remaining] is reliable on Fire TV; NextTrack is not.
                Guid[]? remaining = null;
                lock (_lock)
                {
                    if (!_sessions.TryGetValue(sessionId, out var state)) return;
                    if (state.CurrentIndex != expectedIndex) return; // already advanced by OnPlaybackStart

                    remaining = queue
                        .Skip(expectedIndex + 1)
                        .Select(t => t.JellyfinItemId)
                        .Append(movieId)
                        .ToArray();
                }

                if (remaining is null) return;

                _logger.LogInformation(
                    "TrailerCinema: timer advance from index {I} — sending PlayNow [{R} item(s)] in session {S}.",
                    expectedIndex, remaining.Length, sessionId);

                await _sessionManager.SendPlayCommand(
                    sessionId, sessionId,
                    new PlayRequest { PlayCommand = PlayCommand.PlayNow, ItemIds = remaining, StartPositionTicks = 0 },
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* OnPlaybackStart reset the timer or session ended */ }
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
