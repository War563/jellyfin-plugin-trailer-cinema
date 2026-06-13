namespace JellyfinTrailerPlugin.Services;

public class TrailerInfo
{
    public string VideoId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string StreamUrl { get; set; } = string.Empty;
    public DateTime ResolvedAt { get; set; } = DateTime.MinValue;

    /// <summary>Stream URLs from yt-dlp expire after ~6 hours.</summary>
    public bool IsExpired => DateTime.UtcNow - ResolvedAt > TimeSpan.FromHours(5);
}
