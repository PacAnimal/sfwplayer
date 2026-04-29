using Microsoft.Extensions.Logging;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace SfwPlayer.Services;

public class YoutubeService(ILogger<YoutubeService> log)
{
    private const string DefaultVideoUrl = "https://youtu.be/bWnhtqDJwIU";
    private readonly YoutubeClient _youtube = new();

    public async Task<string> GetStreamUrl(string? url = null, CancellationToken cancel = default)
    {
        var target = url ?? DefaultVideoUrl;
        if (log.IsEnabled(LogLevel.Information))
            log.LogInformation("resolving stream url for {url}", target);

        var manifest = await _youtube.Videos.Streams.GetManifestAsync(target, cancel);

        var stream = manifest.GetMuxedStreams()
            .Where(s => s.VideoResolution.Height <= 720)
            .GetWithHighestVideoQuality()
            ?? manifest.GetMuxedStreams().GetWithHighestVideoQuality()
            ?? throw new InvalidOperationException("no usable stream found for video");

        if (log.IsEnabled(LogLevel.Information))
            log.LogInformation("resolved {quality} stream ({container})", stream.VideoQuality.Label, stream.Container.Name);
        return stream.Url;
    }
}
