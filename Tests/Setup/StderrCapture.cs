#pragma warning disable CA1806 // p/invoke return values intentionally ignored (posix convention)
#pragma warning disable SYSLIB1054 // use LibraryImport instead of DllImport
using System.Runtime.InteropServices;
using System.Text;

namespace Tests.Setup;

// captures native fd 2 (stderr) via pipe/dup2 — catches messages written directly
// by native libraries (e.g. FFmpeg av_log) that bypass .NET's Console.Error
internal static class StderrCapture
{
    // posix (macOS)
    [DllImport("libSystem.B.dylib")] private static extern int pipe(int[] fds);
    [DllImport("libSystem.B.dylib")] private static extern int dup(int fd);
    [DllImport("libSystem.B.dylib")] private static extern int dup2(int oldfd, int newfd);
    [DllImport("libSystem.B.dylib")] private static extern int close(int fd);
    [DllImport("libSystem.B.dylib")] private static extern nint read(int fd, IntPtr buf, nuint count);

    // windows crt
    [DllImport("msvcrt.dll")] private static extern int _pipe(int[] fds, uint psize, int textmode);
    [DllImport("msvcrt.dll")] private static extern int _dup(int fd);
    [DllImport("msvcrt.dll")] private static extern int _dup2(int oldfd, int newfd);
    [DllImport("msvcrt.dll")] private static extern int _close(int fd);
    [DllImport("msvcrt.dll")] private static extern int _read(int fd, IntPtr buf, uint count);

    // redirects fd 2 to a pipe; returns (savedFd, readFd) — pass both to End()
    public static (int savedFd, int readFd) Begin()
    {
        var fds = new int[2]; // fds[0]=read end, fds[1]=write end
        if (OperatingSystem.IsWindows())
        {
            _pipe(fds, 4096, 0x8000); // 0x8000 = _O_BINARY
            var saved = _dup(2);
            _dup2(fds[1], 2);
            _close(fds[1]);
            return (saved, fds[0]);
        }
        pipe(fds);
        var savedFd = dup(2);      // preserve original stderr
        dup2(fds[1], 2);           // redirect stderr to pipe write end
        close(fds[1]);             // drop extra reference to write end; fd 2 is the only holder
        return (savedFd, fds[0]);
    }

    // restores fd 2, drains and returns everything written to the pipe since Begin()
    public static string End(int savedFd, int readFd)
    {
        var sb = new StringBuilder();
        var buf = new byte[4096];
        var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                _dup2(savedFd, 2);
                _close(savedFd);
                int n;
                while ((n = _read(readFd, handle.AddrOfPinnedObject(), 4096)) > 0)
                    sb.Append(Encoding.UTF8.GetString(buf, 0, n));
            }
            else
            {
                dup2(savedFd, 2); // restore original stderr; pipe write end now has zero references → EOF
                close(savedFd);
                nint n;
                while ((n = read(readFd, handle.AddrOfPinnedObject(), 4096)) > 0)
                    sb.Append(Encoding.UTF8.GetString(buf, 0, (int)n));
            }
        }
        finally
        {
            handle.Free();
            if (OperatingSystem.IsWindows()) _close(readFd);
            else close(readFd);
        }
        return sb.ToString();
    }
}
