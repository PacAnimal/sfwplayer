#pragma warning disable CA1401 // p/invokes should not be visible
#pragma warning disable CA2101 // specify marshaling for p/invoke string arguments
#pragma warning disable SYSLIB1054 // use LibraryImport instead of DllImport
using System.Runtime.InteropServices;

namespace SfwPlayer.Platform.MacOS;

internal static partial class Native
{
    internal const int kCGEventSourceStateCombinedSessionState = 1;
    internal const ulong kCGEventFlagMaskControl = 1UL << 18;
    internal const ulong kCGEventFlagMaskAlternate = 1UL << 19;

    [StructLayout(LayoutKind.Sequential)]
    internal struct CGPoint { public double X, Y; }

    [DllImport("/usr/lib/libobjc.dylib")]
    internal static extern IntPtr objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.dylib")]
    internal static extern IntPtr sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    internal static extern IntPtr objc_msgSend_ptr(IntPtr obj, IntPtr sel);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    internal static extern CGPoint objc_msgSend_CGPoint(IntPtr obj, IntPtr sel);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    internal static extern void objc_msgSend_void_bool(IntPtr obj, IntPtr sel,
        [MarshalAs(UnmanagedType.U1)] bool arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    internal static extern void objc_msgSend_void_nint(IntPtr obj, IntPtr sel, nint arg);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    internal static extern IntPtr CGEventCreate(IntPtr source);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    internal static extern CGPoint CGEventGetLocation(IntPtr evt);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    internal static extern void CFRelease(IntPtr cf);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    internal static extern ulong CGEventSourceFlagsState(int stateID);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    internal static extern bool CGEventSourceButtonState(int stateID, uint button);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    internal static extern void objc_msgSend_void_ptr_nint(IntPtr obj, IntPtr sel, IntPtr ptr, nint n);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    internal static extern void objc_msgSend_void_ptr(IntPtr obj, IntPtr sel, IntPtr ptr);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    internal static extern void objc_msgSend_void_double(IntPtr obj, IntPtr sel, double d);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    internal static extern nint objc_msgSend_nint(IntPtr obj, IntPtr sel);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    internal static extern ulong objc_msgSend_ulong(IntPtr obj, IntPtr sel);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NSRect { public double X, Y, Width, Height; }

    // arm64: structs return via regular objc_msgSend, no _stret variant
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    internal static extern NSRect objc_msgSend_NSRect(IntPtr obj, IntPtr sel);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    internal static extern void objc_msgSend_void_NSRect_bool(IntPtr obj, IntPtr sel,
        NSRect rect, [MarshalAs(UnmanagedType.U1)] bool display);

    // nextEventMatchingMask:untilDate:inMode:dequeue:
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    internal static extern IntPtr objc_msgSend_nextEvent(IntPtr obj, IntPtr sel,
        ulong mask, IntPtr expiration, IntPtr mode, [MarshalAs(UnmanagedType.U1)] bool dequeue);

    // stringWithUTF8String: — for creating NSString from a C literal
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    internal static extern IntPtr objc_msgSend_ptr_utf8(IntPtr obj, IntPtr sel,
        [MarshalAs(UnmanagedType.LPStr)] string str);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    internal static extern CGPoint objc_msgSend_CGPoint_ptr(IntPtr obj, IntPtr sel, IntPtr arg);
}
