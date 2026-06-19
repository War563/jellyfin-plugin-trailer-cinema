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

    /// <summary>Returns the ffmpeg executable path, or null if not found.</summary>
    public string? GetFfmpegPath()
    {
        var configured = Plugin.Instance?.Configuration.FfmpegPath?.Trim();
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
            return configured;

        string[] candidates =
        [
            "/usr/lib/jellyfin-ffmpeg/ffmpeg",
            "/usr/local/bin/ffmpeg",
            "/usr/bin/ffmpeg",
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private string GetFfmpegLocationArg()
    {
        var path = GetFfmpegPath();
        return path is not null ? $"--ffmpeg-location \"{path}\"" : string.Empty;
    }

    /// <summary>
    /// Returns the ffprobe binary path (same directory as ffmpeg), or null if not found.
    /// </summary>
    public string? GetFfprobePath()
    {
        var ffmpegPath = GetFfmpegPath();
        if (ffmpegPath is not null)
        {
            var probe = Path.Combine(Path.GetDirectoryName(ffmpegPath) ?? "", "ffprobe");
            if (File.Exists(probe)) return probe;
        }
        return IsAvailable("ffprobe") ? "ffprobe" : null;
    }

    /// <summary>
    /// Concatenates trailers into a single MP4 with chapter markers so that VLC
    /// displays each trailer's title as the video advances through chapters.
    /// Accepts a list of (localPath, title) pairs. Tries stream-copy; falls back to re-encode.
    /// </summary>
    public async Task ConcatenateAsync(
        IReadOnlyList<(string Path, string Title)> inputs,
        string outputPath,
        CancellationToken ct)
    {
        if (inputs.Count == 0) return;

        var ffmpeg = GetFfmpegPath();
        if (ffmpeg is null)
        {
            _logger.LogWarning("TrailerCinema: ffmpeg not found — cannot build combined trailer.");
            return;
        }

        var listPath = outputPath + ".list.txt";
        var tmpPath  = outputPath + ".tmp.mp4";

        await File.WriteAllLinesAsync(
            listPath,
            inputs.Select(i => $"file '{i.Path.Replace("'", "'\\''")}' "),
            ct).ConfigureAwait(false);

        try
        {
            if (!await RunFfmpegConcatAsync(ffmpeg, listPath, tmpPath, "-c copy", ct).ConfigureAwait(false))
            {
                _logger.LogWarning("TrailerCinema: concat -c copy failed — retrying with re-encode.");
                if (!await RunFfmpegConcatAsync(ffmpeg, listPath, tmpPath, "-c:v libx264 -c:a aac -preset fast", ct).ConfigureAwait(false))
                {
                    _logger.LogError("TrailerCinema: ffmpeg concat failed even with re-encode.");
                    return;
                }
            }

            // Embed chapter markers so each trailer's title is visible in VLC and Jellyfin.
            var added = await AddChaptersAsync(ffmpeg, tmpPath, inputs, outputPath, ct).ConfigureAwait(false);
            if (!added)
                File.Move(tmpPath, outputPath, overwrite: true);

            _logger.LogInformation("TrailerCinema: combined trailer ({N} trailers, chapters={Ch}) → {Path}.",
                inputs.Count, added, outputPath);
        }
        finally
        {
            try { File.Delete(listPath); } catch { }
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
        }
    }

    private async Task<bool> AddChaptersAsync(
        string ffmpeg,
        string inputPath,
        IReadOnlyList<(string Path, string Title)> inputs,
        string outputPath,
        CancellationToken ct)
    {
        var ffprobe = GetFfprobePath();
        if (ffprobe is null)
        {
            _logger.LogDebug("TrailerCinema: ffprobe not found — skipping chapter markers.");
            return false;
        }

        // Get each trailer's duration to compute chapter boundaries.
        var chapters = new List<(long StartMs, long EndMs, string Title)>();
        long elapsed = 0;

        foreach (var (path, title) in inputs)
        {
            var ms = await GetDurationMsAsync(ffprobe, path, ct).ConfigureAwait(false);
            if (ms <= 0) return false;
            chapters.Add((elapsed, elapsed + ms, title));
            elapsed += ms;
        }

        // Build ffmetadata file.
        var metaPath = outputPath + ".meta.txt";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(";FFMETADATA1");
        foreach (var (startMs, endMs, title) in chapters)
        {
            sb.AppendLine("[CHAPTER]");
            sb.AppendLine("TIMEBASE=1/1000");
            sb.AppendLine($"START={startMs}");
            sb.AppendLine($"END={endMs}");
            sb.Append("title=").AppendLine(EscapeMetadata(title));
        }
        await File.WriteAllTextAsync(metaPath, sb.ToString(), ct).ConfigureAwait(false);

        // Re-mux to embed chapters (stream-copy, very fast).
        var chapTmp = outputPath + ".chap.tmp.mp4";
        var args = $"-y -i \"{inputPath}\" -i \"{metaPath}\" -map 0 -map_metadata 1 -c copy \"{chapTmp}\"";
        var psi  = new ProcessStartInfo
        {
            FileName = ffmpeg, Arguments = args,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };

        try
        {
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Cannot start ffmpeg.");
            var stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            if (proc.ExitCode != 0)
            {
                var tail = stderr.Length > 200 ? stderr[^200..] : stderr;
                _logger.LogWarning("TrailerCinema: chapter mux failed: {Err}", tail);
                return false;
            }

            File.Move(chapTmp, outputPath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TrailerCinema: chapter mux process error.");
            return false;
        }
        finally
        {
            try { File.Delete(metaPath); } catch { }
            try { if (File.Exists(chapTmp)) File.Delete(chapTmp); } catch { }
        }
    }

    private async Task<long> GetDurationMsAsync(string ffprobe, string filePath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffprobe,
            Arguments = $"-i \"{filePath}\" -show_entries format=duration -v quiet -of csv=p=0",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException();
            var output = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            if (double.TryParse(output.Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var secs))
                return (long)(secs * 1000);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TrailerCinema: ffprobe failed for {File}.", filePath);
        }

        return 0;
    }

    private static string EscapeMetadata(string s)
        => s.Replace("\\", "\\\\")
            .Replace("=", "\\=")
            .Replace(";", "\\;")
            .Replace("#", "\\#")
            .Replace("\n", " ");

    private async Task<bool> RunFfmpegConcatAsync(
        string ffmpeg, string listPath, string outputPath, string codecArgs, CancellationToken ct)
    {
        var args = $"-y -f concat -safe 0 -i \"{listPath}\" {codecArgs} \"{outputPath}\"";
        var psi  = new ProcessStartInfo
        {
            FileName = ffmpeg, Arguments = args,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };

        try
        {
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Could not start ffmpeg.");

            var stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            if (proc.ExitCode != 0)
            {
                var tail = stderr.Length > 300 ? stderr[^300..] : stderr;
                _logger.LogWarning("TrailerCinema: ffmpeg concat exit {Code}: {Err}", proc.ExitCode, tail);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TrailerCinema: ffmpeg process error.");
            return false;
        }
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
