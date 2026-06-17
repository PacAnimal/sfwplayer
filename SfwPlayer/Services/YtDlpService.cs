using Microsoft.Extensions.Logging;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace SfwPlayer.Services;

public class YoutubeService(ILogger<YoutubeService> log, CookieStore? cookies = null)
{
    private const string DefaultVideoUrl = "https://youtu.be/bWnhtqDJwIU";
    private (string key, StreamManifest manifest)? _manifestCache;

    // accepts a full URL or a bare video ID; windowWidth/Height in physical pixels for resolution selection
    public async Task<string> GetStreamUrl(string? videoIdOrUrl = null, CancellationToken cancel = default, double windowWidth = 0, double windowHeight = 0)
    {
        var target = videoIdOrUrl == null ? DefaultVideoUrl
            : videoIdOrUrl.Contains("://") ? videoIdOrUrl
            : $"https://youtu.be/{videoIdOrUrl}";

        if (log.IsEnabled(LogLevel.Information))
            log.LogInformation("resolving stream url for {url}", target);

        var client = BuildClient();
        var manifest = await client.Videos.Streams.GetManifestAsync(target, cancel);
        _manifestCache = (videoIdOrUrl ?? DefaultVideoUrl, manifest);

        var stream = SelectStream(manifest, windowWidth, windowHeight);

        if (log.IsEnabled(LogLevel.Information))
            log.LogInformation("resolved {quality} stream ({container})", stream.VideoQuality.Label, stream.Container.Name);
        return stream.Url;
    }

    // re-selects from cached manifest without a network call; returns null if video not cached
    public string? SelectStreamForSize(string videoKey, double windowWidth, double windowHeight)
    {
        if (_manifestCache?.key != videoKey) return null;
        return SelectStream(_manifestCache.Value.manifest, windowWidth, windowHeight).Url;
    }

    private static MuxedStreamInfo SelectStream(StreamManifest manifest, double windowWidth, double windowHeight)
    {
        var streams = manifest.GetMuxedStreams().OrderBy(s => s.VideoResolution.Height).ToList();
        if (streams.Count == 0) throw new InvalidOperationException("no muxed streams available");

        if (windowWidth > 0 && windowHeight > 0)
        {
            // use the largest stream for the most accurate aspect ratio
            var largest = streams[^1];
            var aspect = (double)largest.VideoResolution.Width / largest.VideoResolution.Height;

            // display area: how the video fills the window with Stretch=Uniform
            double displayW, displayH;
            if (windowWidth / windowHeight >= aspect)
            { displayH = windowHeight; displayW = windowHeight * aspect; }   // window wider — pillar-boxed
            else
            { displayW = windowWidth; displayH = windowWidth / aspect; }     // window taller — letter-boxed

            // smallest stream whose both dimensions cover the display area (so we always scale down)
            return streams.FirstOrDefault(s =>
                s.VideoResolution.Width >= displayW &&
                s.VideoResolution.Height >= displayH) ?? streams[^1];
        }

        // fallback when window size is unknown
        return streams.Where(s => s.VideoResolution.Height <= 720).LastOrDefault() ?? streams[^1];
    }

    private YoutubeClient BuildClient()
    {
        var stored = cookies?.GetCookies()
            .Where(c => c.Domain.Contains("youtube.com", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (stored is not { Count: > 0 }) return new YoutubeClient();

        var http = new HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent", InnerTubeService.ChromeUa);
        http.DefaultRequestHeaders.Add("Cookie",
            string.Join("; ", stored.Select(c => $"{c.Name}={c.Value}")));
        return new YoutubeClient(http);
    }
}
