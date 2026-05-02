using Microsoft.Extensions.Logging;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace SfwPlayer.Services;

public class YoutubeService(ILogger<YoutubeService> log, CookieStore? cookies = null)
{
    private const string DefaultVideoUrl = "https://youtu.be/bWnhtqDJwIU";

    // accepts a full URL or a bare video ID
    public async Task<string> GetStreamUrl(string? videoIdOrUrl = null, CancellationToken cancel = default)
    {
        var target = videoIdOrUrl == null ? DefaultVideoUrl
            : videoIdOrUrl.Contains("://") ? videoIdOrUrl
            : $"https://youtu.be/{videoIdOrUrl}";

        if (log.IsEnabled(LogLevel.Information))
            log.LogInformation("resolving stream url for {url}", target);

        var client = BuildClient();
        var manifest = await client.Videos.Streams.GetManifestAsync(target, cancel);

        var stream = manifest.GetMuxedStreams()
            .Where(s => s.VideoResolution.Height <= 720)
            .GetWithHighestVideoQuality()
            ?? manifest.GetMuxedStreams().GetWithHighestVideoQuality()
            ?? throw new InvalidOperationException("no usable stream found for video");

        if (log.IsEnabled(LogLevel.Information))
            log.LogInformation("resolved {quality} stream ({container})", stream.VideoQuality.Label, stream.Container.Name);
        return stream.Url;
    }

    private YoutubeClient BuildClient()
    {
        var stored = cookies?.GetCookies()
            .Where(c => c.Domain.Contains("youtube.com", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (stored is not { Count: > 0 }) return new YoutubeClient();

        var http = new HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent", InnerTubeService.ChromeUA);
        http.DefaultRequestHeaders.Add("Cookie",
            string.Join("; ", stored.Select(c => $"{c.Name}={c.Value}")));
        return new YoutubeClient(http);
    }
}
