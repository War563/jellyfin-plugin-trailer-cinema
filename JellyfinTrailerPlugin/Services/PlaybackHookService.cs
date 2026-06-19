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
    // We send the full queue [t1 … tN, movie] so the client shows navigation arrows.
    // CurrentIndex = which trailer is currently playing (updated via OnPlaybackStart).
    // TimerCts cancels the NextTrack timer when the client self-advances.
    private sealed record SessionState(
        Guid MovieId,
        IReadOnlyList<TrailerInfo> Queue,
        int CurrentIndex,
        CancellationTokenSource TimerCts);

    private readonly Dictionary<string, SessionState> _sessions = new();
    private readonly object _lock = new();

    private static readonly long ThirtySecondsTicks = TimeSpan.FromSeconds(30).Ticks;
    private const long FallbackTimerMs = 15 * 60 * 1000L; // used when ffprobe couldn't read duration

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
            if (_sessions.TryGetValue(e.SessionInfo.Id, out var s)) { s.TimerCts.Cancel(); _sessions.Remove(e.SessionInfo.Id); }
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

        // A trailer stopped. Wait briefly to distinguish:
        //   - User pressed Next  → PlaybackStart for next item will fire and update state.
        //   - User pressed Back  → nothing fires → clean up (cancel timers).
        var stoppedId = e.Item.Id;
        _ = Task.Run(async () =>
        {
            await Task.Delay(2500).ConfigureAwait(false);
            lock (_lock)
            {
                if (!_sessions.TryGetValue(sessionId, out var state)) return;
                if (state.Queue[state.CurrentIndex].JellyfinItemId != stoppedId) return; // already advanced → OK
                // Still on the stopped item → user pressed Back → abort
                state.TimerCts.Cancel();
                _sessions.Remove(sessionId);
                _logger.LogInformation("TrailerCinema: user stopped trailer in session {S} — aborting.", sessionId);
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
                // Movie starting → cancel any pending trailer timers.
                if (_sessions.TryGetValue(sessionId, out var old))
                {
                    old.TimerCts.Cancel();
                    _sessions.Remove(sessionId);
                }
                if (!_injectedSessions.Add((sessionId, e.Item.Id))) return; // already injected → suppressed
            }
            _ = Task.Run(() => InjectTrailersAsync(e.Session, e.Item));
            return;
        }

        if (e.Item is not Video) return;

        // Update session state when the client starts one of our managed trailers
        // (covers both auto-advance by the client and user pressing Next).
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out var state)) return;

            int newIndex = -1;
            for (int i = 0; i < state.Queue.Count; i++)
            {
                if (state.Queue[i].JellyfinItemId == e.Item.Id) { newIndex = i; break; }
            }
            if (newIndex < 0) return; // not one of our trailers

            // Cancel old timer, start fresh one for the newly playing trailer.
            state.TimerCts.Cancel();
            var newCts = new CancellationTokenSource();
            _sessions[sessionId] = state with { CurrentIndex = newIndex, TimerCts = newCts };

            ScheduleNextTrack(sessionId, state.Queue[newIndex], newCts.Token);
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
                _logger.LogWarning("TrailerCinema: no ready trailers — skipping for '{Movie}'.", movie.Name);
                return;
            }

            // Full queue: all trailers followed by the movie.
            // Sending all items at once makes the client show navigation arrows
            // so the user can press Next/Previous to skip trailers manually.
            var cts    = new CancellationTokenSource();
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

            // Bootstrap timer for the first trailer in case OnPlaybackStart doesn't fire
            // (it will be cancelled and restarted by OnPlaybackStart when t1 actually begins).
            ScheduleNextTrack(session.Id, trailers[0], cts.Token);

            _logger.LogInformation(
                "TrailerCinema: queued {N} trailer(s) + movie '{M}' for session {S}.",
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

    // ── Timer fallback (NextTrack) ───────────────────────────────────────────

    // Schedules a SendPlaystateCommand(NextTrack) after the trailer's duration + buffer.
    // This fires if the client sticks on the last frame and never reports PlaybackStopped,
    // which is a known bug in Jellyfin Android TV / Fire TV with ExoPlayer.
    private void ScheduleNextTrack(string sessionId, TrailerInfo trailer, CancellationToken ct)
    {
        var delayMs = trailer.DurationMs > 0 ? trailer.DurationMs + 12_000 : FallbackTimerMs;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay((int)Math.Min(delayMs, int.MaxValue), ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "TrailerCinema: timer fallback — NextTrack after '{T}' in session {S}.", trailer.Title, sessionId);
                await _sessionManager.SendPlaystateCommand(
                    sessionId, sessionId,
                    new PlaystateRequest { Command = PlaystateCommand.NextTrack },
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* client self-advanced or session ended */ }
        });
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
