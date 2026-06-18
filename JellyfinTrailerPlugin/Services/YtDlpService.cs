using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace JellyfinTrailerPlugin.Services;

public class YtDlpService
{
    private readonly ILogger<YtDlpService> _logger;
    private readonly IApplicationPaths _appPaths;

    // 3 concurrent downloads to avoid hammering YouTube/network.
    private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(3, 3);
    private readonly SemaphoreSlim _initLock = new SemaphoreSlim(1, 1);
    private volatile string? _resolvedPath;

    public YtDlpService(ILogger<YtDlpService> logger, IApplicationPaths appPaths)
    {
        _logger = logger;
        _appPaths = appPaths;
    }

    /// <summary>
    /// Downloads each trailer as a merged 1080p mp4 to <paramref name="downloadDir"/>.
    /// Skips trailers whose file already exists. Sets LocalPath and DownloadedAt on success.
    /// </summary>
    public async Task DownloadTrailersAsync(IEnumerable<TrailerInfo> trailers, string downloadDir, CancellationToken ct)
    {
        Directory.CreateDirectory(downloadDir);
        var tasks = trailers.Select(t => DownloadOneAsync(t, downloadDir, ct));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task DownloadOneAsync(TrailerInfo trailer, string downloadDir, CancellationToken ct)
    {
        var outputPath = Path.Combine(downloadDir, $"{trailer.VideoId}.mp4");

        if (File.Exists(outputPath))
        {
            trailer.LocalPath = outputPath;
            if (trailer.DownloadedAt == DateTime.MinValue)
                trailer.DownloadedAt = File.GetLastWriteTimeUtc(outputPath);
            return;
        }

        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Check again inside semaphore in case another task downloaded it.
            if (File.Exists(outputPath))
            {
                trailer.LocalPath = outputPath;
                trailer.DownloadedAt = File.GetLastWriteTimeUtc(outputPath);
                return;
            }

            await RunDownloadAsync(trailer.VideoId, outputPath, ct).ConfigureAwait(false);

            if (File.Exists(outputPath))
            {
                trailer.LocalPath = outputPath;
                trailer.DownloadedAt = DateTime.UtcNow;
                _logger.LogInformation("TrailerCinema: downloaded '{Title}' ({Id}).", trailer.Title, trailer.VideoId);
            }
            else
            {
                _logger.LogWarning("TrailerCinema: download produced no output for {Id}.", trailer.VideoId);
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task RunDownloadAsync(string videoId, string outputPath, CancellationToken ct)
    {
        string ytDlpPath;
        try
        {
            ytDlpPath = await GetYtDlpPathAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TrailerCinema: could not locate or download yt-dlp.");
            return;
        }

        // Try to locate ffmpeg so yt-dlp can merge video+audio streams.
        var ffmpegArg = GetFfmpegLocationArg();

        // Format priority:
        //   1. 1080p mp4 video + m4a audio (separate streams, merged by yt-dlp/ffmpeg)
        //   2. Best video ≤1080p + any audio
        //   3. Single-stream best available
        var format = "bestvideo[height<=1080][ext=mp4]+bestaudio[ext=m4a]/bestvideo[height<=1080]+bestaudio/best";
        var args = $"-f \"{format}\" --merge-output-format mp4 {ffmpegArg} --no-playlist -o \"{outputPath}\" \"https://www.youtube.com/watch?v={videoId}\"";

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
            _logger.LogDebug("TrailerCinema: downloading {Id} with args: {Args}", videoId, args);
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Could not start yt-dlp process.");

            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var error = (await errorTask).Trim();

            if (process.ExitCode != 0)
                _logger.LogWarning("TrailerCinema: yt-dlp exited {Code} for {Id}: {Error}",
                    process.ExitCode, videoId, error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TrailerCinema: yt-dlp download failed for {Id}.", videoId);
        }
    }

    private static string GetFfmpegLocationArg()
    {
        // Explicit override in plugin config wins.
        var configured = Plugin.Instance?.Configuration.FfmpegPath?.Trim();
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
            return $"--ffmpeg-location \"{configured}\"";

        // Common Jellyfin-FFmpeg paths (Linux).
        string[] candidates =
        [
            "/usr/lib/jellyfin-ffmpeg/ffmpeg",
            "/usr/local/bin/ffmpeg",
            "/usr/bin/ffmpeg",
        ];

        foreach (var c in candidates)
        {
            if (File.Exists(c))
                return $"--ffmpeg-location \"{c}\"";
        }

        // Let yt-dlp find ffmpeg in PATH (Windows or custom installations).
        return string.Empty;
    }

    private async Task<string> GetYtDlpPathAsync(CancellationToken ct)
    {
        var configured = Plugin.Instance?.Configuration.YtDlpPath?.Trim();
        if (!string.IsNullOrEmpty(configured))
            return configured;

        if (_resolvedPath is not null)
            return _resolvedPath;

        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_resolvedPath is not null)
                return _resolvedPath;

            _resolvedPath = await LocateOrDownloadAsync(ct).ConfigureAwait(false);
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
        await DownloadBinaryAsync(local, ct).ConfigureAwait(false);
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

    private async Task DownloadBinaryAsync(string dest, CancellationToken ct)
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
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var fs = File.Create(dest);
        await response.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
        fs.Close();

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            File.SetUnixFileMode(dest,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        _logger.LogInformation("TrailerCinema: yt-dlp downloaded to {Path}.", dest);
    }
}
