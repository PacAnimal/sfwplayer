using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SfwPlayer;

public record OverlayCallbacks(
    Action OnClose,
    Action OnPlayPause,
    Action OnMute,
    Action<double> OnVolumeChanged,
    Action<double> OnOpacityChanged,
    Action OnOpacityReleased,
    Action<float> OnSeekReleased,
    Action<PointerPressedEventArgs> OnStartDrag,
    Action<WindowEdge> OnResize,
    Action OnLockToggled
);

public partial class ControlsOverlayWindow : Window
{
    private readonly OverlayCallbacks _callbacks;
    private bool _isSeeking;
    private long _totalMs;

    public ControlsOverlayWindow(OverlayCallbacks callbacks)
    {
        InitializeComponent();
        _callbacks = callbacks;

        SeekBar.AddHandler(PointerPressedEvent, (_, _) => _isSeeking = true, RoutingStrategies.Tunnel);
        SeekBar.AddHandler(PointerReleasedEvent, (_, _) =>
        {
            _isSeeking = false;
            _callbacks.OnSeekReleased((float)SeekBar.Value);
        }, RoutingStrategies.Tunnel);

        SeekBar.ValueChanged += OnSeekValueChanged;
        VolumeSlider.ValueChanged += (_, e) => _callbacks.OnVolumeChanged(e.NewValue);
        OpacitySlider.ValueChanged += (_, e) => _callbacks.OnOpacityChanged(e.NewValue);
        OpacitySlider.AddHandler(PointerReleasedEvent, (_, _) => _callbacks.OnOpacityReleased(), RoutingStrategies.Tunnel);
    }

    public void ShowPadlock(bool isLocked)
    {
        LockClosedIcon.IsVisible = isLocked;
        LockOpenIcon.IsVisible = !isLocked;
        PadlockButton.Transitions = null;
        PadlockButton.Opacity = isLocked ? 0.45 : 0.85;
        ResizeHandles.IsVisible = !isLocked;
    }

    public void HidePadlock()
    {
        PadlockButton.Transitions = PadlockFadeOutTransition;
        PadlockButton.Opacity = 0.0;
        ResizeHandles.IsVisible = false;
    }

    private static readonly Transitions FadeOutTransition =
    [
        new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(200) },
    ];

    private static readonly Transitions PadlockFadeOutTransition =
    [
        new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(400) },
    ];

    public void ShowControls()
    {
        ControlsGrid.Transitions = null; // instant
        ControlsGrid.Opacity = 1.0;
    }

    public void HideControls()
    {
        ControlsGrid.Transitions = FadeOutTransition; // 200ms fade
        ControlsGrid.Opacity = 0.0;
    }

    public void UpdateTime(long currentMs, long totalMs)
    {
        _totalMs = totalMs;
        TimeLabel.Text = $"{Fmt(currentMs)} / {Fmt(totalMs)}";
    }

    public void UpdatePlayState(bool playing) =>
        PlayPauseButton.Content = playing ? "⏸" : "▶";

    public void UpdateMuteState(bool muted) =>
        MuteButton.Content = muted ? "🔇" : "🔊";

    public void UpdateSeekPosition(float position)
    {
        if (!_isSeeking) SeekBar.Value = position;
    }

    public void SetLoadingVisible(bool visible) =>
        LoadingLabel.IsVisible = visible;

    public int GetInitialVolume() => (int)VolumeSlider.Value;

    private void OnDragPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            _callbacks.OnStartDrag(e);
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
        if (edge.HasValue) _callbacks.OnResize(edge.Value);
    }

    private void OnPadlockClicked(object? sender, RoutedEventArgs e) => _callbacks.OnLockToggled();
    private void OnCloseClicked(object? sender, RoutedEventArgs e) => _callbacks.OnClose();
    private void OnPlayPauseClicked(object? sender, RoutedEventArgs e) => _callbacks.OnPlayPause();
    private void OnMuteClicked(object? sender, RoutedEventArgs e) => _callbacks.OnMute();

    private void OnSeekValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isSeeking && _totalMs > 0)
            TimeLabel.Text = $"{Fmt((long)(e.NewValue * _totalMs))} / {Fmt(_totalMs)}";
    }

    private static string Fmt(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}
