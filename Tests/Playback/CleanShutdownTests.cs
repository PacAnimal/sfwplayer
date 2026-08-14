using SfwPlayer.Services;
using Tests.Setup;

namespace Tests.Playback;

[TestFixture]
public class CleanShutdownTests
{
    private const string TestVideoUrl = "https://youtu.be/EngW7tLk6R8";

    // verifies that VlcVideoBridge.StopAsync() suppresses the harmless FFmpeg noise
    // ([h264] get_buffer() / thread_get_buffer() failed) that VLC 3.x produces when
    // its picture-pool teardown races with active frame-decode threads during Stop().
    [Test]
    [CancelAfter(180_000)]
    public async Task StopMidPlaybackProducesNoDecoderErrors(CancellationToken cancel)
    {
        var (savedFd, readFd) = StderrCapture.Begin();
        string stderr;

        try
        {
            var bridge = new VlcVideoBridge(["--aout=adummy"]);

            var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cancel.Register(() => playing.TrySetCanceled());
            bridge.Player.Playing += (_, _) => playing.TrySetResult();

            var url = await YoutubeThrottle.PaceAsync(
                () => new YoutubeService(TestLog.CreateLogger<YoutubeService>()).GetStreamUrl(TestVideoUrl, cancel), cancel);
            bridge.Play(url);

            await playing.Task;
            await Task.Delay(2000, cancel); // let frame-decode threads become active

            await bridge.StopAsync(); // suppresses fd 2 during Player.Stop()
            bridge.Dispose();
        }
        finally
        {
            stderr = StderrCapture.End(savedFd, readFd);
        }

        Assert.That(stderr, Does.Not.Contain("get_buffer() failed"),
            $"FFmpeg decoder noise escaped StopAsync() suppression:\n{stderr}");
    }
}
