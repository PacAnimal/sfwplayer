#if IS_WINDOWS
#pragma warning disable CA1416 // all code is gated with OperatingSystem.IsWindows()
#pragma warning disable SYSLIB1054 // DllImport used for comctl32 (LibraryImport doesn't support delegate parameters)
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace SfwPlayer.Platform.Windows;

internal static partial class WindowsWebAuth
{
    internal const string GoogleSignInUrl =
        "https://accounts.google.com/ServiceLogin?service=youtube&uilel=3&passive=true&continue=https://www.youtube.com/";

    private static string UserDataPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SfwPlayer", "WebView2");

    private const uint WM_SIZE = 0x0005;
    private delegate nint SubclassProc(IntPtr hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint refData);

    internal static async Task<List<Cookie>?> SignInInWindowAsync(IntPtr hwnd, CancellationToken ct = default)
    {
        var env = await CoreWebView2Environment.CreateAsync(null, UserDataPath);
        var controller = await env.CreateCoreWebView2ControllerAsync(hwnd);

        GetClientRect(hwnd, out var rect);
        controller.Bounds = new System.Drawing.Rectangle(0, 0, rect.Right, rect.Bottom);
        controller.IsVisible = true;

        // resize WebView2 with the parent window
        nint OnSize(IntPtr h, uint msg, nint wp, nint lp, nuint id, nuint _)
        {
            if (msg == WM_SIZE)
                controller.Bounds = new System.Drawing.Rectangle(0, 0, (int)(lp & 0xFFFF), (int)((uint)lp >> 16));
            return DefSubclassProc(h, msg, wp, lp);
        }
        SubclassProc subclassProc = OnSize;
        SetWindowSubclass(hwnd, subclassProc, 1, 0);

        try
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<CoreWebView2NavigationCompletedEventArgs> onNav = null!;
            onNav = (_, _) =>
            {
                var url = controller.CoreWebView2.Source;
                if (url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) &&
                    !url.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase))
                {
                    controller.CoreWebView2.NavigationCompleted -= onNav;
                    tcs.TrySetResult(true);
                }
            };
            controller.CoreWebView2.NavigationCompleted += onNav;

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var combined = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            using var reg = combined.Token.Register(() => tcs.TrySetResult(false));

            controller.CoreWebView2.Navigate(GoogleSignInUrl);
            var success = await tcs.Task;

            if (!success) return null;

            var ytCookies = await controller.CoreWebView2.CookieManager.GetCookiesAsync("https://www.youtube.com");
            var gCookies = await controller.CoreWebView2.CookieManager.GetCookiesAsync("https://accounts.google.com");
            return [.. ytCookies.Concat(gCookies).Select(TryToNetCookie).OfType<Cookie>()];
        }
        finally
        {
            RemoveWindowSubclass(hwnd, subclassProc, 1);
            try { controller.Close(); } catch { }
        }
    }

    internal static void ClearSession()
    {
        try
        {
            if (Directory.Exists(UserDataPath))
                Directory.Delete(UserDataPath, recursive: true);
        }
        catch { }
    }

    private static Cookie? TryToNetCookie(CoreWebView2Cookie c)
    {
        try
        {
            var path = string.IsNullOrEmpty(c.Path) ? "/" : c.Path;
            var cookie = new Cookie(c.Name, c.Value, path, c.Domain) { Secure = c.IsSecure, HttpOnly = c.IsHttpOnly };
            if (c.Expires != DateTime.MinValue && c.Expires > DateTime.UtcNow)
                cookie.Expires = c.Expires;
            return cookie;
        }
        catch { return null; }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, nuint id, nuint refData);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(IntPtr hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
}
#endif
