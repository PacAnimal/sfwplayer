#pragma warning disable CA1401 // p/invokes should not be visible
#pragma warning disable SYSLIB1054 // use LibraryImport instead of DllImport
using System.Runtime.InteropServices;

namespace SfwPlayer.Platform.Windows;

internal static class Native
{
    internal const int GwlExstyle = -20;
    internal const int WsExTransparent = 0x00000020;
    internal const int WsExLayered = 0x00080000;
    internal const int VkControl = 0x11;
    internal const int VkMenu = 0x12; // Alt

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public int X, Y; }

    [DllImport("user32.dll")] internal static extern int GetWindowLong(IntPtr hwnd, int n);
    [DllImport("user32.dll")] internal static extern int SetWindowLong(IntPtr hwnd, int n, int val);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point pt);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int vk);
}
