#pragma warning disable CA1873 // logging calls with cheap args don't need IsEnabled guards
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
    private readonly InnerTubeService _innerTube;
    private readonly ClickThrough _clickThrough;
    private readonly PlaybackStateStore _stateStore;
    private readonly CancellationTokenSource _cts = new();

    private List<VideoInfo> _queue = [];
    private int _queueIndex = -1;
    private string? _currentPlaylistId;
    private bool _queueRefreshed;
    private VideoInfo? _removedVideo;
    private int _removedIndex = -1;
    private CancellationTokenSource _playCts = new();
    private string? _currentVideoId;
    private string? _currentStreamUrl;
    private volatile int _errorRetryForIndex = -1; // -1 = not retrying; set on VLC thread, cleared on UI thread
    private int _errorRetryCount;
    private long _retrySeekMs;
    private long _retryGoodMs; // accumulated playback ms since last retry (seek jumps excluded)

    private VlcVideoBridge? _bridge;
    private PlaylistPickerWindow? _picker;
    private bool _isMuted;
    private bool _isLocked;
    private bool _isHovering;
    private bool _minified;
    private double _savedHeight;
    private DateTime _lastTopBarPress = DateTime.MinValue;
    private long _pendingRestoreMs;
    private bool _pendingRestorePause;
    private bool _pendingRestoreReveal;
    private bool _isSeeking;
    private bool _seekTrackDragging;
    private double _targetOpacity = 1.0;
    private long _totalMs;
    private long _currentMs;

    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _resizeTimer = new() { Interval = TimeSpan.FromMilliseconds(8) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private PixelPoint _resizeStartCursor;
    private PixelPoint _resizeStartPos;
    private Size _resizeStartSize;
    private WindowEdge _resizeEdge;

    private PixelPoint _videoPressWindowPos;
    private DateTime _videoPressTime;
    private PixelPoint _videoDragCursorStart;
    private bool _videoDragActive;

    private static readonly Transitions FadeOutControls =
    [
        new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(200) },
    ];
    private static readonly Transitions FadeLabel =
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
        _innerTube = services.GetRequiredService<InnerTubeService>();
        _clickThrough = new ClickThrough(this, services.GetRequiredService<ILogger<ClickThrough>>());
        _stateStore = services.GetRequiredService<PlaybackStateStore>();

        _pollTimer.Tick += OnPollTick;
        _resizeTimer.Tick += OnResizeTick;
        _saveTimer.Tick += (_, _) =>
        {
            if (_queue.Count > 0 && _queueIndex >= 0)
            {
                var pos = _pendingRestoreMs > 0 ? _pendingRestoreMs : _currentMs;
                _stateStore.Save(new PlaybackState(_currentPlaylistId, _queue, _queueIndex, pos));
            }
        };

        SeekBar.AddHandler(PointerPressedEvent, (_, e) =>
        {
            _isSeeking = true;
            SeekBar.Value = SliderValueAt(SeekBar, e.GetCurrentPoint(SeekBar).Position.X);
            if (!IsThumbPress(e))
            {
                _seekTrackDragging = true;
                e.Pointer.Capture(SeekBar);
                e.Handled = true; // prevent Avalonia's LargeChange step from overriding exact click position
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
            if (_bridge is { } b)
            {
                b.Player.Position = (float)SeekBar.Value;
                if (_totalMs > 0) _currentMs = (long)(SeekBar.Value * _totalMs);
            }
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
            e.Handled = true;
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

        bool brightTrackDragging = false;
        BrightnessSlider.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (IsThumbPress(e)) return;
            brightTrackDragging = true;
            BrightnessSlider.Value = SliderValueAt(BrightnessSlider, e.GetCurrentPoint(BrightnessSlider).Position.X);
            e.Pointer.Capture(BrightnessSlider);
        }, RoutingStrategies.Tunnel);
        BrightnessSlider.AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (brightTrackDragging)
                BrightnessSlider.Value = SliderValueAt(BrightnessSlider, e.GetCurrentPoint(BrightnessSlider).Position.X);
        }, RoutingStrategies.Tunnel | RoutingStrategies.Direct);
        BrightnessSlider.AddHandler(PointerReleasedEvent, (_, _) => brightTrackDragging = false,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        BrightnessSlider.ValueChanged += (_, e) =>
            BrightnessOverlay.Opacity = 1.0 - e.NewValue / 100.0;

        KeyDown += OnKeyDown;
        Opened += OnOpened;
        Closing += OnClosing;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        PositionBottomRight();
        _clickThrough.Initialize();
        _pollTimer.Start();
        _saveTimer.Start();
        InitializeBridge();
        SaveTestCredentialsMenuItem.IsVisible = System.Diagnostics.Debugger.IsAttached;

        if (App.OverrideUrl != null)
        {
            LoadingLabel.Text = "Loading...";
            SetPlayEnabled(true);
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
        else
        {
            await TryRestoreStateAsync();
        }
    }

    private async Task TryRestoreStateAsync()
    {
        var state = _stateStore.TryLoad();
        if (state == null || state.Queue.Count == 0) return;

        _queue = state.Queue;
        _currentPlaylistId = state.PlaylistId;
        _removedVideo = null;
        _removedIndex = -1;

        // current video if still valid, else nearest next, else first
        var index = Math.Clamp(state.QueueIndex, 0, _queue.Count - 1);
        _queueIndex = -1;
        _pendingRestoreMs = state.PositionMs;
        _pendingRestorePause = true;
        if (state.PositionMs > 0) VideoImage.IsVisible = false;
        await PlayQueueItemAsync(index);
    }

    private async Task PlayQueueItemAsync(int index)
    {
        if (index < 0 || index >= _queue.Count) return;

        // navigating away from the restored video — don't seek or pause on play
        if (_queueIndex >= 0 && index != _queueIndex) { _pendingRestoreMs = 0; _pendingRestorePause = false; _pendingRestoreReveal = false; VideoImage.IsVisible = true; _errorRetryCount = 0; _errorRetryForIndex = -1; _retrySeekMs = 0; _retryGoodMs = 0; }

        _playCts.Cancel();
        _playCts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, _playCts.Token);

        _queueIndex = index;
        _queueRefreshed = false;
        _removedVideo = null;
        _removedIndex = -1;
        var video = _queue[index];

        TrackTitle.Text = video.Title;
        TrackTitle.IsVisible = true;
        LoadingLabel.Text = "Loading...";
        LoadingLabel.IsVisible = true;
        SetPlayEnabled(true);
        UpdateQueueMenuItems();

        try
        {
            var physW = ClientSize.Width * RenderScaling;
            var physH = ClientSize.Height * RenderScaling;
            _currentVideoId = video.Id;
            var url = await _youtube.GetStreamUrl(video.Id, linked.Token, physW, physH);
            _currentStreamUrl = url;
            _bridge!.Play(url);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "skipping unavailable video {id}", video.Id);
            LoadingLabel.IsVisible = false;
            if (_queueIndex < _queue.Count - 1)
                _ = PlayQueueItemAsync(_queueIndex + 1);
        }
    }

    private void UpdateQueueMenuItems()
    {
        bool hasPrev, hasNext;
        if (_removedVideo != null)
        {
            hasPrev = _removedIndex > 0;
            hasNext = _removedIndex < _queue.Count;
        }
        else
        {
            hasPrev = _queue.Count > 0 && _queueIndex > 0;
            hasNext = _queue.Count > 0 && _queueIndex < _queue.Count - 1;
        }
        PrevMenuItem.IsEnabled = hasPrev;
        NextMenuItem.IsEnabled = hasNext;
        PrevButton.IsEnabled = hasPrev;
        NextButton.IsEnabled = hasNext;
        MinPrevButton.IsEnabled = hasPrev;
        MinNextButton.IsEnabled = hasNext;
        UpdateTrashButton();
    }

    private void UpdateTrashButton()
    {
        var inPlaylist = _currentPlaylistId != null;
        TrashButton.IsVisible = inPlaylist && !_minified;
        MinTrashButton.IsVisible = inPlaylist && _minified;
        if (!inPlaylist) return;
        var canUndo = _removedVideo != null;
        TrashEmptyIcon.IsVisible = !canUndo;
        TrashFullIcon.IsVisible = canUndo;
        MinTrashEmptyIcon.IsVisible = !canUndo;
        MinTrashFullIcon.IsVisible = canUndo;
        var canAct = canUndo || (_queueIndex >= 0 && _queueIndex < _queue.Count);
        TrashButton.IsEnabled = canAct;
        MinTrashButton.IsEnabled = canAct;
    }

    private async Task RefreshQueueAsync(string playlistId, CancellationToken cancel)
    {
        _log.LogInformation("refreshing playlist {id} before end of track", playlistId);
        List<VideoInfo> fresh;
        try
        {
            fresh = await _innerTube.GetPlaylistVideosAsync(playlistId, cancel);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "playlist refresh failed for {id}", playlistId);
            return;
        }
        if (fresh.Count == 0 || _queueIndex < 0 || _queueIndex >= _queue.Count) return;

        var currentId = _queue[_queueIndex].Id;
        var newIndex = fresh.FindIndex(v => v.Id == currentId);
        if (newIndex >= 0)
        {
            _queue = fresh;
            _queueIndex = newIndex;
        }
        else
        {
            // current video was deleted externally; land on what was originally next
            var nextId = _queueIndex + 1 < _queue.Count ? _queue[_queueIndex + 1].Id : null;
            var nextIndex = nextId != null ? fresh.FindIndex(v => v.Id == nextId) : -1;
            _queue = fresh;
            _queueIndex = nextIndex >= 0 ? nextIndex - 1 : -1;
        }
        UpdateQueueMenuItems();
        _log.LogInformation("playlist refreshed: {count} videos, queue index now {index}", fresh.Count, _queueIndex);
    }

    private void OnSelectPlaylistMenuClicked(object? sender, RoutedEventArgs e)
    {
        if (_picker != null) { _picker.Activate(); return; }
        _picker = new PlaylistPickerWindow(_services);
        _picker.PlayRequested += OnPickerPlayRequested;
        _picker.Closed += (_, _) => _picker = null;
        _picker.Show(this);
    }

    private void OnPickerPlayRequested(PlaybackRequest req)
    {
        _pendingRestoreMs = 0;
        _pendingRestorePause = false;
        _pendingRestoreReveal = false;
        VideoImage.IsVisible = true;
        var videos = req.Shuffle
            ? [.. req.Videos.OrderBy(_ => Random.Shared.Next())]
            : req.Videos;
        _queue = videos;
        _queueIndex = -1;
        _currentPlaylistId = req.PlaylistId;
        _removedVideo = null;
        _removedIndex = -1;
        _ = PlayQueueItemAsync(req.Shuffle ? 0 : req.StartIndex);
    }

    private void OnPrevClicked(object? sender, RoutedEventArgs e)
    {
        if (_removedVideo != null)
        {
            if (_removedIndex > 0) _ = PlayQueueItemAsync(_removedIndex - 1);
        }
        else if (_queueIndex > 0)
            _ = PlayQueueItemAsync(_queueIndex - 1);
    }

    private void OnNextClicked(object? sender, RoutedEventArgs e)
    {
        if (_removedVideo != null)
        {
            if (_removedIndex < _queue.Count) _ = PlayQueueItemAsync(_removedIndex);
        }
        else if (_queueIndex < _queue.Count - 1)
            _ = PlayQueueItemAsync(_queueIndex + 1);
    }

    private void OnTrashClicked(object? sender, RoutedEventArgs e)
    {
        if (_removedVideo != null)
        {
            _queue.Insert(_removedIndex, _removedVideo);
            _removedVideo = null;
            _removedIndex = -1;
        }
        else
        {
            if (_queueIndex < 0 || _queueIndex >= _queue.Count) return;
            _removedVideo = _queue[_queueIndex];
            _removedIndex = _queueIndex;
            _queue.RemoveAt(_queueIndex);
        }
        UpdateTrashButton();
        UpdateQueueMenuItems();
    }

    private void InitializeBridge()
    {
        _bridge = new VlcVideoBridge([.. VlcSetup.GetArgs(), .. App.ExtraVlcArgs]);
        _bridge.BitmapSourceChanged = () => VideoImage.Source = _bridge.Bitmap;
        _bridge.FrameReady = () =>
        {
            if (_pendingRestoreReveal)
            {
                _pendingRestoreReveal = false;
                VideoImage.IsVisible = true;
            }
            VideoImage.InvalidateVisual();
            if (_pendingRestorePause)
            {
                _pendingRestorePause = false;
                _bridge.Player.Pause();
            }
        };
        _bridge.Player.Volume = (int)VolumeSlider.Value;

        _bridge.Player.Playing += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            PlayIcon.IsVisible = false;
            PauseIcon.IsVisible = true;
            MinPlayIcon.IsVisible = false;
            MinPauseIcon.IsVisible = true;
            LoadingLabel.IsVisible = false;
        });
        _bridge.Player.Paused += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            PlayIcon.IsVisible = true; PauseIcon.IsVisible = false;
            MinPlayIcon.IsVisible = true; MinPauseIcon.IsVisible = false;
            Dispatcher.UIThread.Post(ApplyRestoreSeek, DispatcherPriority.Background);
        });
        _bridge.Player.Stopped += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            PlayIcon.IsVisible = true; PauseIcon.IsVisible = false;
            MinPlayIcon.IsVisible = true; MinPauseIcon.IsVisible = false;
        });

        _bridge.Player.LengthChanged += (_, ev) =>
            Dispatcher.UIThread.Post(() =>
            {
                _totalMs = ev.Length;
                UpdateTimeLabel();
                if (_currentMs > 0 && ev.Length > 0)
                    SeekBar.Value = _currentMs / (double)ev.Length;
                if (_retrySeekMs > 0)
                {
                    var ms = _retrySeekMs;
                    var total = _totalMs;
                    _retrySeekMs = 0;
                    Cathedral.Utils.Background.RunTask(async () =>
                    {
                        await Task.Delay(300);
                        if (_bridge != null) _bridge.Player.Position = (float)(ms / (double)total);
                    }, _log, _cts.Token);
                }
                else
                    Dispatcher.UIThread.Post(ApplyRestoreSeek, DispatcherPriority.Background);
            });
        _bridge.Player.TimeChanged += (_, ev) =>
            Dispatcher.UIThread.Post(() =>
            {
                var delta = ev.Time - _currentMs;
                _currentMs = ev.Time;
                if (!_isSeeking) UpdateTimeLabel();
                // accumulate real playback progress (filter out seek jumps); reset retry after 5s
                if (_errorRetryCount > 0 && delta > 0 && delta < 2000)
                {
                    _retryGoodMs += delta;
                    if (_retryGoodMs >= 5000) { _errorRetryCount = 0; _errorRetryForIndex = -1; _retryGoodMs = 0; }
                }
            });
        _bridge.Player.PositionChanged += (_, ev) =>
            Dispatcher.UIThread.Post(() => { if (!_isSeeking) SeekBar.Value = ev.Position; });

        _bridge.Player.EncounteredError += (_, _) =>
        {
            // set flag before EndReached fires so it can see it
            _errorRetryForIndex = _queueIndex;
            Task.Run(async () =>
            {
                if (_cts.IsCancellationRequested) { _errorRetryForIndex = -1; return; }
                await Task.Delay(500);
                Dispatcher.UIThread.Post(() =>
                {
                    if (_cts.IsCancellationRequested) return;
                    var retryIdx = _errorRetryForIndex;
                    if (retryIdx < 0 || _queueIndex != retryIdx) return; // navigated away during delay
                    _errorRetryCount++;
                    if (_errorRetryCount <= 10)
                    {
                        _log.LogWarning("decode error (retry {n}/10) for {id}", _errorRetryCount, _queue.ElementAtOrDefault(retryIdx)?.Id);
                        _retrySeekMs = _currentMs;
                        _retryGoodMs = 0;
                        _pendingRestoreMs = 0;
                        _ = PlayQueueItemAsync(retryIdx);
                    }
                    else
                    {
                        _log.LogError("decode error after 10 retries, skipping {id}", _queue.ElementAtOrDefault(retryIdx)?.Id);
                        _errorRetryForIndex = -1;
                        _errorRetryCount = 0;
                        if (_queue.Count > 0 && _queueIndex < _queue.Count - 1)
                            _ = PlayQueueItemAsync(_queueIndex + 1);
                        else
                            ShowHint();
                    }
                });
            });
        };

        _bridge.Player.EndReached += (_, _) =>
            Task.Run(() =>
            {
                if (App.ExitOnDone)
                {
                    Dispatcher.UIThread.Post(() =>
                        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
                            ?.Shutdown());
                    return;
                }
                if (_cts.IsCancellationRequested) return;
                Dispatcher.UIThread.Post(() =>
                {
                    if (_errorRetryForIndex >= 0) return; // EncounteredError is handling this
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

        // refresh the playlist ~30s before the current track ends so queue reflects external changes
        if (_currentPlaylistId != null && !_queueRefreshed && _totalMs > 30_000 && _currentMs > 0 && (_totalMs - _currentMs) <= 30_000)
        {
            _queueRefreshed = true;
            _ = RefreshQueueAsync(_currentPlaylistId, _playCts.Token);
        }
    }

    private void UpdateState()
    {
        var showControls = _minified || (_isHovering && !_isLocked);

        if (showControls)
        {
            ControlsGrid.Transitions = null;
            ControlsGrid.Opacity = 1.0;
            ControlsGrid.IsHitTestVisible = true;
        }
        else
        {
            ControlsGrid.Transitions = FadeOutControls;
            ControlsGrid.Opacity = 0.0;
            ControlsGrid.IsHitTestVisible = false;
        }

        // minified: hover restores opacity; otherwise tied to controls visibility
        Opacity = (_minified ? _isHovering : showControls) ? 1.0 : _targetOpacity;

        // border visible in non-minified (ControlsGrid handles it) or when hovering in minified
        ResizeBorder.IsVisible = !_minified || _isHovering;

        LoadingLabel.Transitions = FadeLabel;
        LoadingLabel.Opacity = showControls ? 0.33 : 1.0;

        if (_isLocked)
        {
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
            ResizeHandles.IsVisible = !_minified;
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
        _clickThrough.IsCursorOverRect(new Rect(4, 4, 22, 22));

    private void ShowHint()
    {
        TrackTitle.IsVisible = false;
        LoadingLabel.Text = "Right-click to select media";
        LoadingLabel.IsVisible = true;
        SetPlayEnabled(false);
    }

    private void SetPlayEnabled(bool enabled)
    {
        PlayPauseButton.IsEnabled = enabled;
        MinPlayPauseButton.IsEnabled = enabled;
    }

    private async void ApplyRestoreSeek()
    {
        if (_pendingRestoreMs <= 0 || _totalMs <= 0 || _bridge == null || _bridge.Player.IsPlaying) return;
        var ms = _pendingRestoreMs;
        _pendingRestoreMs = 0;
        var pos = (float)(ms / (double)_totalMs);
        _currentMs = ms;
        SeekBar.Value = pos;
        UpdateTimeLabel();
        await Task.Delay(500);
        if (_bridge == null) { VideoImage.IsVisible = true; return; }
        if (_bridge.Player.IsPlaying) { VideoImage.IsVisible = true; return; } // user pressed play during wait
        _pendingRestoreReveal = true;
        _bridge.Player.Position = pos;
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
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var now = DateTime.UtcNow;
        if ((now - _lastTopBarPress).TotalMilliseconds < 300)
        {
            _lastTopBarPress = DateTime.MinValue;
            SetMinified(!_minified);
            return;
        }
        _lastTopBarPress = now;
        BeginMoveDrag(e);
    }

    private void SetMinified(bool minified)
    {
        _minified = minified;

        VideoImage.IsVisible = !minified;
        BrightnessOverlay.IsVisible = !minified;
        BrightnessIcon.IsVisible = !minified;
        BrightnessSlider.IsVisible = !minified;
        LoadingLabel.IsVisible = !minified && LoadingLabel.IsVisible;

        PrevButton.IsVisible = !minified;
        PlayPauseButton.IsVisible = !minified;
        NextButton.IsVisible = !minified;

        MinTransportLeft.IsVisible = minified;

        if (minified)
        {
            _savedHeight = Height;
            MinHeight = 60;
            Height = 84;
        }
        else
        {
            MinHeight = 135;
            Height = _savedHeight > 0 ? _savedHeight : 270;
            if (_queue.Count == 0 && _bridge?.Player.IsPlaying != true)
                ShowHint();
        }

        UpdateTrashButton();
        UpdateState();
    }

    private void OnVideoDragPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _videoPressWindowPos = Position;
        _videoPressTime = DateTime.UtcNow;
        _videoDragCursorStart = _clickThrough.GetCursorPosition();
        _videoDragActive = false;
        e.Pointer.Capture((IInputElement)sender!);
    }

    private void OnVideoPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _videoDragActive = false; return; }
        var cursor = _clickThrough.GetCursorPosition();
        var dx = cursor.X - _videoDragCursorStart.X;
        var dy = cursor.Y - _videoDragCursorStart.Y;
        if (!_videoDragActive && (Math.Abs(dx) >= 4 || Math.Abs(dy) >= 4))
            _videoDragActive = true;
        if (_videoDragActive)
            Position = new PixelPoint(_videoPressWindowPos.X + dx, _videoPressWindowPos.Y + dy);
    }

    private void OnVideoDragReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        if (!_videoDragActive && (DateTime.UtcNow - _videoPressTime).TotalMilliseconds < 300)
            OnPlayPauseClicked(null, null!);
        _videoDragActive = false;
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
            EvaluateStreamResolution();
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

    private void EvaluateStreamResolution()
    {
        if (_currentVideoId == null || _queue.Count == 0 || _queueIndex < 0) return;
        var physW = ClientSize.Width * RenderScaling;
        var physH = ClientSize.Height * RenderScaling;
        var newUrl = _youtube.SelectStreamForSize(_currentVideoId, physW, physH);
        if (newUrl == null || newUrl == _currentStreamUrl) return;
        _log.LogInformation("window resized, switching stream resolution");
        _currentStreamUrl = newUrl;
        _bridge!.Play(newUrl);
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
        else if (b.Player.Media != null) b.Player.Play();
        else if (_queueIndex >= 0 && _queueIndex < _queue.Count) _ = PlayQueueItemAsync(_queueIndex);
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

        if (_queue.Count > 0 && _queueIndex >= 0)
        {
            var savePos = _pendingRestoreMs > 0 ? _pendingRestoreMs : _currentMs;
            _stateStore.Save(new PlaybackState(_currentPlaylistId, _queue, _queueIndex, savePos));
        }

        _picker?.Close();
        _picker = null;

        _cts.Cancel();
        _playCts.Cancel();
        _pollTimer.Stop();
        _resizeTimer.Stop();
        _saveTimer.Stop();

        if (_bridge is { } b)
            await b.StopAsync();

        _bridge?.Dispose();
        Close();
    }
}
