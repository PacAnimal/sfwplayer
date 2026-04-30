using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SfwPlayer.Platform;
using SfwPlayer.Services;

namespace SfwPlayer;

public partial class MainWindow : Window
{
    private readonly ILogger<MainWindow> _log;
    private readonly YoutubeService _youtube;
    private readonly ClickThrough _clickThrough;
    private readonly CancellationTokenSource _cts = new();

    private VlcVideoBridge? _bridge;
    private bool _isMuted;
    private bool _isLocked;
    private bool _isHovering;
    private bool _isSeeking;
    private double _targetOpacity = 1.0;
    private long _totalMs;
    private long _currentMs;

    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _moveTimer = new() { Interval = TimeSpan.FromMilliseconds(8) };
    private PixelPoint _moveStartCursor;
    private PixelPoint _moveStartPos;
    private WindowEdge _resizeEdge;
    private Size _resizeStartSize;

    private static readonly Transitions FadeOutControls =
    [
        new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(200) },
    ];
    private static readonly Transitions FadeOutPadlock =
    [
        new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(400) },
    ];

    public MainWindow(IServiceProvider services)
    {
        InitializeComponent();
        _log = services.GetRequiredService<ILogger<MainWindow>>();
        _youtube = services.GetRequiredService<YoutubeService>();
        _clickThrough = new ClickThrough(this, services.GetRequiredService<ILogger<ClickThrough>>());

        _pollTimer.Tick += OnPollTick;
        _moveTimer.Tick += OnMoveTick;

        SeekBar.AddHandler(PointerPressedEvent, (_, _) => _isSeeking = true, RoutingStrategies.Tunnel);
        SeekBar.AddHandler(PointerReleasedEvent, (_, _) =>
        {
            _isSeeking = false;
            if (_bridge is { } b) b.Player.Position = (float)SeekBar.Value;
        }, RoutingStrategies.Tunnel);
        SeekBar.ValueChanged += (_, e) =>
        {
            if (_isSeeking && _totalMs > 0)
                TimeLabel.Text = $"{Fmt((long)(e.NewValue * _totalMs))} / {Fmt(_totalMs)}";
        };

        VolumeSlider.ValueChanged += (_, e) =>
        {
            if (_bridge is { } b) b.Player.Volume = (int)e.NewValue;
            if (_isMuted && e.NewValue > 0)
            {
                _isMuted = false;
                if (_bridge is { } b2) b2.Player.Mute = false;
                MuteButton.Content = "🔊";
            }
        };

        OpacitySlider.ValueChanged += (_, e) =>
        {
            _targetOpacity = e.NewValue;
            Opacity = e.NewValue;
        };
        OpacitySlider.AddHandler(PointerReleasedEvent,
            (_, _) => { if (_isHovering && !_isLocked) Opacity = 1.0; },
            RoutingStrategies.Tunnel);

        KeyDown += OnKeyDown;
        Opened += OnOpened;
        Closing += OnClosing;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        PositionBottomRight();
        _clickThrough.Initialize();
        _pollTimer.Start();
        InitializeBridge();

        try
        {
            var url = await _youtube.GetStreamUrl(App.OverrideUrl, _cts.Token);
            _bridge!.Play(url);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "failed to load video");
        }
    }

    private void InitializeBridge()
    {
        _bridge = new VlcVideoBridge([.. VlcSetup.GetArgs(), .. App.ExtraVlcArgs]);
        _bridge.BitmapSourceChanged = () => VideoImage.Source = _bridge.Bitmap;
        _bridge.FrameReady = VideoImage.InvalidateVisual;
        _bridge.Player.Volume = (int)VolumeSlider.Value;

        _bridge.Player.Playing += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            PlayPauseButton.Content = "⏸";
            LoadingLabel.IsVisible = false;
        });
        _bridge.Player.Paused += (_, _) =>
            Dispatcher.UIThread.Post(() => PlayPauseButton.Content = "▶");
        _bridge.Player.Stopped += (_, _) =>
            Dispatcher.UIThread.Post(() => PlayPauseButton.Content = "▶");

        _bridge.Player.LengthChanged += (_, ev) =>
            Dispatcher.UIThread.Post(() => { _totalMs = ev.Length; UpdateTimeLabel(); });
        _bridge.Player.TimeChanged += (_, ev) =>
            Dispatcher.UIThread.Post(() => { _currentMs = ev.Time; UpdateTimeLabel(); });
        _bridge.Player.PositionChanged += (_, ev) =>
            Dispatcher.UIThread.Post(() => { if (!_isSeeking) SeekBar.Value = ev.Position; });

        _bridge.Player.EncounteredError += (_, _) =>
            Dispatcher.UIThread.Post(() => _log.LogError("vlc encountered an error during playback"));

        _bridge.Player.EndReached += (_, _) =>
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (App.ExitOnDone)
                {
                    Dispatcher.UIThread.Post(() =>
                        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
                            ?.Shutdown(0));
                    return;
                }
                if (!_cts.IsCancellationRequested) _bridge.Replay();
            });
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

    private void OnPollTick(object? sender, EventArgs e)
    {
        bool isOver;
        if (OperatingSystem.IsMacOS())
            isOver = _clickThrough.IsCursorOverWindow();
        else
        {
            var cursor = _clickThrough.GetCursorPosition();
            var scale = RenderScaling;
            var bounds = new PixelRect(Position.X, Position.Y,
                (int)(ClientSize.Width * scale), (int)(ClientSize.Height * scale));
            isOver = bounds.Contains(cursor);
        }

        if (isOver != _isHovering)
        {
            _isHovering = isOver;
            UpdateState();
        }
        ApplyClickThrough();
    }

    private void UpdateState()
    {
        var showControls = _isHovering && !_isLocked;

        if (showControls)
        {
            ControlsGrid.Transitions = null;
            ControlsGrid.Opacity = 1.0;
            ControlsGrid.IsHitTestVisible = true;
            Opacity = 1.0;
        }
        else
        {
            ControlsGrid.Transitions = FadeOutControls;
            ControlsGrid.Opacity = 0.0;
            ControlsGrid.IsHitTestVisible = false;
            Opacity = _targetOpacity;
        }

        if (_isLocked)
        {
            // locked: padlock always visible at low opacity so user can always find and click it
            LockClosedIcon.IsVisible = true;
            LockOpenIcon.IsVisible = false;
            PadlockButton.Transitions = null;
            PadlockButton.Opacity = 0.45;
            ResizeHandles.IsVisible = false;
        }
        else if (_isHovering)
        {
            LockClosedIcon.IsVisible = false;
            LockOpenIcon.IsVisible = true;
            PadlockButton.Transitions = null;
            PadlockButton.Opacity = 0.85;
            ResizeHandles.IsVisible = true;
        }
        else
        {
            PadlockButton.Transitions = FadeOutPadlock;
            PadlockButton.Opacity = 0.0;
            ResizeHandles.IsVisible = false;
        }

        ApplyClickThrough();
    }

    // when locked, click-through is enabled everywhere except over the padlock button
    // so the user can still click it to unlock; when unlocked, click-through only when not hovering
    private void ApplyClickThrough()
    {
        var passThrough = _isLocked ? !IsCursorOverPadlock() : !_isHovering;
        if (passThrough) _clickThrough.Enable(); else _clickThrough.Disable();
    }

    private bool IsCursorOverPadlock()
    {
        var cursor = _clickThrough.GetCursorPosition();
        var scale = RenderScaling;
        return cursor.X >= Position.X && cursor.X < Position.X + (int)(40 * scale)
            && cursor.Y >= Position.Y && cursor.Y < Position.Y + (int)(40 * scale);
    }

    private void UpdateTimeLabel() =>
        TimeLabel.Text = $"{Fmt(_currentMs)} / {Fmt(_totalMs)}";

    private static string Fmt(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    private void OnDragPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        WindowEdge? edge = ((sender as Control)?.Tag as string) switch
        {
            "NorthWest" => WindowEdge.NorthWest,
            "North" => WindowEdge.North,
            "NorthEast" => WindowEdge.NorthEast,
            "East" => WindowEdge.East,
            "SouthEast" => WindowEdge.SouthEast,
            "South" => WindowEdge.South,
            "SouthWest" => WindowEdge.SouthWest,
            "West" => WindowEdge.West,
            _ => null,
        };
        if (!edge.HasValue) return;
        _resizeEdge = edge.Value;
        _moveStartCursor = _clickThrough.GetCursorPosition();
        _moveStartPos = Position;
        _resizeStartSize = ClientSize;
        _moveTimer.Start();
    }

    private void OnMoveTick(object? sender, EventArgs e)
    {
        if (!_clickThrough.IsLeftButtonHeld())
        {
            _moveTimer.Stop();
            return;
        }

        var cursor = _clickThrough.GetCursorPosition();
        var dx = cursor.X - _moveStartCursor.X;
        var dy = cursor.Y - _moveStartCursor.Y;
        var scale = RenderScaling;

        var newX = _moveStartPos.X;
        var newY = _moveStartPos.Y;
        var newW = _resizeStartSize.Width;
        var newH = _resizeStartSize.Height;

        var westEdge = _resizeEdge is WindowEdge.West or WindowEdge.NorthWest or WindowEdge.SouthWest;
        var eastEdge = _resizeEdge is WindowEdge.East or WindowEdge.NorthEast or WindowEdge.SouthEast;
        var northEdge = _resizeEdge is WindowEdge.North or WindowEdge.NorthWest or WindowEdge.NorthEast;
        var southEdge = _resizeEdge is WindowEdge.South or WindowEdge.SouthWest or WindowEdge.SouthEast;

        if (westEdge) { newW -= dx / scale; newX += dx; }
        if (eastEdge) { newW += dx / scale; }
        if (northEdge) { newH -= dy / scale; newY += dy; }
        if (southEdge) { newH += dy / scale; }

        const double minW = 160;
        const double minH = 90;
        if (newW < minW)
        {
            if (westEdge) newX = _moveStartPos.X + (int)((_resizeStartSize.Width - minW) * scale);
            newW = minW;
        }
        if (newH < minH)
        {
            if (northEdge) newY = _moveStartPos.Y + (int)((_resizeStartSize.Height - minH) * scale);
            newH = minH;
        }

        Width = newW;
        Height = newH;
        Position = new PixelPoint(newX, newY);
    }

    private void OnPadlockClicked(object? sender, RoutedEventArgs e)
    {
        _isLocked = !_isLocked;
        UpdateState();
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();

    private void OnPlayPauseClicked(object? sender, RoutedEventArgs e)
    {
        if (_bridge is not { } b) return;
        if (b.Player.IsPlaying) b.Player.Pause();
        else b.Player.Play();
    }

    private void OnMuteClicked(object? sender, RoutedEventArgs e)
    {
        if (_bridge is not { } b) return;
        _isMuted = !_isMuted;
        b.Player.Mute = _isMuted;
        MuteButton.Content = _isMuted ? "🔇" : "🔊";
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space: OnPlayPauseClicked(null, null!); break;
            case Key.M: OnMuteClicked(null, null!); break;
            case Key.Escape: Close(); break;
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        _cts.Cancel();
        _pollTimer.Stop();
        _moveTimer.Stop();
        _bridge?.Dispose();
    }
}
