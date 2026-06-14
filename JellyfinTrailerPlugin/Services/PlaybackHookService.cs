using JellyfinTrailerPlugin.Channels;
using MediaBrowser.Controller.Channels;
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
    private readonly ILibraryManager _libraryManager;
    private readonly TrailerCacheService _cache;
    private readonly TrailerChannel _channel;
    private readonly ILogger<PlaybackHookService> _logger;

    // Movies we queued after trailers: skip re-injection when they start playing.
    private readonly HashSet<Guid> _suppressedMovies = new HashSet<Guid>();
    private readonly object _lock = new();

    public PlaybackHookService(
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        TrailerCacheService cache,
        TrailerChannel channel,
        ILogger<PlaybackHookService> logger)
    {
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _cache          = cache;
        _channel        = channel;
        _logger         = logger;

        _sessionManager.PlaybackStart += OnPlaybackStart;
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        if (e.Item is not Movie) return;

        lock (_lock)
        {
            // Movie was queued by us after trailers — let it play untouched.
            if (_suppressedMovies.Remove(e.Item.Id))
                return;
        }

        _ = Task.Run(() => InjectTrailersAsync(e.Session, e.Item));
    }

    private async Task InjectTrailersAsync(SessionInfo session, BaseItem movie)
    {
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null) return;

            var trailers = await _cache.GetTrailersAsync(config, config.TrailerCount, CancellationToken.None)
                .ConfigureAwait(false);

            if (trailers.Count == 0)
            {
                _logger.LogInformation("TrailerCinema: pool empty — skipping injection for session {S}.", session.Id);
                return;
            }

            // Compute channel item IDs and verify they exist in the DB.
            // Items are created on first Jellyfin startup or when the channel is browsed.
            var trailerIds = trailers
                .Select(t => _channel.GetJellyfinItemId(t.VideoId))
                .Where(id => _libraryManager.GetItemById(id) != null)
                .ToList();

            if (trailerIds.Count == 0)
            {
                _logger.LogWarning(
                    "TrailerCinema: channel items not in DB yet. " +
                    "Open Channels → Trailer Cinema in Jellyfin once to initialize them.");
                return;
            }

            // Mark the movie suppressed so when it plays after trailers we don't loop.
            lock (_lock)
            {
                _suppressedMovies.Add(movie.Id);
            }

            // Stop what is currently playing
            await _sessionManager.SendPlaystateCommand(
                session.Id,
                session.Id,
                new PlaystateRequest { Command = PlaystateCommand.Stop },
                CancellationToken.None).ConfigureAwait(false);

            await Task.Delay(800).ConfigureAwait(false);

            // Single PlayNow with full playlist: trailers + movie
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

            // Remove suppression so the movie plays normally if we failed
            lock (_lock)
            {
                _suppressedMovies.Remove(movie.Id);
            }
        }
    }

    public void Dispose()
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        GC.SuppressFinalize(this);
    }
}
