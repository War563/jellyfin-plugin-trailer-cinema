using JellyfinTrailerPlugin.Services;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace JellyfinTrailerPlugin.Api;

[ApiController]
[Route("TrailerCinema")]
[Produces("application/json")]
public class TrailerController : ControllerBase
{
    private readonly TrailerCacheService _cache;
    private readonly ILogger<TrailerController> _logger;

    public TrailerController(TrailerCacheService cache, ILogger<TrailerController> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    /// <summary>Returns the current status of the trailer pool.</summary>
    [HttpGet("Status")]
    [Authorize(Policy = "DefaultAuthorization")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetStatus()
    {
        return Ok(new
        {
            PoolCount = _cache.PoolCount,
            LastRefresh = _cache.LastRefresh,
            IsConfigured = !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.ApiKey)
        });
    }

    /// <summary>Returns all cached trailers (titles + resolved status).</summary>
    [HttpGet("Trailers")]
    [Authorize(Policy = "DefaultAuthorization")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<object>> GetTrailers()
    {
        var trailers = _cache.GetAllCached().Select(t => new
        {
            t.VideoId,
            t.Title,
            HasUrl = !string.IsNullOrEmpty(t.StreamUrl),
            t.IsExpired,
            ResolvedAt = t.ResolvedAt == DateTime.MinValue ? (DateTime?)null : t.ResolvedAt
        });

        return Ok(trailers);
    }

    /// <summary>Triggers an immediate pool refresh.</summary>
    [HttpPost("Refresh")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public ActionResult TriggerRefresh()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
            return Problem("Plugin not initialised.");

        _ = Task.Run(() => _cache.RefreshAsync(config, CancellationToken.None));

        return Accepted(new { Message = "Pool refresh started." });
    }
}
