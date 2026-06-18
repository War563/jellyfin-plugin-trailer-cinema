using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace JellyfinTrailerPlugin.Services;

/// <summary>
/// Creates and maintains Video items in Jellyfin's library, one per downloaded trailer.
/// Items use an HTTP proxy URL as their Path so Jellyfin treats them as Remote items
/// and the client direct-plays them. The proxy endpoint (/TrailerCinema/Stream/{id})
/// serves the locally-downloaded mp4 file, giving 1080p quality with audio.
/// </summary>
public class TrailerLibraryService
{
    private static readonly Guid FolderGuid = new("c1a2b3d4-e5f6-7890-abcd-ef1234567890");

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<TrailerLibraryService> _logger;

    public TrailerLibraryService(ILibraryManager libraryManager, ILogger<TrailerLibraryService> logger)
    {
        _libraryManager = libraryManager;
        _logger         = logger;
    }

    public Guid GetItemId(string videoId)
        => _libraryManager.GetNewItemId("TrailerCinema:" + videoId, typeof(Video));

    /// <summary>
    /// Ensures one Video library item per trailer exists.
    /// The item Path is set to the proxy URL so Jellyfin exposes it as a Remote item
    /// that clients can direct-play (the proxy endpoint serves the local file).
    /// </summary>
    public void SyncItems(IReadOnlyList<TrailerInfo> trailers, string serverBaseUrl)
    {
        var folder = EnsureFolder();

        foreach (var trailer in trailers)
        {
            var itemId = GetItemId(trailer.VideoId);
            trailer.JellyfinItemId = itemId;

            var proxyUrl = $"{serverBaseUrl.TrimEnd('/')}/TrailerCinema/Stream/{trailer.VideoId}";

            var existing = _libraryManager.GetItemById(itemId);
            if (existing is not null)
            {
                // If the item already has the correct proxy URL and is non-virtual, reuse it.
                if (existing.Path == proxyUrl && !existing.IsVirtualItem)
                    continue;

                // Recreate if path or type changed (e.g. old local-file items from v1.1.18).
                _libraryManager.DeleteItem(existing, new DeleteOptions { DeleteFileLocation = false }, false);
            }

            var video = new Video
            {
                Id            = itemId,
                Name          = trailer.Title,
                Path          = proxyUrl,      // HTTP URL → LocationType.Remote → client direct-plays
                IsVirtualItem = false,
                DateCreated   = DateTime.UtcNow,
                DateModified  = DateTime.UtcNow,
                Container     = "mp4"
            };

            _libraryManager.CreateItem(video, folder);
            _logger.LogDebug("TrailerCinema: synced item {Id} → {Url}.", itemId, proxyUrl);
        }
    }

    private Folder EnsureFolder()
    {
        if (_libraryManager.GetItemById(FolderGuid) is Folder existing)
            return existing;

        var folder = new Folder
        {
            Id            = FolderGuid,
            Name          = "Trailer Cinema Pool",
            IsVirtualItem = true,
            DateCreated   = DateTime.UtcNow,
            DateModified  = DateTime.UtcNow,
            Path          = "virtual://TrailerCinema"
        };

        _libraryManager.CreateItem(folder, _libraryManager.RootFolder);
        _logger.LogInformation("TrailerCinema: created pool folder in library.");
        return folder;
    }
}
