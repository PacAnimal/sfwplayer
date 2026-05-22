using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Tests.Setup;

namespace Tests.Auth;

[TestFixture]
public class WkWebViewCookieTests
{
    // Spins up a minimal in-process HTTP server, then spawns SfwPlayer in --wktest mode.
    // The subprocess runs on its OS main thread, navigates a WKWebView to the local server,
    // and prints the extracted cookies to stdout. We assert that SfwTestCookie appears.
    //
    // The subprocess approach is required because WKNavigationDelegate callbacks are delivered
    // on the GCD main queue, which requires the macOS main thread's run loop to be pumped.
    // dotnet test runs all tests on background threads with no such run loop.
    [Test]
    public async Task NavigateAndExtract_CookieSetByServer_IsReturned()
    {
        if (!OperatingSystem.IsMacOS()) Assert.Ignore("macOS only");

        var sfwExe = SubprocessHelper.FindSfwPlayerExe();
        if (sfwExe == null)
            Assert.Ignore("SfwPlayer executable not found; build the main project first");

        var port = GetFreePort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _ = ServeAsync(listener);

        var url = $"http://127.0.0.1:{port}/";
        var foundCookie = false;

        try
        {
            var psi = new ProcessStartInfo(sfwExe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--wktest");
            psi.ArgumentList.Add(url);

            using var proc = Process.Start(psi)!;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            try
            {
                string? line;
                while ((line = await proc.StandardOutput.ReadLineAsync(cts.Token)) != null)
                {
                    if (line == "COOKIE:SfwTestCookie=roundtrip_ok") foundCookie = true;
                    if (line == "DONE") break;
                }
            }
            catch (OperationCanceledException)
            {
                // timeout — fall through, assert will fail with useful message
            }
            finally
            {
                if (!proc.HasExited) proc.Kill();
            }
        }
        finally
        {
            listener.Stop();
        }

        Assert.That(foundCookie, Is.True,
            "SfwPlayer --wktest should have extracted SfwTestCookie from the local HTTP server via WKWebView");
    }

    private static async Task ServeAsync(HttpListener listener)
    {
        try
        {
            while (listener.IsListening)
            {
                var ctx = await listener.GetContextAsync();
                ctx.Response.Headers["Set-Cookie"] = "SfwTestCookie=roundtrip_ok; Path=/";
                ctx.Response.ContentType = "text/html";
                await ctx.Response.OutputStream.WriteAsync("<html><body>ok</body></html>"u8.ToArray());
                ctx.Response.Close();
            }
        }
        catch { /* listener stopped */ }
    }

    private static int GetFreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }
}
