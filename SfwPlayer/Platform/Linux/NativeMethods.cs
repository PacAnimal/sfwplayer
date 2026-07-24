#pragma warning disable CA1401 // p/invokes should not be visible
#pragma warning disable CA2101 // specify marshaling for p/invoke string arguments
#pragma warning disable SYSLIB1054 // use LibraryImport instead of DllImport
using System.Runtime.InteropServices;

namespace SfwPlayer.Platform.Linux;

internal static class NativeMethods
{
    internal const int ShapeInput = 2;
    internal const int ShapeSet = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct XRectangle { public short X, Y, Width, Height; }

    [DllImport("libX11")] internal static extern IntPtr XOpenDisplay(string? display);
    [DllImport("libX11")] internal static extern IntPtr XRootWindow(IntPtr display, int screen);
    [DllImport("libX11")] internal static extern int XDefaultScreen(IntPtr display);
    [DllImport("libX11")]
    internal static extern int XQueryPointer(IntPtr display, IntPtr window,
        out IntPtr rootReturn, out IntPtr childReturn,
        out int rootX, out int rootY, out int winX, out int winY, out uint mask);
    [DllImport("libX11")] internal static extern void XQueryKeymap(IntPtr display, byte[] keys);

    [DllImport("libXext")]
    internal static extern void XShapeCombineRectangles(IntPtr display,
        IntPtr dest, int destKind, int xOff, int yOff,
        IntPtr rects, int nRects, int op, int ordering);
}
