using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace JellyfinTrailerPlugin.Services;

/// <summary>
/// Creates and maintains virtual Video items in Jellyfin's library database,
/// one per cached trailer. Each item's path points to the plugin's proxy
/// endpoint which redirects to the actual yt-dlp stream URL.
/// This gives each trailer a real Jellyfin Guid usable in PlayRequest.ItemIds.
/// </summary>
public class TrailerLibraryService
{
    // Stable folder Guid — deterministic so it survives restarts.
    private static readonly Guid FolderGuid = new Guid("c1a2b3d4-e5f6-7890-abcd-ef1234567890");

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<TrailerLibraryService> _logger;

    public TrailerLibraryService(ILibraryManager libraryManager, ILogger<TrailerLibraryService> logger)
    {
        _libraryManager = libraryManager;
        _logger         = logger;
    }

    /// <summary>
    /// Returns the deterministic Jellyfin Guid for a given YouTube video ID.
    /// The ID is the same every run so existing DB entries are reused.
    /// </summary>
    public Guid GetItemId(string videoId)
        => _libraryManager.GetNewItemId("TrailerCinema:" + videoId, typeof(Video));

    /// <summary>
    /// Ensures the virtual pool folder and one Video item per trailer exist in the
    /// Jellyfin database. Sets <see cref="TrailerInfo.JellyfinItemId"/> on each entry.
    /// </summary>
    public void SyncItems(IReadOnlyList<TrailerInfo> trailers, string serverBaseUrl)
    {
        var folder = EnsureFolder();

        foreach (var trailer in trailers)
        {
            var itemId   = GetItemId(trailer.VideoId);
            trailer.JellyfinItemId = itemId;

            if (_libraryManager.GetItemById(itemId) is not null)
                continue;   // already in DB

            var proxyUrl = $"{serverBaseUrl.TrimEnd('/')}/TrailerCinema/Stream/{trailer.VideoId}";

            var video = new Video
            {
                Id            = itemId,
                Name          = trailer.Title,
                Path          = proxyUrl,
                IsVirtualItem = true,
                DateCreated   = DateTime.UtcNow,
                DateModified  = DateTime.UtcNow,
                Container     = "mp4"
            };

            _libraryManager.CreateItem(video, folder);
            _logger.LogDebug("TrailerCinema: created library item {Id} for {VideoId}.", itemId, trailer.VideoId);
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
