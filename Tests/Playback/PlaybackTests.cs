using System.Diagnostics;

namespace Tests.Playback;

[TestFixture]
public class PlaybackTests
{
    private const string TestVideoUrl = "https://youtu.be/EngW7tLk6R8"; // 7-second demo

    [Test]
    [CancelAfter(60_000)]
    public async Task PlaysShortVideoToCompletion(CancellationToken cancel)
    {
        // the test bin dir has the app binary but with a broken runtimeconfig;
        // resolve to the app's own output dir via the dll location
        var appBin = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "bin", "Debug", "net10.0", "osx-arm64"));
        var appExe = Path.Combine(appBin, "sfwplayer");
        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();
        var proc = new Process
        {
            StartInfo = new ProcessStartInfo(appExe, $"--url {TestVideoUrl} --exit-on-done")
            {
                UseShellExecute = false,
                WorkingDirectory = appBin,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
            EnableRaisingEvents = true,
        };
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cancel.Register(() => tcs.TrySetCanceled());
        proc.Exited += (_, _) => tcs.TrySetResult();
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var sw = Stopwatch.StartNew();
        await tcs.Task;
        sw.Stop();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(proc.ExitCode, Is.Zero,
                $"app exited with non-zero code\nstdout:\n{stdout}\nstderr:\n{stderr}");
            Assert.That(sw.Elapsed.TotalSeconds, Is.LessThan(30), "7-second video should complete well within 30s");
        }
    }
}
