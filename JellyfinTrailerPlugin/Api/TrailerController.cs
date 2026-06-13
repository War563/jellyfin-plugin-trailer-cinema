using JellyfinTrailerPlugin.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace JellyfinTrailerPlugin.Api;

[ApiController]
[Route("TrailerCinema")]
[Produces("application/json")]
public class TrailerController : ControllerBase
{
    private readonly TrailerCacheService _cache;

    public TrailerController(TrailerCacheService cache)
    {
        _cache = cache;
    }

    [HttpGet("Status")]
    [Authorize(Policy = "DefaultAuthorization")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetStatus() => Ok(new
    {
        PoolCount    = _cache.PoolCount,
        LastRefresh  = _cache.LastRefresh,
        IsConfigured = !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.ApiKey)
    });

    [HttpGet("Trailers")]
    [Authorize(Policy = "DefaultAuthorization")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<object>> GetTrailers() => Ok(
        _cache.GetAllCached().Select(t => new
        {
            t.VideoId,
            t.Title,
            HasUrl     = !string.IsNullOrEmpty(t.StreamUrl),
            t.IsExpired,
            ResolvedAt = t.ResolvedAt == DateTime.MinValue ? (DateTime?)null : t.ResolvedAt
        }));

    [HttpPost("Refresh")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public ActionResult TriggerRefresh()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null) return Problem("Plugin not initialised.");

        _ = Task.Run(() => _cache.RefreshAsync(config, CancellationToken.None));
        return Accepted(new { Message = "Pool refresh started." });
    }
}
