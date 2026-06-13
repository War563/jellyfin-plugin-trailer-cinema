using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace JellyfinTrailerPlugin.Api;

[ApiController]
[Route("TrailerCinema")]
[Produces("application/json")]
public class TrailerController : ControllerBase
{
    [HttpGet("Status")]
    [Authorize(Policy = "DefaultAuthorization")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetStatus()
    {
        var cache = PluginEntryPoint.Current?.Cache;
        return Ok(new
        {
            PoolCount   = cache?.PoolCount ?? 0,
            LastRefresh = cache?.LastRefresh,
            IsConfigured = !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.ApiKey)
        });
    }

    [HttpGet("Trailers")]
    [Authorize(Policy = "DefaultAuthorization")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<object>> GetTrailers()
    {
        var trailers = PluginEntryPoint.Current?.Cache.GetAllCached()
            ?? Enumerable.Empty<Services.TrailerInfo>();

        return Ok(trailers.Select(t => new
        {
            t.VideoId,
            t.Title,
            HasUrl      = !string.IsNullOrEmpty(t.StreamUrl),
            t.IsExpired,
            ResolvedAt  = t.ResolvedAt == DateTime.MinValue ? (DateTime?)null : t.ResolvedAt
        }));
    }

    [HttpPost("Refresh")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public ActionResult TriggerRefresh()
    {
        var config = Plugin.Instance?.Configuration;
        var cache  = PluginEntryPoint.Current?.Cache;

        if (config is null || cache is null)
            return Problem("Plugin not initialised.");

        _ = Task.Run(() => cache.RefreshAsync(config, CancellationToken.None));
        return Accepted(new { Message = "Pool refresh started." });
    }
}
