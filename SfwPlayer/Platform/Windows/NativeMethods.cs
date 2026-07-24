#pragma warning disable CA1401 // p/invokes should not be visible
#pragma warning disable SYSLIB1054 // use LibraryImport instead of DllImport
using System.Runtime.InteropServices;

namespace SfwPlayer.Platform.Windows;

internal static class NativeMethods
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

    // crt fd primitives (mirrors MacOS.NativeMethods POSIX equivalents)
    internal const int OWronly = 0x0001;
    [DllImport("msvcrt.dll")] internal static extern int _open_osfhandle(IntPtr osfhandle, int flags);
    [DllImport("msvcrt.dll")] internal static extern int _dup(int fd);
    [DllImport("msvcrt.dll")] internal static extern int _dup2(int oldfd, int newfd);
    [DllImport("msvcrt.dll")] internal static extern int _close(int fd);

    // opens the NUL device as a Win32 HANDLE — more reliable than _wopen("nul") across CRT versions
    internal const uint GenericWrite = 0x40000000u;
    internal const uint FileShareAll = 0x00000003u; // READ | WRITE
    internal const uint OpenExisting = 3u;
    [DllImport("kernel32.dll")]
    internal static extern IntPtr CreateFileW(
        [MarshalAs(UnmanagedType.LPWStr)] string lpFileName,
        uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);
}
