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

    // Spawns SfwPlayer --url <url> --exit-on-done; the real app window opens visibly and exits 0 when done.
    [Test]
    [CancelAfter(60_000)]
    public async Task PlaysShortVideoToCompletion(CancellationToken cancel)
    {
        var sfwExe = SubprocessHelper.FindSfwPlayerExe();
        if (sfwExe == null)
            Assert.Ignore("SfwPlayer executable not found; build the main project first");

        var psi = new ProcessStartInfo(sfwExe!)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--url");
        psi.ArgumentList.Add(TestVideoUrl);
        psi.ArgumentList.Add("--exit-on-done");

        var sw = Stopwatch.StartNew();
        using var proc = Process.Start(psi)!;
        try
        {
            await proc.WaitForExitAsync(cancel);
        }
        finally
        {
            if (!proc.HasExited) proc.Kill();
        }
        sw.Stop();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(proc.ExitCode, Is.Zero, "SfwPlayer should exit 0 after EndReached");
            Assert.That(sw.Elapsed.TotalSeconds, Is.LessThan(50), "7-second video should complete well within 50s");
        }
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
