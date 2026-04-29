using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SfwPlayer.Platform;
using SfwPlayer.Platform.MacOS;
using SfwPlayer.Services;

namespace SfwPlayer;

public partial class MainWindow : Window
{
    private readonly ILogger<MainWindow> _log;
    private readonly YoutubeService _youtube;
    private readonly ClickThrough _clickThrough;
    private readonly CancellationTokenSource _cts = new();

    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private Media? _currentMedia;

    private long _totalMs;
    private long _currentMs;
    private bool _isMuted;

    private bool _isLocked = false;
    private bool _isHovering;
    private bool _isDragging;
    private double _targetOpacity = 1.0;
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    private WindowEdge _resizeEdge;
    private PixelPoint _resizeCursorStart;
    private PixelPoint _resizePosStart;
    private Size _resizeSizeStart;
    private readonly DispatcherTimer _resizeTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };

    private ControlsOverlayWindow? _overlay;
    private IntPtr _overlayNsWin;

    public MainWindow(IServiceProvider services)
    {
        InitializeComponent();
        _log = services.GetRequiredService<ILogger<MainWindow>>();
        _youtube = services.GetRequiredService<YoutubeService>();
        _clickThrough = new ClickThrough(this, services.GetRequiredService<ILogger<ClickThrough>>());

        _pollTimer.Tick += OnPollTick;
        _resizeTimer.Tick += OnResizeTick;

        KeyDown += OnKeyDown;
        Opened += OnOpened;
        Closing += OnClosing;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        PositionBottomRight();
        _clickThrough.Initialize();
        _pollTimer.Start();
        InitializeVlc();
        CreateOverlay();

        try
        {
            var url = await _youtube.GetStreamUrl(App.OverrideUrl, _cts.Token);
            _currentMedia = new Media(_libVlc!, new Uri(url));
            _mediaPlayer!.Play(_currentMedia);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "failed to load video");
        }
    }

    private void CreateOverlay()
    {
        _overlay = new ControlsOverlayWindow(new OverlayCallbacks(
            OnClose: Close,
            OnPlayPause: () => OnPlayPauseClicked(null, null!),
            OnMute: () => OnMuteClicked(null, null!),
            OnVolumeChanged: v =>
            {
                if (_mediaPlayer == null) return;
                _mediaPlayer.Volume = (int)v;
                if (_isMuted && v > 0)
                {
                    _isMuted = false;
                    _mediaPlayer.Mute = false;
                    _overlay?.UpdateMuteState(false);
                }
            },
            OnOpacityChanged: v =>
            {
                _targetOpacity = v;
                ApplyWindowOpacity(v);
            },
            OnOpacityReleased: () =>
            {
                if (_isHovering && !_isLocked) ApplyWindowOpacity(1.0);
            },
            OnSeekReleased: pos =>
            {
                if (_mediaPlayer is { } mp) mp.Position = pos;
            },
            OnStartDrag: _ =>
            {
                if (_isLocked) return;
                StartDrag();
                _isDragging = true;
                UpdateOverlayState();
            },
            OnResize: edge => StartResize(edge),
            OnLockToggled: () =>
            {
                _isLocked = !_isLocked;
                UpdateOverlayState();
            }
        ))
        {
            Position = Position,
            Width = Width,
            Height = Height,
        };

        _overlay.Opened += OnOverlayOpened;
        _overlay.Show();

        // keep overlay in sync with main window (no longer a child window so no auto-follow)
        SizeChanged += (_, args) =>
        {
            _overlay.Width = args.NewSize.Width;
            _overlay.Height = args.NewSize.Height;
        };
        PositionChanged += (_, _) => _overlay.Position = Position;
    }

    // macOS: perform window drag using the current NSEvent on the main window.
    // BeginMoveDrag(e) with an event from a different window is ignored by Avalonia.
    private void StartDrag()
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            var mainNsWin = _clickThrough.GetNSWindowHandle();
            if (mainNsWin == IntPtr.Zero) return;
            var nsApp = Native.objc_msgSend_ptr(Native.objc_getClass("NSApplication"), Native.sel_registerName("sharedApplication"));
            var currentEvent = Native.objc_msgSend_ptr(nsApp, Native.sel_registerName("currentEvent"));
            if (currentEvent == IntPtr.Zero) return;
            Native.objc_msgSend_void_ptr(mainNsWin, Native.sel_registerName("performWindowDragWithEvent:"), currentEvent);
        }
        catch (Exception ex) { _log.LogWarning(ex, "native drag failed"); }
    }

    // Avalonia's Window.Opacity only scales the Skia layer; VLC's native NSView is unaffected.
    // Use NSWindow.setAlphaValue: directly so the video becomes semi-transparent too.
    private void ApplyWindowOpacity(double v)
    {
        if (!OperatingSystem.IsMacOS())
        {
            Opacity = v;
            return;
        }
        try
        {
            var mainNsWin = _clickThrough.GetNSWindowHandle();
            if (mainNsWin != IntPtr.Zero)
                Native.objc_msgSend_void_double(mainNsWin, Native.sel_registerName("setAlphaValue:"), v);
        }
        catch (Exception ex) { _log.LogWarning(ex, "setAlphaValue failed"); }
    }

    private void OnOverlayOpened(object? sender, EventArgs e)
    {
        _overlay!.Opened -= OnOverlayOpened;
        if (OperatingSystem.IsMacOS())
            EnsureOverlayAboveMain();
    }

    // macOS: make overlay a child window of main so it's always above VLC's NSView subview.
    // child windows automatically follow parent position — no PositionChanged sync needed.
    private void EnsureOverlayAboveMain()
    {
        try
        {
            var mainNsWin = _clickThrough.GetNSWindowHandle();
            var overlayHandle = _overlay!.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (mainNsWin == IntPtr.Zero || overlayHandle == IntPtr.Zero) return;
            var overlayNsWin = Native.objc_msgSend_ptr(overlayHandle, Native.sel_registerName("window"));
            if (overlayNsWin == IntPtr.Zero) return;
            _overlayNsWin = overlayNsWin;
            // place overlay above main window without using addChildWindow, which on macOS 26
            // routes child events through the parent instead of delivering them directly
            var mainLevel = Native.objc_msgSend_nint(mainNsWin, Native.sel_registerName("level"));
            Native.objc_msgSend_void_nint(overlayNsWin, Native.sel_registerName("setLevel:"), mainLevel + 1);
            Native.objc_msgSend_void_ptr(overlayNsWin, Native.sel_registerName("orderFront:"), IntPtr.Zero);
        }
        catch (Exception ex) { _log.LogWarning(ex, "addChildWindow failed"); }
    }

    private void PositionBottomRight()
    {
        var screen = Screens.Primary;
        if (screen == null) return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        Position = new PixelPoint(
            area.X + area.Width - (int)(Width * scale),
            area.Y + area.Height - (int)(Height * scale));
    }

    private void InitializeVlc()
    {
        _libVlc = new LibVLC(false, [.. VlcSetup.GetArgs(), .. App.ExtraVlcArgs]);
        _mediaPlayer = new MediaPlayer(_libVlc);
        VideoView.MediaPlayer = _mediaPlayer;

        _mediaPlayer.Playing += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            _overlay?.UpdatePlayState(true);
            _overlay?.SetLoadingVisible(false);
        });

        _mediaPlayer.Paused += (_, _) =>
            Dispatcher.UIThread.Post(() => _overlay?.UpdatePlayState(false));

        _mediaPlayer.Stopped += (_, _) =>
            Dispatcher.UIThread.Post(() => _overlay?.UpdatePlayState(false));

        _mediaPlayer.PositionChanged += (_, ev) =>
            Dispatcher.UIThread.Post(() => _overlay?.UpdateSeekPosition(ev.Position));

        _mediaPlayer.LengthChanged += (_, ev) =>
            Dispatcher.UIThread.Post(() =>
            {
                _totalMs = ev.Length;
                _overlay?.UpdateTime(_currentMs, _totalMs);
            });

        _mediaPlayer.TimeChanged += (_, ev) =>
            Dispatcher.UIThread.Post(() =>
            {
                _currentMs = ev.Time;
                _overlay?.UpdateTime(_currentMs, _totalMs);
            });

        _mediaPlayer.EndReached += (_, _) =>
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (App.ExitOnDone)
                {
                    Dispatcher.UIThread.Post(() =>
                        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown(0));
                    return;
                }
                if (_cts.IsCancellationRequested || _currentMedia == null) return;
                _mediaPlayer.Stop();
                _mediaPlayer.Play(_currentMedia);
            });

        _mediaPlayer.Volume = _overlay?.GetInitialVolume() ?? 80;
    }

    private void UpdateOverlayState()
    {
        var interactive = _isHovering && !_isDragging && !_resizeTimer.IsEnabled;
        var showControls = interactive && !_isLocked;

        if (showControls)
        {
            _overlay?.ShowControls();
            _clickThrough.Disable();
            ApplyWindowOpacity(1.0);
        }
        else
        {
            _overlay?.HideControls();
            _clickThrough.Enable();
            ApplyWindowOpacity(interactive ? 1.0 : _targetOpacity);
        }

        if (interactive)
            _overlay?.ShowPadlock(_isLocked);
        else
            _overlay?.HidePadlock();

        if (OperatingSystem.IsMacOS() && _overlayNsWin != IntPtr.Zero)
            Native.objc_msgSend_void_bool(_overlayNsWin, Native.sel_registerName("setIgnoresMouseEvents:"), !interactive);
    }

    private void OnPollTick(object? sender, EventArgs e)
    {
        if (_isDragging && !_clickThrough.IsLeftButtonHeld())
        {
            _isDragging = false;
            UpdateOverlayState();
        }

        // re-enforce overlay interactivity every tick — Avalonia may reset setIgnoresMouseEvents
        if (OperatingSystem.IsMacOS() && _overlayNsWin != IntPtr.Zero)
        {
            var interactive = _isHovering && !_isDragging && !_resizeTimer.IsEnabled;
            Native.objc_msgSend_void_bool(_overlayNsWin, Native.sel_registerName("setIgnoresMouseEvents:"), !interactive);
        }

        bool isOver;
        if (OperatingSystem.IsMacOS())
        {
            isOver = _clickThrough.IsCursorOverWindow();
        }
        else
        {
            var cursor = _clickThrough.GetCursorPosition();
            var scale = RenderScaling;
            var bounds = new PixelRect(
                Position.X, Position.Y,
                (int)(ClientSize.Width * scale),
                (int)(ClientSize.Height * scale));
            isOver = bounds.Contains(cursor);
        }

        if (isOver == _isHovering) return;
        _isHovering = isOver;
        UpdateOverlayState();
    }

    private void OnPlayPauseClicked(object? sender, RoutedEventArgs e)
    {
        if (_mediaPlayer == null) return;
        if (_mediaPlayer.IsPlaying) _mediaPlayer.Pause();
        else _mediaPlayer.Play();
    }

    private void OnMuteClicked(object? sender, RoutedEventArgs e)
    {
        if (_mediaPlayer == null) return;
        _isMuted = !_isMuted;
        _mediaPlayer.Mute = _isMuted;
        _overlay?.UpdateMuteState(_isMuted);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space:
                OnPlayPauseClicked(null, null!);
                break;
            case Key.M:
                OnMuteClicked(null, null!);
                break;
            case Key.Escape:
                Close();
                break;
        }
    }

    private void StartResize(WindowEdge edge)
    {
        _resizeEdge = edge;
        _resizeCursorStart = _clickThrough.GetCursorPosition();
        _resizePosStart = Position;
        _resizeSizeStart = new Size(Width, Height);
        _resizeTimer.Start();
        UpdateOverlayState();
    }

    private void StopResize()
    {
        _resizeTimer.Stop();
        UpdateOverlayState();
    }

    private void OnResizeTick(object? sender, EventArgs e)
    {
        if (!_clickThrough.IsLeftButtonHeld()) { StopResize(); return; }

        var cursor = _clickThrough.GetCursorPosition();
        var scale = RenderScaling;
        var dxDip = (cursor.X - _resizeCursorStart.X) / scale;
        var dyDip = (cursor.Y - _resizeCursorStart.Y) / scale;

        var x = _resizePosStart.X;
        var y = _resizePosStart.Y;
        var w = _resizeSizeStart.Width;
        var h = _resizeSizeStart.Height;

        switch (_resizeEdge)
        {
            case WindowEdge.East:
                w = Math.Max(MinWidth, w + dxDip);
                break;
            case WindowEdge.West:
                w = Math.Max(MinWidth, w - dxDip);
                x = _resizePosStart.X + (int)((_resizeSizeStart.Width - w) * scale);
                break;
            case WindowEdge.South:
                h = Math.Max(MinHeight, h + dyDip);
                break;
            case WindowEdge.North:
                h = Math.Max(MinHeight, h - dyDip);
                y = _resizePosStart.Y + (int)((_resizeSizeStart.Height - h) * scale);
                break;
            case WindowEdge.SouthEast:
                w = Math.Max(MinWidth, w + dxDip);
                h = Math.Max(MinHeight, h + dyDip);
                break;
            case WindowEdge.SouthWest:
                w = Math.Max(MinWidth, w - dxDip);
                h = Math.Max(MinHeight, h + dyDip);
                x = _resizePosStart.X + (int)((_resizeSizeStart.Width - w) * scale);
                break;
            case WindowEdge.NorthEast:
                w = Math.Max(MinWidth, w + dxDip);
                h = Math.Max(MinHeight, h - dyDip);
                y = _resizePosStart.Y + (int)((_resizeSizeStart.Height - h) * scale);
                break;
            case WindowEdge.NorthWest:
                w = Math.Max(MinWidth, w - dxDip);
                h = Math.Max(MinHeight, h - dyDip);
                x = _resizePosStart.X + (int)((_resizeSizeStart.Width - w) * scale);
                y = _resizePosStart.Y + (int)((_resizeSizeStart.Height - h) * scale);
                break;
        }

        Position = new PixelPoint(x, y);
        Width = w;
        Height = h;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        _cts.Cancel();
        _pollTimer.Stop();
        _resizeTimer.Stop();
        _mediaPlayer?.Stop();
        _currentMedia?.Dispose();
        _mediaPlayer?.Dispose();
        _libVlc?.Dispose();
        _overlay?.Close();
    }
}
