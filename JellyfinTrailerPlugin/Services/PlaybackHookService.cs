using JellyfinTrailerPlugin.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace JellyfinTrailerPlugin.Services;

/// <summary>
/// Intercepts playback start events. When a movie begins, we build a playlist
/// of trailer items followed by the original movie and send it to the client.
///
/// Jellyfin's session API accepts a PlayRequest with a list of ItemIds, but those
/// must be items that exist in the library. For external URLs we instead use
/// SendPlaystateCommand with a MediaSourceInfo override approach via the
/// PlaybackStart flow — the cleanest supported path is to queue external items
/// as remote media sources attached to transient BaseItem instances and enqueue
/// them via ISessionManager.SendGeneralCommand / SendPlayCommand.
///
/// Practical approach that actually works:
/// 1. Build a list of MediaSourceInfo for each trailer stream URL.
/// 2. Use ISessionManager.SendPlayCommand with the movie's item ID but
///    prepend the trailer queue through the client's QueueableMediaType.
///
/// Because Jellyfin clients only play items from the server library by itemId,
/// the most reliable cross-client approach is to create transient Video items,
/// register them temporarily so the server can serve them, and include their
/// fake IDs in the play request queue.
///
/// We use the simpler "GeneralCommand → PlayNow with ExternalUrl" approach
/// supported in Jellyfin 10.8+: sending individual Play commands per trailer
/// in sequence, followed by the movie. This works for clients that honour
/// the Queue/PlayNext commands (Jellyfin Web, Android TV, iOS app).
/// </summary>
public class PlaybackHookService : IDisposable
{
    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly TrailerCacheService _cache;
    private readonly ILogger<PlaybackHookService> _logger;

    // Prevent re-entrant trailer injection when the trailer itself fires PlaybackStart
    private readonly HashSet<string> _activeSessions = new HashSet<string>();
    private readonly object _lockObj = new();

    public PlaybackHookService(
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        TrailerCacheService cache,
        ILogger<PlaybackHookService> logger)
    {
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _cache = cache;
        _logger = logger;

        _sessionManager.PlaybackStart += OnPlaybackStart;
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        // Only intercept movies
        if (e.Item?.GetType().Name != "Movie")
            return;

        // Ignore if this session is already in trailer mode
        lock (_lockObj)
        {
            if (!_activeSessions.Add(e.Session.Id))
                return;
        }

        // Fire-and-forget; we don't want to block the playback pipeline
        _ = Task.Run(() => InjectTrailersAsync(e.Session, e.Item, e.PlaySessionId));
    }

    private async Task InjectTrailersAsync(SessionInfo session, BaseItem movie, string? playSessionId)
    {
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null) return;

            var trailers = await _cache.GetTrailersAsync(config, config.TrailerCount, CancellationToken.None);
            if (trailers.Count == 0)
            {
                _logger.LogInformation("TrailerCinema: no trailers available for session {Session}.", session.Id);
                return;
            }

            // Step 1: Stop current playback so we can replace the queue
            await _sessionManager.SendPlaystateCommand(
                session.Id,
                session.Id,
                new PlaystateRequest { Command = PlaystateCommand.Stop },
                CancellationToken.None);

            // Small delay so the client processes the stop
            await Task.Delay(800);

            // Step 2: Play trailers one by one using external URL play command.
            // We send the first trailer as PlayNow, the rest as PlayNext (queued).
            bool first = true;
            foreach (var trailer in trailers)
            {
                var playRequest = new PlayRequest
                {
                    PlayCommand = first ? PlayCommand.PlayNow : PlayCommand.PlayNext,
                    // ExternalMediaSourceId signals to clients this is a direct stream URL
                    MediaSourceId = trailer.StreamUrl,
                    StartPositionTicks = 0
                };

                await _sessionManager.SendPlayCommand(
                    session.Id,
                    session.Id,
                    playRequest,
                    CancellationToken.None);

                first = false;
                await Task.Delay(200);
            }

            // Step 3: Queue the original movie after all trailers
            var moviePlayRequest = new PlayRequest
            {
                PlayCommand = PlayCommand.PlayNext,
                ItemIds = new[] { movie.Id },
                StartPositionTicks = 0
            };

            await _sessionManager.SendPlayCommand(
                session.Id,
                session.Id,
                moviePlayRequest,
                CancellationToken.None);

            _logger.LogInformation(
                "TrailerCinema: queued {Count} trailer(s) before '{Movie}' for session {Session}.",
                trailers.Count, movie.Name, session.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TrailerCinema: error injecting trailers for session {Session}.", session.Id);
        }
        finally
        {
            lock (_lockObj)
            {
                _activeSessions.Remove(session.Id);
            }
        }
    }

    public void Dispose()
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        GC.SuppressFinalize(this);
    }
}
