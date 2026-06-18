using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace JellyfinTrailerPlugin.Services;

/// <summary>
/// Creates and maintains video items in Jellyfin's library database, one per cached trailer.
/// Each item's path points to the plugin's proxy endpoint which redirects to the yt-dlp stream URL.
/// Items are stored as non-virtual remote items so Jellyfin clients can queue and play them.
/// </summary>
public class TrailerLibraryService
{
    // Stable folder Guid — deterministic so it survives restarts.
    private static readonly Guid FolderGuid = new Guid("c1a2b3d4-e5f6-7890-abcd-ef1234567890");

    private readonly ILibraryManager _libraryManager;
    private readonly IItemRepository _itemRepository;
    private readonly ILogger<TrailerLibraryService> _logger;

    public TrailerLibraryService(
        ILibraryManager libraryManager,
        IItemRepository itemRepository,
        ILogger<TrailerLibraryService> logger)
    {
        _libraryManager = libraryManager;
        _itemRepository = itemRepository;
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
            var itemId = GetItemId(trailer.VideoId);
            trailer.JellyfinItemId = itemId;

            var existing = _libraryManager.GetItemById(itemId);
            if (existing is not null)
            {
                if (!existing.IsVirtualItem)
                    continue; // Already created correctly as a remote item.

                // Migrate old virtual item: virtual items have no MediaSources so clients skip them.
                // Delete and recreate as a non-virtual remote item.
                _libraryManager.DeleteItem(existing, new DeleteOptions { DeleteFileLocation = false }, false);
            }

            var proxyUrl = $"{serverBaseUrl.TrimEnd('/')}/TrailerCinema/Stream/{trailer.VideoId}";

            var video = new Video
            {
                Id            = itemId,
                Name          = trailer.Title,
                Path          = proxyUrl,
                IsVirtualItem = false,  // Must be false — virtual items get no MediaSources and are skipped by clients.
                DateCreated   = DateTime.UtcNow,
                DateModified  = DateTime.UtcNow,
                Container     = "mp4"
            };

            _libraryManager.CreateItem(video, folder);

            // Store stub stream info so Jellyfin builds a valid MediaSource without probing the URL.
            // The real stream is resolved at play time via the proxy → yt-dlp redirect.
            _itemRepository.SaveMediaStreams(itemId, new List<MediaStream>
            {
                new() { Type = MediaStreamType.Video, Index = 0, Codec = "h264", Width = 1920, Height = 1080, IsDefault = true },
                new() { Type = MediaStreamType.Audio, Index = 1, Codec = "aac",  Channels = 2, SampleRate = 44100,   IsDefault = true }
            }, CancellationToken.None);

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
