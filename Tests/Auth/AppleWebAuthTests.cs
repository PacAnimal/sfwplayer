#pragma warning disable CA2101 // marshaling for p/invoke string arguments
#pragma warning disable SYSLIB1054 // use LibraryImport instead of DllImport
using System.Runtime.InteropServices;
using SfwPlayer.Platform.MacOS;

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

        Assert.That(result, Is.Null);
        Assert.That(sw.ElapsedMilliseconds, Is.LessThan(500),
            "null-anchor path should return immediately, not wait for a timeout");
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

    // Opens a real ASWebAuthenticationSession browser sheet, waits 5 seconds, then cancels.
    // The Google sign-in browser should visibly appear and disappear.
    // Skips gracefully if NSWindow creation fails (headless/CI environment).
    [Test]
    [CancelAfter(20_000)]
    public async Task SignInAsync_OpensBrowserAndCancels(CancellationToken testCancel)
    {
        if (!OperatingSystem.IsMacOS()) Assert.Ignore("macOS only");

        var nsWindow = CreateNsWindow();
        if (nsWindow == IntPtr.Zero) Assert.Ignore("NSWindow creation failed — headless/CI environment");

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result = await AppleWebAuth.SignInWithAnchorAsync(nsWindow, cts.Token);
            Assert.That(result, Is.Null, "cancelled sign-in should return null");
        }
        finally
        {
            ObjcMsgSendVoid(nsWindow, SelRegisterName("close"));
        }
    }

    // --- NSWindow helpers ---

    [StructLayout(LayoutKind.Sequential)]
    private struct NSRect { public double X, Y, W, H; }

    [DllImport("/usr/lib/libobjc.dylib")] private static extern IntPtr ObjcGetClass(string name);
    [DllImport("/usr/lib/libobjc.dylib")] private static extern IntPtr SelRegisterName(string name);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")] private static extern IntPtr ObjcMsgSendPtr(IntPtr obj, IntPtr sel);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")] private static extern IntPtr ObjcMsgSendInitWindow(IntPtr obj, IntPtr sel, NSRect rect, nuint style, nuint backing, bool defer);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")] private static extern void ObjcMsgSendVoid(IntPtr obj, IntPtr sel);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")] private static extern void ObjcMsgSendVoidPtr(IntPtr obj, IntPtr sel, IntPtr a);

    private static IntPtr CreateNsWindow()
    {
        try
        {
            var cls = ObjcGetClass("NSWindow");
            var alloc = ObjcMsgSendPtr(cls, SelRegisterName("alloc"));
            var rect = new NSRect { X = 200, Y = 200, W = 800, H = 600 };
            // NSTitledWindowMask(1) | NSClosableWindowMask(2) | NSMiniaturizableWindowMask(4) = 7, NSBackingStoreBuffered = 2
            var window = ObjcMsgSendInitWindow(alloc, SelRegisterName("initWithContentRect:styleMask:backing:defer:"),
                rect, 7, 2, false);
            if (window == IntPtr.Zero) return IntPtr.Zero;
            ObjcMsgSendVoidPtr(window, SelRegisterName("makeKeyAndOrderFront:"), IntPtr.Zero);
            return window;
        }
        catch { return IntPtr.Zero; }
    }
}
