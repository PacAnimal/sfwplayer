using System.Runtime.InteropServices;
using System.Text;

namespace Tests.Setup;

// captures native fd 2 (stderr) via POSIX pipe/dup2 — catches messages written directly
// by native libraries (e.g. FFmpeg av_log) that bypass .NET's Console.Error
internal static class StderrCapture
{
    [DllImport("libSystem.B.dylib")] private static extern int pipe(int[] fds);
    [DllImport("libSystem.B.dylib")] private static extern int dup(int fd);
    [DllImport("libSystem.B.dylib")] private static extern int dup2(int oldfd, int newfd);
    [DllImport("libSystem.B.dylib")] private static extern int close(int fd);
    [DllImport("libSystem.B.dylib")] private static extern nint read(int fd, IntPtr buf, nuint count);

    // redirects fd 2 to a pipe; returns (savedFd, readFd) — pass both to End()
    public static (int savedFd, int readFd) Begin()
    {
        var fds = new int[2]; // fds[0]=read end, fds[1]=write end
        pipe(fds);
        var saved = dup(2);      // preserve original stderr
        dup2(fds[1], 2);         // redirect stderr to pipe write end
        close(fds[1]);            // drop extra reference to write end; fd 2 is the only holder
        return (saved, fds[0]);
    }

    // restores fd 2, drains and returns everything written to the pipe since Begin()
    public static string End(int savedFd, int readFd)
    {
        dup2(savedFd, 2); // restore original stderr; pipe write end now has zero references → EOF
        close(savedFd);

        var sb = new StringBuilder();
        var buf = new byte[4096];
        var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            nint n;
            while ((n = read(readFd, handle.AddrOfPinnedObject(), 4096)) > 0)
                sb.Append(Encoding.UTF8.GetString(buf, 0, (int)n));
        }
        finally
        {
            handle.Free();
            close(readFd);
        }
        return sb.ToString();
    }
}
