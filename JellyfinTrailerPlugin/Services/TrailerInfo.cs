namespace JellyfinTrailerPlugin.Services;

public class TrailerInfo
{
    public string VideoId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;

    /// <summary>Absolute path to the downloaded mp4 file on disk.</summary>
    public string LocalPath { get; set; } = string.Empty;

    public DateTime DownloadedAt { get; set; } = DateTime.MinValue;

    /// <summary>Duration in milliseconds; used to drive the timer-based fallback in PlaybackHookService.</summary>
    public long DurationMs { get; set; }

    /// <summary>Jellyfin library item Guid assigned by TrailerLibraryService.</summary>
    public Guid JellyfinItemId { get; set; }

    /// <summary>True when the file has been downloaded and still exists on disk.</summary>
    public bool IsReady => !string.IsNullOrEmpty(LocalPath) && File.Exists(LocalPath);
}
