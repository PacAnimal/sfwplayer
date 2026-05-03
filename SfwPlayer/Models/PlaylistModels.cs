namespace SfwPlayer.Models;

public record PlaylistInfo(string Id, string Title, long VideoCount, string? ThumbnailUrl);

public record VideoInfo(string Id, string Title, string? ThumbnailUrl, long Position, string? Duration = null, string? SetVideoId = null);

public record PlaybackRequest(List<VideoInfo> Videos, bool Shuffle, int StartIndex = 0);
