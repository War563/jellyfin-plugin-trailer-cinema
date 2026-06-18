using JellyfinTrailerPlugin.Services;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace JellyfinTrailerPlugin.Channels;

public class TrailerChannel : IChannel, IRequiresMediaInfoCallback
{
    private readonly TrailerCacheService _cache;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<TrailerChannel> _logger;

    public TrailerChannel(
        TrailerCacheService cache,
        ILibraryManager libraryManager,
        ILogger<TrailerChannel> logger)
    {
        _cache          = cache;
        _libraryManager = libraryManager;
        _logger         = logger;
    }

    public string Name => "Trailer Cinema";
    public string Description => "Pool de trailers para reproducir antes de cada película.";
    public string DataVersion => "3";
    public string HomePageUrl => string.Empty;
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;
    public bool IsEnabledFor(string userId) => true;

    public InternalChannelFeatures GetChannelFeatures() => new InternalChannelFeatures
    {
        ContentTypes = new List<ChannelMediaContentType> { ChannelMediaContentType.Movie },
        MediaTypes   = new List<ChannelMediaType>        { ChannelMediaType.Video }
    };

    public IEnumerable<ImageType> GetSupportedChannelImages()
        => Enumerable.Empty<ImageType>();

    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
        => Task.FromResult(new DynamicImageResponse { HasImage = false });

    public Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        var items = _cache.GetAllCached()
            .Where(t => t.IsReady)
            .Select(t => new ChannelItemInfo
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

    public Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(
        string id, CancellationToken cancellationToken)
    {
        var trailer = _cache.GetAllCached().FirstOrDefault(t => t.VideoId == id);
        if (trailer is null || !trailer.IsReady)
        {
            _logger.LogWarning("TrailerCinema channel: no ready file for {VideoId}.", id);
            return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());
        }

        return Task.FromResult<IEnumerable<MediaSourceInfo>>(new[]
        {
            new MediaSourceInfo
            {
                Id                   = id,
                Path                 = trailer.LocalPath,
                Protocol             = MediaProtocol.File,
                IsRemote             = false,
                Name                 = trailer.Title,
                SupportsDirectPlay   = true,
                SupportsDirectStream = true,
                SupportsTranscoding  = true
            }
        });
    }

    public Guid GetJellyfinItemId(string videoId)
    {
        var channelId = _libraryManager.GetNewItemId("Channel" + Name, typeof(Channel));
        return _libraryManager.GetNewItemId(
            channelId.ToString("N") + videoId,
            typeof(MediaBrowser.Controller.Entities.Video));
    }
}
