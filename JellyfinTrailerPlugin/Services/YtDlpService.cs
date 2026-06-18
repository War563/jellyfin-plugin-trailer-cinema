using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace JellyfinTrailerPlugin.Services;

public class YtDlpService
{
    private readonly ILogger<YtDlpService> _logger;
    private readonly IApplicationPaths _appPaths;
    private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(3, 3);
    private readonly SemaphoreSlim _initLock = new SemaphoreSlim(1, 1);
    private volatile string? _resolvedPath;

    public YtDlpService(ILogger<YtDlpService> logger, IApplicationPaths appPaths)
    {
        _logger = logger;
        _appPaths = appPaths;
    }

    public async Task ResolveUrlsAsync(IEnumerable<TrailerInfo> trailers, CancellationToken ct)
    {
        var tasks = trailers.Select(t => ResolveOneAsync(t, ct));
        await Task.WhenAll(tasks);
    }

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

    private async Task<string> GetYtDlpPathAsync(CancellationToken ct)
    {
        // Manual override wins — always use it without caching.
        var configured = Plugin.Instance?.Configuration.YtDlpPath?.Trim();
        if (!string.IsNullOrEmpty(configured))
            return configured;

        // Auto-detect / auto-download (result cached for lifetime of the service).
        if (_resolvedPath is not null)
            return _resolvedPath;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_resolvedPath is not null)
                return _resolvedPath;

            _resolvedPath = await LocateOrDownloadAsync(ct);
            return _resolvedPath;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task<string> LocateOrDownloadAsync(CancellationToken ct)
    {
        if (IsAvailable("yt-dlp"))
        {
            _logger.LogInformation("TrailerCinema: using system yt-dlp.");
            return "yt-dlp";
        }

        var local = LocalBinaryPath();
        if (File.Exists(local))
        {
            _logger.LogInformation("TrailerCinema: using local yt-dlp at {Path}.", local);
            return local;
        }

        _logger.LogInformation("TrailerCinema: yt-dlp not found — downloading automatically...");
        await DownloadAsync(local, ct);
        return local;
    }

    private string LocalBinaryPath()
    {
        var dir = Path.Combine(_appPaths.DataPath, "ytdlp");
        Directory.CreateDirectory(dir);
        var exe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "yt-dlp.exe" : "yt-dlp";
        return Path.Combine(dir, exe);
    }

    private static bool IsAvailable(string name)
    {
        try
        {
            var check = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where" : "which";
            var psi = new ProcessStartInfo
            {
                FileName = check, Arguments = name,
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    private async Task DownloadAsync(string dest, CancellationToken ct)
    {
        string url;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            url = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            url = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_macos";
        else if (RuntimeInformation.OSArchitecture == Architecture.Arm64)
            url = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux_aarch64";
        else
            url = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux";

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var fs = File.Create(dest);
        await response.Content.CopyToAsync(fs, ct);
        fs.Close();

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            File.SetUnixFileMode(dest,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        _logger.LogInformation("TrailerCinema: yt-dlp downloaded to {Path}.", dest);
    }

    private async Task<string?> RunYtDlpAsync(string videoId, CancellationToken ct)
    {
        string ytDlpPath;
        try
        {
            ytDlpPath = await GetYtDlpPathAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TrailerCinema: could not locate or download yt-dlp.");
            return null;
        }

        // Format 22 = YouTube's 720p mp4 with audio+video combined in one file.
        // "bestvideo+bestaudio" gives TWO separate URLs; taking only the first = video-only, no sound.
        // Fallbacks ensure we always get a single combined stream even if format 22 is unavailable.
        var args = $"-f \"22/best[height>=480][ext=mp4]/best[ext=mp4]/best\" -g --no-playlist \"https://www.youtube.com/watch?v={videoId}\"";

        var psi = new ProcessStartInfo
        {
            FileName = ytDlpPath,
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

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask  = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync(ct);
            var output = (await outputTask).Trim();
            var error  = (await errorTask).Trim();

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("TrailerCinema: yt-dlp exited with code {Code} for {Id}: {Error}",
                    process.ExitCode, videoId, error);
                return null;
            }

            var firstUrl = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

            if (firstUrl != null)
                _logger.LogDebug("TrailerCinema: resolved {Id} → {Url}",
                    videoId, firstUrl[..Math.Min(80, firstUrl.Length)]);

            return firstUrl;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TrailerCinema: yt-dlp failed for {Id}.", videoId);
            return null;
        }
    }
}
