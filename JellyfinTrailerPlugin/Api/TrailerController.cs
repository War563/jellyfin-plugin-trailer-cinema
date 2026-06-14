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
    private readonly YtDlpService _ytDlp;

    public TrailerController(TrailerCacheService cache, YtDlpService ytDlp)
    {
        _cache = cache;
        _ytDlp = ytDlp;
    }

    /// <summary>
    /// Proxy endpoint: redirects to the actual yt-dlp stream URL for a trailer.
    /// Video library items use this URL as their Path so Jellyfin can play them.
    /// </summary>
    [HttpGet("Stream/{videoId}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> StreamTrailer(string videoId, CancellationToken cancellationToken)
    {
        var trailer = _cache.GetAllCached().FirstOrDefault(t => t.VideoId == videoId);
        if (trailer is null)
            return NotFound();

        var url = trailer.StreamUrl;
        if (string.IsNullOrEmpty(url) || trailer.IsExpired)
            url = await _ytDlp.ResolveOneAsync(videoId, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrEmpty(url))
            return NotFound();

        return Redirect(url);
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
