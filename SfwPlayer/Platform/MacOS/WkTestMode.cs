#pragma warning disable CA2101 // marshaling for p/invoke string arguments
#pragma warning disable SYSLIB1054 // use LibraryImport instead of DllImport
using System.Net;
using System.Runtime.InteropServices;

namespace SfwPlayer.Platform.MacOS;

// runs as a subprocess mode: navigates a WKWebView to a URL, pumps the main run loop manually,
// and prints extracted cookies to stdout so the test can read them.
internal static class WkTestMode
{
    internal static void Run(string url)
    {
        if (!OperatingSystem.IsMacOS()) { Console.WriteLine("DONE"); return; }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var task = AppleWebAuth.NavigateAndExtractAsync(
            url,
            u => u.StartsWith(url, StringComparison.Ordinal),
            cts.Token);

        if (!task.IsCompleted)
            PumpUntilDone(task);

        if (task.Result != null)
            foreach (var c in task.Result)
                Console.WriteLine($"COOKIE:{c.Name}={c.Value}");
        Console.WriteLine("DONE");
    }

    // opens the Google sign-in window for 5 s, then cancels; used by --signin-test
    internal static void RunSignIn()
    {
        if (!OperatingSystem.IsMacOS()) { Console.WriteLine("DONE"); return; }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var task = AppleWebAuth.NavigateAndExtractAsync(
            AppleWebAuth.GoogleSignInUrl,
            _ => false,
            cts.Token);

        if (!task.IsCompleted)
            PumpUntilDone(task);

        Console.WriteLine("DONE");
    }

    private static void PumpUntilDone(Task<List<Cookie>?> task)
    {
        var runLoop = MsgPtr(ObjcClass("NSRunLoop"), SelReg("mainRunLoop"));

        NativeLibrary.TryLoad(
            "/System/Library/Frameworks/Foundation.framework/Foundation",
            typeof(WkTestMode).Assembly, null, out var foundation);
        NativeLibrary.TryGetExport(foundation, "NSDefaultRunLoopMode", out var modePtrAddr);
        var mode = modePtrAddr != IntPtr.Zero ? Marshal.ReadIntPtr(modePtrAddr) : IntPtr.Zero;

        var nsDateClass = ObjcClass("NSDate");
        var selDate = SelReg("dateWithTimeIntervalSinceNow:");
        var selRun = SelReg("runMode:beforeDate:");

        while (!task.IsCompleted)
        {
            var deadline = MsgPtrDbl(nsDateClass, selDate, 0.1);
            MsgVoidPP(runLoop, selRun, mode, deadline);
        }
    }

    private static IntPtr ObjcClass(string name) => objc_getClass(name);
    private static IntPtr SelReg(string name) => sel_registerName(name);

    [DllImport("/usr/lib/libobjc.dylib")] private static extern IntPtr objc_getClass(string name);
    [DllImport("/usr/lib/libobjc.dylib")] private static extern IntPtr sel_registerName(string name);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")] private static extern IntPtr MsgPtr(IntPtr o, IntPtr s);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")] private static extern IntPtr MsgPtrDbl(IntPtr o, IntPtr s, double d);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")] private static extern void MsgVoidPP(IntPtr o, IntPtr s, IntPtr a, IntPtr b);
}
