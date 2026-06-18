using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace JellyfinTrailerPlugin.Services;

/// <summary>
/// Creates and maintains Video items in Jellyfin's library, one per downloaded trailer.
/// Each item's Path points to the local mp4 file so Jellyfin serves it natively.
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
    /// Ensures one Video library item per trailer exists, pointing at the local mp4 file.
    /// Sets <see cref="TrailerInfo.JellyfinItemId"/> on each entry.
    /// </summary>
    public void SyncItems(IReadOnlyList<TrailerInfo> trailers)
    {
        var folder = EnsureFolder();

        foreach (var trailer in trailers)
        {
            var itemId = GetItemId(trailer.VideoId);
            trailer.JellyfinItemId = itemId;

            var existing = _libraryManager.GetItemById(itemId);
            if (existing is not null)
            {
                // If the item already points to the correct local file, nothing to do.
                if (existing.Path == trailer.LocalPath && !existing.IsVirtualItem)
                    continue;

                // Recreate if path changed or it's still a legacy virtual/remote item.
                _libraryManager.DeleteItem(existing, new DeleteOptions { DeleteFileLocation = false }, false);
            }

            var video = new Video
            {
                Id           = itemId,
                Name         = trailer.Title,
                Path         = trailer.LocalPath,  // Real local file → LocationType.FileSystem
                IsVirtualItem = false,
                DateCreated  = trailer.DownloadedAt == DateTime.MinValue ? DateTime.UtcNow : trailer.DownloadedAt,
                DateModified = DateTime.UtcNow,
                Container    = "mp4"
            };

            _libraryManager.CreateItem(video, folder);
            _logger.LogDebug("TrailerCinema: synced library item {Id} → {Path}.", itemId, trailer.LocalPath);
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
