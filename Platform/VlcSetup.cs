#pragma warning disable CA2101 // specify marshaling for p/invoke string arguments
#pragma warning disable SYSLIB1054 // use LibraryImport instead of DllImport
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace SfwPlayer.Platform;

public static class VlcSetup
{
    // Environment.SetEnvironmentVariable doesn't call setenv() on macOS in .NET 10+
    // so native getenv() (used by libvlccore) won't see it — use setenv() directly
    [DllImport("libSystem.B.dylib")]
    private static extern int setenv(string name, string value, int overwrite);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern IntPtr objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern IntPtr sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_ptr(IntPtr obj, IntPtr sel);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_nint(IntPtr obj, IntPtr sel, nint arg);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr IdGetter(IntPtr self, IntPtr cmd);

    // kept alive to prevent GC of delegate used as native function pointer
    private static IdGetter? _windowMethodImpl;

    public static void Initialize()
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.OSArchitecture != Architecture.Arm64) return;

        var libDir = Path.Combine(AppContext.BaseDirectory, "libvlc", "osx-arm64");
        if (!Directory.Exists(libDir)) return;

        var pluginsDir = Path.Combine(libDir, "plugins");
        _ = setenv("VLC_PLUGIN_PATH", pluginsDir, 1);
        Core.Initialize(libDir);
    }

    // establishes a GUI connection to the window server so the process can show windows.
    // safe to call from any thread.
    public static void ActivateApp()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var nsApp = objc_msgSend_ptr(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
        objc_msgSend_void_nint(nsApp, sel_registerName("setActivationPolicy:"), 0); // NSApplicationActivationPolicyRegular
        objc_msgSend_void_nint(nsApp, sel_registerName("activateIgnoringOtherApps:"), 1);
    }

    // macOS 26 calls [avnWindow window] on AvnWindow objects (an NSWindow subclass).
    // NSWindow has no 'window' method so this crashes. Inject 'window -> self' at runtime.
    // Must be called after libAvaloniaNative.dylib registers the AvnWindow class.
    public static void PatchAvnWindow()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var cls = objc_getClass("AvnWindow");
        if (cls == IntPtr.Zero) return;
        var sel = sel_registerName("window");
        _windowMethodImpl = (self, _) => self;
        class_addMethod(cls, sel, Marshal.GetFunctionPointerForDelegate(_windowMethodImpl), "@16@0:8");
    }

    public static string[] GetArgs() => [];
}
