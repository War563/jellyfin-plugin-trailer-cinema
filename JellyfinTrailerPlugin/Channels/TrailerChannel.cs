using JellyfinTrailerPlugin.Services;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace JellyfinTrailerPlugin.Channels;

/// <summary>
/// Exposes the trailer pool as a Jellyfin channel so each trailer gets a real
/// library item ID that can be used in SendPlayCommand.ItemIds.
/// IRequiresMediaInfoCallback lets us provide a fresh yt-dlp URL on every play
/// request instead of baking potentially-expired URLs into the DB.
/// </summary>
public class TrailerChannel : IChannel, IRequiresMediaInfoCallback
{
    private readonly TrailerCacheService _cache;
    private readonly YtDlpService _ytDlp;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<TrailerChannel> _logger;

    public TrailerChannel(
        TrailerCacheService cache,
        YtDlpService ytDlp,
        ILibraryManager libraryManager,
        ILogger<TrailerChannel> logger)
    {
        _cache = cache;
        _ytDlp = ytDlp;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    // ── IChannel ────────────────────────────────────────────────────────────

    public string Name => "Trailer Cinema";
    public string Description => "Pool de trailers para reproducir antes de cada película.";
    public string DataVersion => "2";
    public string HomePageUrl => string.Empty;
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;
    public bool IsEnabledFor(string userId) => true;

    public InternalChannelFeatures GetChannelFeatures() => new InternalChannelFeatures
    {
        ContentTypes = new[] { ChannelMediaContentType.Movie },
        MediaTypes = new[] { ChannelMediaType.Video }
    };

    public IEnumerable<ImageType> GetSupportedChannelImages()
        => Enumerable.Empty<ImageType>();

    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
        => Task.FromResult(new DynamicImageResponse { HasImage = false });

    public Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        var trailers = _cache.GetAllCached();

        var items = trailers.Select(t => new ChannelItemInfo
        {
            Id          = t.VideoId,
            Name        = t.Title,
            Type        = ChannelItemType.Media,
            ContentType = ChannelMediaContentType.Movie,
            MediaType   = ChannelMediaType.Video
        }).ToList();

        return Task.FromResult(new ChannelItemResult
        {
            Items            = items,
            TotalRecordCount = items.Count
        });
    }

    // ── IRequiresMediaInfoCallback ───────────────────────────────────────────

    public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(
        string id, CancellationToken cancellationToken)
    {
        var trailer = _cache.GetAllCached().FirstOrDefault(t => t.VideoId == id);
        if (trailer is null) return Enumerable.Empty<MediaSourceInfo>();

        var url = trailer.StreamUrl;
        if (string.IsNullOrEmpty(url) || trailer.IsExpired)
        {
            _logger.LogDebug("TrailerCinema: renewing stream URL for {VideoId}.", id);
            url = await _ytDlp.ResolveOneAsync(id, cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrEmpty(url)) return Enumerable.Empty<MediaSourceInfo>();

        return new[]
        {
            new MediaSourceInfo
            {
                Id                  = id,
                Path                = url,
                Protocol            = MediaProtocol.Http,
                IsRemote            = true,
                Name                = trailer.Title,
                SupportsDirectPlay  = true,
                SupportsDirectStream = true,
                SupportsTranscoding = false
            }
        };
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Computes the Jellyfin library Guid for a channel item using the same
    /// deterministic formula that ChannelManager uses internally.
    /// </summary>
    public Guid GetJellyfinItemId(string videoId)
    {
        var channelId = _libraryManager.GetNewItemId(
            "Channel" + Name,
            typeof(Channel));

        return _libraryManager.GetNewItemId(
            channelId.ToString("N") + videoId,
            typeof(MediaBrowser.Controller.Entities.Video));
    }
}
