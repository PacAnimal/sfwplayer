using System.Diagnostics;
using System.Runtime.InteropServices;
using SfwPlayer.Platform.MacOS;
using Tests.Setup;

namespace Tests.Auth;

[TestFixture]
public class AppleWebAuthTests
{
    // Verifies the static constructor runs without crashing:
    // block ABI setup, _NSConcreteGlobalBlock resolution, ObjC class registration.
    [Test]
    public void ClassInit_DoesNotCrash()
    {
        if (!OperatingSystem.IsMacOS()) Assert.Ignore("macOS only");
        Assert.DoesNotThrow(() => _ = typeof(AppleWebAuth));
    }

    // Verifies the null-anchor guard: calling with no NSView returns null immediately
    // without opening a browser or blocking.
    [Test]
    public async Task SignInAsync_NoWindow_ReturnsNullImmediately()
    {
        if (!OperatingSystem.IsMacOS()) Assert.Ignore("macOS only");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await AppleWebAuth.SignInAsync(IntPtr.Zero);
        sw.Stop();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.Null);
            Assert.That(sw.ElapsedMilliseconds, Is.LessThan(500),
                "null-anchor path should return immediately, not wait for a timeout");
        }
    }

    // Verifies that an external CancellationToken propagates cleanly through SignInAsync.
    // Uses IntPtr.Zero (no window) so it never opens a browser — just tests cancellation wiring.
    [Test]
    public async Task SignInAsync_Cancellation_ReturnsNull()
    {
        if (!OperatingSystem.IsMacOS()) Assert.Ignore("macOS only");

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // already cancelled

        var result = await AppleWebAuth.SignInAsync(IntPtr.Zero, cts.Token);
        Assert.That(result, Is.Null);
    }

    // Spawns SfwPlayer --signin-test, which opens the Google sign-in window for 5 s then exits.
    // The window is visible during the test run.
    [Test]
    public async Task SignInAsync_OpensBrowserAndCancels()
    {
        if (!OperatingSystem.IsMacOS()) Assert.Ignore("macOS only");

        var sfwExe = SubprocessHelper.FindSfwPlayerExe();
        if (sfwExe == null)
            Assert.Ignore("SfwPlayer executable not found; build the main project first");

        var psi = new ProcessStartInfo(sfwExe!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--signin-test");

        using var proc = Process.Start(psi)!;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var done = false;
        try
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync(cts.Token)) != null)
            {
                if (line == "DONE") { done = true; break; }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (!proc.HasExited) proc.Kill();
        }

        Assert.That(done, Is.True, "SfwPlayer --signin-test should print DONE after showing the sign-in window");
    }
}
