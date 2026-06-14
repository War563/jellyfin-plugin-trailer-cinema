using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace JellyfinTrailerPlugin.Services;

public class PlaybackHookService : IDisposable
{
    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly TrailerCacheService _cache;
    private readonly ILogger<PlaybackHookService> _logger;

    // Tracks (sessionId, movieId) pairs that already received trailer injection.
    // Keyed in OnPlaybackStart (before Task.Run) to prevent double-injection races.
    // Cleared on injection failure so the user can retry.
    private readonly HashSet<(string, Guid)> _injectedSessions = new();
    private readonly object _lock = new();

    public PlaybackHookService(
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        TrailerCacheService cache,
        ILogger<PlaybackHookService> logger)
    {
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _cache          = cache;
        _logger         = logger;

        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.SessionEnded  += OnSessionEnded;
    }

    private void OnSessionEnded(object? sender, SessionEventArgs e)
    {
        // Clean up entries for the ended session to avoid unbounded growth.
        lock (_lock)
        {
            _injectedSessions.RemoveWhere(k => k.Item1 == e.SessionInfo.Id);
        }
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        if (e.Item is not Movie) return;

        lock (_lock)
        {
            // Already injected trailers for this movie in this session — suppress.
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

            var trailers = await _cache.GetTrailersAsync(config, config.TrailerCount, CancellationToken.None)
                .ConfigureAwait(false);

            if (trailers.Count == 0)
            {
                _logger.LogInformation("TrailerCinema: pool empty — skipping for session {S}.", session.Id);
                return;
            }

            // Only use trailers whose library item was created successfully
            var trailerIds = trailers
                .Where(t => t.JellyfinItemId != Guid.Empty
                         && _libraryManager.GetItemById(t.JellyfinItemId) is not null)
                .Select(t => t.JellyfinItemId)
                .ToList();

            if (trailerIds.Count == 0)
            {
                _logger.LogWarning("TrailerCinema: no library items found in DB yet — waiting for next refresh.");
                return;
            }

            // Stop current playback
            await _sessionManager.SendPlaystateCommand(
                session.Id,
                session.Id,
                new PlaystateRequest { Command = PlaystateCommand.Stop },
                CancellationToken.None).ConfigureAwait(false);

            await Task.Delay(800).ConfigureAwait(false);

            // Single PlayNow: all trailers followed by the original movie
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

            // Allow movie to play normally if injection failed
            lock (_lock)
            {
                _injectedSessions.Remove((session.Id, movie.Id));
            }
        }
    }

    public void Dispose()
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.SessionEnded  -= OnSessionEnded;
        GC.SuppressFinalize(this);
    }
}
