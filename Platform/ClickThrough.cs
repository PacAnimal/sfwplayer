using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.Logging;
using LinuxNative = SfwPlayer.Platform.Linux.Native;
using MacNative = SfwPlayer.Platform.MacOS.Native;
using WinNative = SfwPlayer.Platform.Windows.Native;

namespace SfwPlayer.Platform;

public class ClickThrough(Window window, ILogger<ClickThrough> log)
{
    private IntPtr _handle;
    private IntPtr _x11Display;

    public void Initialize()
    {
        try
        {
            var ph = window.TryGetPlatformHandle();
            if (ph == null)
            {
                log.LogWarning("no platform handle available, click-through disabled");
                return;
            }

            _handle = ph.Handle;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                _x11Display = LinuxNative.XOpenDisplay(null);
                if (_x11Display == IntPtr.Zero)
                    log.LogWarning("failed to open X11 display");
            }

            if (log.IsEnabled(LogLevel.Debug))
                log.LogDebug("click-through initialized (handle=0x{handle:x})", _handle);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "click-through init failed");
        }
    }

    public void Enable()
    {
        if (_handle == IntPtr.Zero) return;
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) EnableWindows();
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) EnableMac();
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) EnableLinux();
        }
        catch (Exception ex) { log.LogWarning(ex, "enable click-through failed"); }
    }

    public void Disable()
    {
        if (_handle == IntPtr.Zero) return;
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) DisableWindows();
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) DisableMac();
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) DisableLinux();
        }
        catch (Exception ex) { log.LogWarning(ex, "disable click-through failed"); }
    }

    public PixelPoint GetCursorPosition()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return GetCursorWindows();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return GetCursorMac();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return GetCursorLinux();
        }
        catch (Exception ex) { log.LogWarning(ex, "get cursor position failed"); }
        return default;
    }

    public IntPtr GetNSWindowHandle() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && _handle != IntPtr.Zero
            ? MacNative.objc_msgSend_ptr(_handle, MacNative.sel_registerName("window"))
            : IntPtr.Zero;

    // captured at resize start — avoids absolute Avalonia→NSWindow coordinate conversion
    private MacNative.NSRect _resizeStartNSFrame;
    private int _resizeStartAvX, _resizeStartAvY;
    private double _resizeStartAvW, _resizeStartAvH;
    private bool _hasResizeStartFrame;

    // call once from OnResizePressed to snapshot the current NSWindow frame
    public void BeginResize(PixelPoint pos, Size size)
    {
        _resizeStartAvX = pos.X; _resizeStartAvY = pos.Y;
        _resizeStartAvW = size.Width; _resizeStartAvH = size.Height;
        _hasResizeStartFrame = false;
        if (!OperatingSystem.IsMacOS() || _handle == IntPtr.Zero) return;
        var nsWin = GetNSWindow();
        if (nsWin == IntPtr.Zero) return;
        _resizeStartNSFrame = MacNative.objc_msgSend_NSRect(nsWin, MacNative.sel_registerName("frame"));
        _hasResizeStartFrame = true;
    }

    // applies position + size atomically; on macOS applies deltas to the snapshotted NSWindow
    // frame so we never need to convert Avalonia screen coords to NSWindow screen coords
    public void MoveResize(int newX, int newY, double newW, double newH)
    {
        if (OperatingSystem.IsMacOS() && _hasResizeStartFrame && _handle != IntPtr.Zero)
        {
            var nsWin = GetNSWindow();
            if (nsWin != IntPtr.Zero)
            {
                var scale = window.RenderScaling;
                // delta in Avalonia coords → delta in NSWindow points (logical pixels = points on macOS)
                var dX = (newX - _resizeStartAvX) / scale; // +right in both systems
                var dY = (newY - _resizeStartAvY) / scale; // +down in Avalonia, but NSWindow Y is +up
                var dW = newW - _resizeStartAvW;
                var dH = newH - _resizeStartAvH;
                // NSWindow.Y is the bottom edge (Y-up). derivation:
                //   newNsY = startNsY - dY - dH
                // because moving the top-left down (+dY) moves the bottom down too,
                // and increasing height also moves the bottom down (in Y-up space, both subtract from Y)
                var frame = new MacNative.NSRect
                {
                    X = _resizeStartNSFrame.X + dX,
                    Y = _resizeStartNSFrame.Y - dY - dH,
                    Width = _resizeStartNSFrame.Width + dW,
                    Height = _resizeStartNSFrame.Height + dH,
                };
                MacNative.objc_msgSend_void_NSRect_bool(nsWin, MacNative.sel_registerName("setFrame:display:"), frame, true);
                return;
            }
        }
        window.Width = newW;
        window.Height = newH;
        window.Position = new PixelPoint(newX, newY);
    }

    // uses NSWindow.mouseLocationOutsideOfEventStream which returns cursor position
    // in the window's local coordinate space — no coordinate conversion needed.
    public bool IsCursorOverWindow()
    {
        if (_handle == IntPtr.Zero) return false;
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var nsWin = GetNSWindow();
                if (nsWin == IntPtr.Zero) return false;
                var pt = MacNative.objc_msgSend_CGPoint(nsWin, MacNative.sel_registerName("mouseLocationOutsideOfEventStream"));
                var size = window.ClientSize;
                return pt.X >= 0 && pt.X < size.Width && pt.Y >= 0 && pt.Y < size.Height;
            }
        }
        catch (Exception ex) { log.LogWarning(ex, "IsCursorOverWindow failed"); }
        return false;
    }

    public bool IsLeftButtonHeld()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return (WinNative.GetAsyncKeyState(0x01) & 0x8000) != 0;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return MacNative.CGEventSourceButtonState(MacNative.kCGEventSourceStateCombinedSessionState, 0);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return IsLeftButtonLinux();
        }
        catch (Exception ex) { log.LogWarning(ex, "left button check failed"); }
        return false;
    }

    // ── Windows ──────────────────────────────────────────────────────────────

    void EnableWindows()
    {
        var s = WinNative.GetWindowLong(_handle, WinNative.GWL_EXSTYLE);
        _ = WinNative.SetWindowLong(_handle, WinNative.GWL_EXSTYLE, s | WinNative.WS_EX_TRANSPARENT | WinNative.WS_EX_LAYERED);
    }

    void DisableWindows()
    {
        var s = WinNative.GetWindowLong(_handle, WinNative.GWL_EXSTYLE);
        _ = WinNative.SetWindowLong(_handle, WinNative.GWL_EXSTYLE, s & ~WinNative.WS_EX_TRANSPARENT);
    }

    static PixelPoint GetCursorWindows()
    {
        WinNative.GetCursorPos(out var pt);
        return new PixelPoint(pt.X, pt.Y);
    }

    // ── macOS ─────────────────────────────────────────────────────────────────

    IntPtr GetNSWindow() =>
        MacNative.objc_msgSend_ptr(_handle, MacNative.sel_registerName("window"));

    void EnableMac() =>
        MacNative.objc_msgSend_void_bool(GetNSWindow(), MacNative.sel_registerName("setIgnoresMouseEvents:"), true);

    void DisableMac() =>
        MacNative.objc_msgSend_void_bool(GetNSWindow(), MacNative.sel_registerName("setIgnoresMouseEvents:"), false);

    PixelPoint GetCursorMac()
    {
        var evt = MacNative.CGEventCreate(IntPtr.Zero);
        var pt = MacNative.CGEventGetLocation(evt);
        MacNative.CFRelease(evt);
        var scale = window.RenderScaling;
        // CGEventGetLocation uses top-left origin with Y increasing downward — no conversion needed
        return new PixelPoint(
            (int)(pt.X * scale),
            (int)(pt.Y * scale));
    }

    // ── Linux X11 ────────────────────────────────────────────────────────────

    void EnableLinux()
    {
        if (_x11Display == IntPtr.Zero) return;
        // empty input shape → all clicks pass through
        LinuxNative.XShapeCombineRectangles(_x11Display, _handle, LinuxNative.ShapeInput, 0, 0, IntPtr.Zero, 0, LinuxNative.ShapeSet, 0);
    }

    void DisableLinux()
    {
        if (_x11Display == IntPtr.Zero) return;
        var scale = window.RenderScaling;
        var rect = new LinuxNative.XRectangle
        {
            X = 0,
            Y = 0,
            Width = (short)(window.ClientSize.Width * scale),
            Height = (short)(window.ClientSize.Height * scale),
        };
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<LinuxNative.XRectangle>());
        try
        {
            Marshal.StructureToPtr(rect, ptr, false);
            LinuxNative.XShapeCombineRectangles(_x11Display, _handle, LinuxNative.ShapeInput, 0, 0, ptr, 1, LinuxNative.ShapeSet, 0);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    PixelPoint GetCursorLinux()
    {
        if (_x11Display == IntPtr.Zero) return default;
        var root = LinuxNative.XRootWindow(_x11Display, LinuxNative.XDefaultScreen(_x11Display));
        _ = LinuxNative.XQueryPointer(_x11Display, root, out _, out _, out int rx, out int ry, out _, out _, out _);
        return new PixelPoint(rx, ry);
    }

    bool IsLeftButtonLinux()
    {
        if (_x11Display == IntPtr.Zero) return false;
        var root = LinuxNative.XRootWindow(_x11Display, LinuxNative.XDefaultScreen(_x11Display));
        _ = LinuxNative.XQueryPointer(_x11Display, root, out _, out _, out _, out _, out _, out _, out uint mask);
        return (mask & 256u) != 0; // Button1Mask
    }

}
