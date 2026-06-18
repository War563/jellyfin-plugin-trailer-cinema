using MediaBrowser.Model.Plugins;

namespace JellyfinTrailerPlugin.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>YouTube channel ID to pull trailers from.</summary>
    public string YouTubeChannelId { get; set; } = string.Empty;

    /// <summary>YouTube Data API v3 key.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Number of trailers to play before each movie.</summary>
    public int TrailerCount { get; set; } = 5;

    /// <summary>Total pool size kept on disk.</summary>
    public int PoolSize { get; set; } = 20;

    /// <summary>Case-insensitive substring that video titles must contain.</summary>
    public string TitleFilter { get; set; } = "castellano";

    /// <summary>Comma-separated words — videos whose title contains any of them are excluded.</summary>
    public string TitleExclude { get; set; } = string.Empty;

    /// <summary>Shuffle the pool before picking trailers.</summary>
    public bool Shuffle { get; set; } = true;

    /// <summary>Minimum video duration in seconds.</summary>
    public int MinDurationSeconds { get; set; } = 60;

    /// <summary>Maximum video duration in seconds.</summary>
    public int MaxDurationSeconds { get; set; } = 300;

    /// <summary>
    /// Full path to the yt-dlp binary. Leave empty to auto-detect from system PATH
    /// or auto-download into the plugin data directory.
    /// </summary>
    public string YtDlpPath { get; set; } = string.Empty;

    /// <summary>
    /// Full path to the ffmpeg binary used by yt-dlp to merge video+audio streams.
    /// Leave empty to auto-detect (checks /usr/lib/jellyfin-ffmpeg/ffmpeg and system PATH).
    /// </summary>
    public string FfmpegPath { get; set; } = string.Empty;
}
