using JellyfinTrailerPlugin.Configuration;
using Microsoft.Extensions.Logging;
using System.Xml;
using GoogleYouTubeService = Google.Apis.YouTube.v3.YouTubeService;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;

namespace JellyfinTrailerPlugin.Services;

public class YouTubeService
{
    private readonly ILogger<YouTubeService> _logger;

    public YouTubeService(ILogger<YouTubeService> logger)
    {
        _logger = logger;
    }

    public async Task<List<TrailerInfo>> GetTrailersAsync(PluginConfiguration config, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey) || string.IsNullOrWhiteSpace(config.YouTubeChannelId))
        {
            _logger.LogWarning("TrailerCinema: API key or channel ID not configured.");
            return new List<TrailerInfo>();
        }

        var api = new GoogleYouTubeService(new BaseClientService.Initializer
        {
            ApiKey = config.ApiKey,
            ApplicationName = "JellyfinTrailerCinema"
        });

        var results = new List<TrailerInfo>();

        try
        {
            // Step 1: search videos in channel
            var searchRequest = api.Search.List("snippet");
            searchRequest.ChannelId = config.YouTubeChannelId;
            searchRequest.Type = "video";
            searchRequest.Order = SearchResource.ListRequest.OrderEnum.Date;
            searchRequest.MaxResults = Math.Min(config.PoolSize * 3, 50);
            searchRequest.Q = config.TitleFilter;

            var searchResponse = await searchRequest.ExecuteAsync(ct);

            var videoIds = new List<string>();
            foreach (var item in searchResponse.Items)
            {
                var id = item.Id?.VideoId;
                if (!string.IsNullOrEmpty(id))
                    videoIds.Add(id);
            }

            if (videoIds.Count == 0)
                return results;

            // Step 2: get video details (duration)
            var videoRequest = api.Videos.List("contentDetails,snippet");
            videoRequest.Id = string.Join(",", videoIds);
            var videoResponse = await videoRequest.ExecuteAsync(ct);

            var excludeWords = (config.TitleExclude ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var video in videoResponse.Items)
            {
                var title = video.Snippet?.Title ?? string.Empty;

                if (!title.Contains(config.TitleFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (excludeWords.Any(w => title.Contains(w, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var duration = ParseIso8601Duration(video.ContentDetails?.Duration);
                if (duration < config.MinDurationSeconds || duration > config.MaxDurationSeconds)
                    continue;

                results.Add(new TrailerInfo
                {
                    VideoId = video.Id,
                    Title = title
                });

                if (results.Count >= config.PoolSize)
                    break;
            }

            _logger.LogInformation("TrailerCinema: fetched {Count} trailers from YouTube.", results.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TrailerCinema: error fetching trailers from YouTube.");
        }

        return results;
    }

    private static int ParseIso8601Duration(string? iso)
    {
        if (string.IsNullOrEmpty(iso))
            return 0;

        try
        {
            return (int)XmlConvert.ToTimeSpan(iso).TotalSeconds;
        }
        catch
        {
            return 0;
        }
    }
}
