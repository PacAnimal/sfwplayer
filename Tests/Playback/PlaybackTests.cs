using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using SfwPlayer.Platform;
using SfwPlayer.Services;
using Tests.Setup;

namespace Tests.Playback;

[TestFixture]
public class PlaybackTests
{
    private const string TestVideoUrl = "https://youtu.be/EngW7tLk6R8"; // 7-second demo

    [Test]
    [CancelAfter(60_000)]
    public async Task PlaysShortVideoToCompletion(CancellationToken cancel)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cancel.Register(() => tcs.TrySetCanceled());

        // use dummy outputs: we care about decode + EndReached, not audio/video rendering
        using var vlc = new LibVLC(false, [.. VlcSetup.GetArgs(), "--vout=dummy", "--aout=adummy", "--no-stats"]);
        using var player = new MediaPlayer(vlc);
        player.EndReached += (_, _) => tcs.TrySetResult();

        var url = await new YoutubeService(TestLog.CreateLogger<YoutubeService>())
            .GetStreamUrl(TestVideoUrl, cancel);
        using var media = new Media(vlc, new Uri(url));
        player.Play(media);

        var sw = Stopwatch.StartNew();
        await tcs.Task;
        sw.Stop();

        player.Stop();
        Assert.That(sw.Elapsed.TotalSeconds, Is.LessThan(30),
            "7-second video should complete well within 30s");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task VmemReceivesFrames(CancellationToken cancel)
    {
        const int MaxW = 1280, MaxH = 720;
        uint actualWidth = 0, actualHeight = 0;
        var frameReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cancel.Register(() => frameReceived.TrySetCanceled());

        var y = new byte[MaxW * MaxH];
        var u = new byte[MaxW / 2 * (MaxH / 2)];
        var v = new byte[MaxW / 2 * (MaxH / 2)];
        var pinY = GCHandle.Alloc(y, GCHandleType.Pinned);
        var pinU = GCHandle.Alloc(u, GCHandleType.Pinned);
        var pinV = GCHandle.Alloc(v, GCHandleType.Pinned);

        try
        {
            // verbose=true to capture any format negotiation errors in output
            using var vlc = new LibVLC(true, [.. VlcSetup.GetArgs(), "--no-stats", "--aout=adummy"]);
            vlc.Log += (_, e) => Console.Error.WriteLine($"[vlc] {e.Level} {e.Module}: {e.Message}");
            using var player = new MediaPlayer(vlc);

            uint VideoFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height,
                ref uint pitches, ref uint lines)
            {
                actualWidth = width;
                actualHeight = height;
                Console.Error.WriteLine($"[test] VideoFormat called: chroma={Marshal.PtrToStringAnsi(chroma, 4)} {width}x{height}");
                var uvW = (width + 1) / 2;
                var uvH = (height + 1) / 2;
                pitches = width;
                Unsafe.Add(ref pitches, 1) = uvW;
                Unsafe.Add(ref pitches, 2) = uvW;
                lines = height;
                Unsafe.Add(ref lines, 1) = uvH;
                Unsafe.Add(ref lines, 2) = uvH;
                return 1;
            }

            IntPtr Lock(IntPtr opaque, IntPtr planes)
            {
                Marshal.WriteIntPtr(planes, 0, pinY.AddrOfPinnedObject());
                Marshal.WriteIntPtr(planes, IntPtr.Size, pinU.AddrOfPinnedObject());
                Marshal.WriteIntPtr(planes, IntPtr.Size * 2, pinV.AddrOfPinnedObject());
                return IntPtr.Zero;
            }

            void Display(IntPtr opaque, IntPtr picture) => frameReceived.TrySetResult();

            player.SetVideoFormatCallbacks(VideoFormat, null);
            player.SetVideoCallbacks(Lock, null, Display);

            var url = await new YoutubeService(TestLog.CreateLogger<YoutubeService>())
                .GetStreamUrl(TestVideoUrl, cancel);
            using var media = new Media(vlc, new Uri(url));
            player.Play(media);

            await frameReceived.Task;
            player.Stop();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(actualWidth, Is.GreaterThan(0), "VideoFormat must have been called with a valid width");
                Assert.That(actualHeight, Is.GreaterThan(0), "VideoFormat must have been called with a valid height");
            }
        }
        finally
        {
            pinY.Free(); pinU.Free(); pinV.Free();
        }
    }
}
