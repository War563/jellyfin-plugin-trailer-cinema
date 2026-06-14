using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace JellyfinTrailerPlugin.Services;

public class YtDlpService
{
    private readonly ILogger<YtDlpService> _logger;
    private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(3, 3);

    public YtDlpService(ILogger<YtDlpService> logger)
    {
        _logger = logger;
    }

    public async Task ResolveUrlsAsync(IEnumerable<TrailerInfo> trailers, CancellationToken ct)
    {
        var tasks = trailers.Select(t => ResolveOneAsync(t, ct));
        await Task.WhenAll(tasks);
    }

    /// <summary>Resolves the direct stream URL for a single video ID.</summary>
    public Task<string?> ResolveOneAsync(string videoId, CancellationToken ct)
        => RunYtDlpAsync(videoId, ct);

    private async Task ResolveOneAsync(TrailerInfo trailer, CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            var url = await RunYtDlpAsync(trailer.VideoId, ct);
            if (!string.IsNullOrEmpty(url))
            {
                trailer.StreamUrl = url;
                trailer.ResolvedAt = DateTime.UtcNow;
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<string?> RunYtDlpAsync(string videoId, CancellationToken ct)
    {
        var args = $"-f \"bestvideo[ext=mp4]+bestaudio[ext=m4a]/best[ext=mp4]/best\" -g --no-playlist \"https://www.youtube.com/watch?v={videoId}\"";

        var psi = new ProcessStartInfo
        {
            FileName = "yt-dlp",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Could not start yt-dlp process.");

            // .NET 6: ReadToEndAsync() has no CancellationToken overload
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync(ct);
            var output = (await outputTask).Trim();
            var error = (await errorTask).Trim();

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("TrailerCinema: yt-dlp exited with code {Code} for {Id}: {Error}",
                    process.ExitCode, videoId, error);
                return null;
            }

            var firstUrl = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

            if (firstUrl != null)
                _logger.LogDebug("TrailerCinema: resolved {Id} → {Url}", videoId, firstUrl[..Math.Min(80, firstUrl.Length)]);

            return firstUrl;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TrailerCinema: yt-dlp failed for {Id}.", videoId);
            return null;
        }
    }
}
