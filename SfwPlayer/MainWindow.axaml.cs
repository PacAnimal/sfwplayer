using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SfwPlayer.Models;
using SfwPlayer.Platform;
using SfwPlayer.Platform.MacOS;
using SfwPlayer.Services;
using SfwPlayer.Views;

namespace SfwPlayer;

public partial class MainWindow : Window
{
    private readonly ILogger<MainWindow> _log;
    private readonly IServiceProvider _services;
    private readonly YoutubeService _youtube;
    private readonly ClickThrough _clickThrough;
    private readonly CancellationTokenSource _cts = new();

    private List<VideoInfo> _queue = [];
    private int _queueIndex = -1;
    private CancellationTokenSource _playCts = new();

    private VlcVideoBridge? _bridge;
    private PlaylistPickerWindow? _picker;
    private bool _isMuted;
    private bool _isLocked;
    private bool _isHovering;
    private bool _isSeeking;
    private bool _seekTrackDragging;
    private double _targetOpacity = 1.0;
    private long _totalMs;
    private long _currentMs;

    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _resizeTimer = new() { Interval = TimeSpan.FromMilliseconds(8) };
    private PixelPoint _resizeStartCursor;
    private PixelPoint _resizeStartPos;
    private Size _resizeStartSize;
    private WindowEdge _resizeEdge;

    private PixelPoint _videoPressWindowPos;
    private DateTime _videoPressTime;

    private static readonly Transitions FadeOutControls =
    [
        new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(200) },
    ];
    private static readonly Transitions FadeOutPadlock =
    [
        new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(400) },
    ];
    private static readonly Transitions FadePadlockHover =
    [
        new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(150) },
    ];

    public MainWindow(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
        _log = services.GetRequiredService<ILogger<MainWindow>>();
        _youtube = services.GetRequiredService<YoutubeService>();
        _clickThrough = new ClickThrough(this, services.GetRequiredService<ILogger<ClickThrough>>());

        _pollTimer.Tick += OnPollTick;
        _resizeTimer.Tick += OnResizeTick;

        SeekBar.AddHandler(PointerPressedEvent, (_, e) =>
        {
            _isSeeking = true;
            if (!IsThumbPress(e))
            {
                _seekTrackDragging = true;
                SeekBar.Value = SliderValueAt(SeekBar, e.GetCurrentPoint(SeekBar).Position.X);
                e.Pointer.Capture(SeekBar);
            }
        }, RoutingStrategies.Tunnel);
        SeekBar.AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (_seekTrackDragging)
                SeekBar.Value = SliderValueAt(SeekBar, e.GetCurrentPoint(SeekBar).Position.X);
        }, RoutingStrategies.Tunnel | RoutingStrategies.Direct);
        SeekBar.AddHandler(PointerReleasedEvent, (_, _) =>
        {
            if (!_isSeeking) return;
            _isSeeking = false;
            _seekTrackDragging = false;
            if (_bridge is { } b) b.Player.Position = (float)SeekBar.Value;
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        SeekBar.ValueChanged += (_, e) =>
        {
            if (_isSeeking && _totalMs > 0)
                TimeLabel.Text = $"{Fmt((long)(e.NewValue * _totalMs))} / {Fmt(_totalMs)}";
        };

        bool volTrackDragging = false;
        VolumeSlider.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (IsThumbPress(e)) return;
            volTrackDragging = true;
            VolumeSlider.Value = SliderValueAt(VolumeSlider, e.GetCurrentPoint(VolumeSlider).Position.X);
            e.Pointer.Capture(VolumeSlider);
        }, RoutingStrategies.Tunnel);
        VolumeSlider.AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (volTrackDragging)
                VolumeSlider.Value = SliderValueAt(VolumeSlider, e.GetCurrentPoint(VolumeSlider).Position.X);
        }, RoutingStrategies.Tunnel | RoutingStrategies.Direct);
        VolumeSlider.AddHandler(PointerReleasedEvent, (_, _) => volTrackDragging = false,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        VolumeSlider.ValueChanged += (_, e) =>
        {
            if (_bridge is { } b) b.Player.Volume = (int)e.NewValue;
            if (_isMuted && e.NewValue > 0)
            {
                _isMuted = false;
                if (_bridge is { } b2) b2.Player.Mute = false;
                VolumeIcon.IsVisible = true;
                MuteIcon.IsVisible = false;
            }
        };

        bool opTrackDragging = false;
        OpacitySlider.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (IsThumbPress(e)) return;
            opTrackDragging = true;
            OpacitySlider.Value = SliderValueAt(OpacitySlider, e.GetCurrentPoint(OpacitySlider).Position.X);
            e.Pointer.Capture(OpacitySlider);
        }, RoutingStrategies.Tunnel);
        OpacitySlider.AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (opTrackDragging)
                OpacitySlider.Value = SliderValueAt(OpacitySlider, e.GetCurrentPoint(OpacitySlider).Position.X);
        }, RoutingStrategies.Tunnel | RoutingStrategies.Direct);
        OpacitySlider.AddHandler(PointerReleasedEvent, (_, _) =>
        {
            opTrackDragging = false;
            if (_isHovering && !_isLocked) Opacity = 1.0;
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        OpacitySlider.ValueChanged += (_, e) =>
        {
            _targetOpacity = e.NewValue;
            Opacity = e.NewValue;
        };

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
        SaveTestCredentialsMenuItem.IsVisible = System.Diagnostics.Debugger.IsAttached;

        if (App.OverrideUrl != null)
        {
            LoadingLabel.Text = "Loading...";
            try
            {
                var url = await _youtube.GetStreamUrl(App.OverrideUrl, _cts.Token);
                _bridge!.Play(url);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.LogError(ex, "failed to load video");
                ShowHint();
            }
        }
    }

    private async Task PlayQueueItemAsync(int index)
    {
        if (index < 0 || index >= _queue.Count) return;

        _playCts.Cancel();
        _playCts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, _playCts.Token);

        _queueIndex = index;
        var video = _queue[index];

        TrackTitle.Text = video.Title;
        TrackTitle.IsVisible = true;
        LoadingLabel.Text = "Loading...";
        LoadingLabel.IsVisible = true;
        UpdateQueueMenuItems();

        try
        {
            var url = await _youtube.GetStreamUrl(video.Id, linked.Token);
            _bridge!.Play(url);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "failed to load video {id}", video.Id);
            LoadingLabel.IsVisible = false;
        }
    }

    private void UpdateQueueMenuItems()
    {
        PrevMenuItem.IsEnabled = _queue.Count > 0 && _queueIndex > 0;
        NextMenuItem.IsEnabled = _queue.Count > 0 && _queueIndex < _queue.Count - 1;
    }

    private async void OnSelectPlaylistMenuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _picker = new PlaylistPickerWindow(_services);
        _picker.PlayRequested += OnPickerPlayRequested;
        var result = await _picker.ShowDialog<PlaybackRequest?>(this);
        _picker = null;
        if (result == null) return;

        var videos = result.Shuffle
            ? [.. result.Videos.OrderBy(_ => Random.Shared.Next())]
            : result.Videos;

        _queue = videos;
        _queueIndex = -1;
        await PlayQueueItemAsync(result.Shuffle ? 0 : result.StartIndex);
    }

    private void OnPickerPlayRequested(PlaybackRequest req)
    {
        _queue = req.Videos;
        _queueIndex = -1;
        _ = PlayQueueItemAsync(req.StartIndex);
    }

    private void OnPrevClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_queueIndex > 0)
            _ = PlayQueueItemAsync(_queueIndex - 1);
    }

    private void OnNextClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_queueIndex < _queue.Count - 1)
            _ = PlayQueueItemAsync(_queueIndex + 1);
    }

    private void InitializeBridge()
    {
        _bridge = new VlcVideoBridge([.. VlcSetup.GetArgs(), .. App.ExtraVlcArgs]);
        _bridge.BitmapSourceChanged = () => VideoImage.Source = _bridge.Bitmap;
        _bridge.FrameReady = VideoImage.InvalidateVisual;
        _bridge.Player.Volume = (int)VolumeSlider.Value;

        _bridge.Player.Playing += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            PlayIcon.IsVisible = false;
            PauseIcon.IsVisible = true;
            LoadingLabel.IsVisible = false;
        });
        _bridge.Player.Paused += (_, _) =>
            Dispatcher.UIThread.Post(() => { PlayIcon.IsVisible = true; PauseIcon.IsVisible = false; });
        _bridge.Player.Stopped += (_, _) =>
            Dispatcher.UIThread.Post(() => { PlayIcon.IsVisible = true; PauseIcon.IsVisible = false; });

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
                if (_cts.IsCancellationRequested) return;
                Dispatcher.UIThread.Post(() =>
                {
                    if (_queue.Count > 0 && _queueIndex < _queue.Count - 1)
                        _ = PlayQueueItemAsync(_queueIndex + 1);
                    else
                        ShowHint();
                });
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
            // locked: padlock always visible; brightens on hover so user can find and click it
            LockClosedIcon.IsVisible = true;
            LockOpenIcon.IsVisible = false;
            PadlockButton.Transitions = FadePadlockHover;
            PadlockButton.Opacity = _isHovering ? 0.75 : 0.0;
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
    // so the user can still click it to unlock; when unlocked, click-through only when not hovering;
    // at >=95% opacity the window is solid enough that clicks should never pass through
    private void ApplyClickThrough()
    {
        if (_targetOpacity >= 0.95) { _clickThrough.Disable(); return; }
        var passThrough = _isLocked ? !IsCursorOverPadlock() : !_isHovering;
        if (passThrough) _clickThrough.Enable(); else _clickThrough.Disable();
    }

    private bool IsCursorOverPadlock() =>
        _clickThrough.IsCursorOverRect(new Avalonia.Rect(4, 4, 22, 22));

    private void ShowHint()
    {
        TrackTitle.IsVisible = false;
        LoadingLabel.Text = "Right-click to select a playlist";
        LoadingLabel.IsVisible = true;
    }

    private void UpdateTimeLabel() =>
        TimeLabel.Text = $"{Fmt(_currentMs)} / {Fmt(_totalMs)}";

    private static string Fmt(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    private static double SliderValueAt(Slider slider, double x)
    {
        const double thumbHalf = 5.5; // half of the 11px thumb width in ThinSlider
        var offset = thumbHalf;
        var usable = slider.Bounds.Width - 2 * offset;
        if (usable <= 0) return slider.Value;
        return Math.Clamp(slider.Minimum + (x - offset) / usable * (slider.Maximum - slider.Minimum), slider.Minimum, slider.Maximum);
    }

    private static bool IsThumbPress(PointerPressedEventArgs e) =>
        e.Source is Visual v && (v is Thumb || v.FindAncestorOfType<Thumb>() != null);

    private void OnDragPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnVideoDragPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _videoPressWindowPos = Position;
        _videoPressTime = DateTime.UtcNow;
        BeginMoveDrag(e);
    }

    private void OnVideoDragReleased(object? sender, PointerReleasedEventArgs e)
    {
        var dx = Position.X - _videoPressWindowPos.X;
        var dy = Position.Y - _videoPressWindowPos.Y;
        if (Math.Abs(dx) < 4 && Math.Abs(dy) < 4
            && (DateTime.UtcNow - _videoPressTime).TotalMilliseconds < 300)
            OnPlayPauseClicked(null, null!);
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
        _resizeStartCursor = _clickThrough.GetCursorPosition();
        _resizeStartPos = Position;
        _resizeStartSize = ClientSize;
        _clickThrough.BeginResize(_resizeStartPos, _resizeStartSize);
        _resizeTimer.Start();
    }

    private void OnResizeTick(object? sender, EventArgs e)
    {
        if (!_clickThrough.IsLeftButtonHeld())
        {
            _resizeTimer.Stop();
            return;
        }

        var cursor = _clickThrough.GetCursorPosition();
        var dx = cursor.X - _resizeStartCursor.X;
        var dy = cursor.Y - _resizeStartCursor.Y;
        var scale = RenderScaling;

        var newX = _resizeStartPos.X;
        var newY = _resizeStartPos.Y;
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

        const double minW = 240;
        const double minH = 135;
        if (newW < minW)
        {
            if (westEdge) newX = _resizeStartPos.X + (int)((_resizeStartSize.Width - minW) * scale);
            newW = minW;
        }
        if (newH < minH)
        {
            if (northEdge) newY = _resizeStartPos.Y + (int)((_resizeStartSize.Height - minH) * scale);
            newH = minH;
        }

        _clickThrough.MoveResize(newX, newY, newW, newH);
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
        VolumeIcon.IsVisible = !_isMuted;
        MuteIcon.IsVisible = _isMuted;
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

    private void OnSignOutMenuClicked(object? sender, RoutedEventArgs e)
    {
        _services.GetRequiredService<CookieStore>().Clear();
        AppleWebAuth.ClearWebKitSession();
        _log.LogInformation("signed out from youtube");
    }

    private void OnSaveTestCredentialsClicked(object? sender, RoutedEventArgs e)
    {
        var src = _services.GetRequiredService<CookieStore>();
        if (!src.HasCookies)
        {
            _log.LogWarning("no cookies to save");
            return;
        }
        var dst = new CookieStore(_services.GetRequiredService<ILogger<CookieStore>>()) { DataPath = CookieStore.TestCookiePath };
        dst.Save(src.GetCookies());
        if (_log.IsEnabled(LogLevel.Information))
            _log.LogInformation("saved test cookies to {path}", CookieStore.TestCookiePath);
    }

    private bool _closingStarted;

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingStarted) return;
        _closingStarted = true;
        e.Cancel = true;

        _picker?.Close();
        _picker = null;

        _cts.Cancel();
        _playCts.Cancel();
        _pollTimer.Stop();
        _resizeTimer.Stop();

        if (_bridge is { } b)
            await b.StopAsync();

        _bridge?.Dispose();
        Close();
    }
}
