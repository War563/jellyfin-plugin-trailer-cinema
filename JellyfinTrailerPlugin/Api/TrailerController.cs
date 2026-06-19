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

    /// <summary>
    /// Serves the pre-concatenated MP4 containing all selected trailers.
    /// This is the item played before movies: VLC receives one file and plays all trailers in sequence.
    /// Route must be declared before Stream/{videoId} so it takes precedence over the wildcard.
    /// </summary>
    [HttpGet("Stream/combined")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult StreamCombined()
    {
        var path = _cache.GetCombinedPath();
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            return NotFound();

        return PhysicalFile(path, "video/mp4", enableRangeProcessing: true);
    }

    /// <summary>
    /// Serves the local mp4 file for a single trailer (individual items in the library catalog).
    /// </summary>
    [HttpGet("Stream/{videoId}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult StreamTrailer(string videoId)
    {
        var trailer = _cache.GetAllCached().FirstOrDefault(t => t.VideoId == videoId);
        if (trailer is null || !trailer.IsReady)
            return NotFound();

        return PhysicalFile(trailer.LocalPath, "video/mp4", enableRangeProcessing: true);
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
            t.IsReady,
            t.LocalPath,
            Downloaded = t.DownloadedAt == DateTime.MinValue ? (DateTime?)null : t.DownloadedAt
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
