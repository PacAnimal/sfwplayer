using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SfwPlayer.Models;
using SfwPlayer.Platform.MacOS;
using SfwPlayer.Services;
#if IS_WINDOWS
using System.Net;
using SfwPlayer.Platform.Windows;
#endif

namespace SfwPlayer.Views;

public partial class PlaylistPickerWindow : Window
{
    private readonly InnerTubeService _innerTube;
    private readonly CookieStore _cookies;
    private readonly ILogger<PlaylistPickerWindow> _log;
    private readonly CancellationTokenSource _cts = new();
    private ObservableCollection<VideoListItem>? _videoItems;
    private string? _currentPlaylistId;

    public event Action<PlaybackRequest>? PlayRequested;

    public PlaylistPickerWindow(IServiceProvider services)
    {
        InitializeComponent();
        _innerTube = services.GetRequiredService<InnerTubeService>();
        _cookies = services.GetRequiredService<CookieStore>();
        _log = services.GetRequiredService<ILogger<PlaylistPickerWindow>>();
        Closed += (_, _) => _cts.Cancel();
        Opened += OnOpened;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (!_cookies.HasCookies)
        {
            ShowStatus("Sign in to YouTube to continue...");
            var ok = await SignInAsync();
            if (!ok) return;
        }

        await LoadPlaylistsAsync();
    }

    private async Task<bool> SignInAsync()
    {
#if IS_WINDOWS
        if (OperatingSystem.IsWindows())
        {
            var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            List<Cookie>? wCookies;
            try { wCookies = await WindowsWebAuth.SignInInWindowAsync(hwnd, _cts.Token); }
            catch { ShowStatus("Sign-in failed. Ensure Microsoft Edge WebView2 Runtime is installed."); return false; }
            if (wCookies == null || wCookies.Count == 0) { ShowStatus("Sign-in cancelled or failed."); return false; }
            _cookies.Save(wCookies);
            return _cookies.HasCookies;
        }
#endif
        if (!OperatingSystem.IsMacOS())
        {
            ShowStatus("Sign-in is not supported on this platform.");
            return false;
        }

        var prevTitle = Title;
        Title = "SfwPlayer — Sign in to YouTube";
        var nsView = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        var cookies = await AppleWebAuth.SignInInWindowAsync(nsView, _cts.Token);
        Title = prevTitle;
        if (cookies == null || cookies.Count == 0)
        {
            ShowStatus("Sign-in cancelled or failed.");
            return false;
        }
        _cookies.Save(cookies);
        return _cookies.HasCookies;
    }

    private async Task LoadPlaylistsAsync()
    {
        ShowStatus("Loading playlists...");
        try
        {
            var items = await _innerTube.GetPlaylistsAsync(CancellationToken.None);
            if (items.Count == 0)
            {
                ShowStatus("No playlists found. Try signing out and signing in again.");
                SignOutButton.IsVisible = true;
                return;
            }
            PlaylistList.ItemsSource = items;
            SignOutButton.IsVisible = true;
            HideStatus();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "failed to load playlists");
            ShowStatus($"Error loading playlists: {ex.Message}");
        }
    }

    private async void OnPlaylistSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (PlaylistList.SelectedItem is not PlaylistInfo playlist) return;

        VideoList.ItemsSource = null;
        _videoItems = null;
        _currentPlaylistId = null;
        PlayAllButton.IsEnabled = false;
        ShuffleButton.IsEnabled = false;
        ShowStatus("Loading videos...");

        try
        {
            var videos = await _innerTube.GetPlaylistVideosAsync(playlist.Id, CancellationToken.None);
            var items = videos.Select(v => new VideoListItem(v)).ToList();
            _currentPlaylistId = playlist.Id;
            _videoItems = new ObservableCollection<VideoListItem>(items);
            VideoList.ItemsSource = _videoItems;
            PlayAllButton.IsEnabled = _videoItems.Count > 0;
            ShuffleButton.IsEnabled = _videoItems.Count > 0;
            HideStatus();
            _ = LoadThumbnailsAsync(items, _cts.Token);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "failed to load videos for {id}", playlist.Id);
            ShowStatus($"Error loading videos: {ex.Message}");
        }
    }

    private static async Task LoadThumbnailsAsync(List<VideoListItem> items, CancellationToken cancel)
    {
        using var http = new HttpClient();
        try
        {
            await Parallel.ForEachAsync(items,
                new ParallelOptions { MaxDegreeOfParallelism = 5, CancellationToken = cancel },
                async (item, ct) => await item.LoadThumbnailAsync(http, ct));
        }
        catch (OperationCanceledException) { }
    }

    private void OnVideoDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_videoItems == null || VideoList.SelectedItem is not VideoListItem item) return;
        var startIndex = _videoItems.IndexOf(item);
        PlayRequested?.Invoke(new PlaybackRequest([.. _videoItems.Select(i => i.Info)], false, startIndex, _currentPlaylistId));
    }

    private async void OnRemoveVideoClicked(object? sender, RoutedEventArgs e)
    {
        if (_videoItems == null || sender is not Button btn || btn.DataContext is not VideoListItem item) return;
        _videoItems.Remove(item);
        PlayAllButton.IsEnabled = _videoItems.Count > 0;
        ShuffleButton.IsEnabled = _videoItems.Count > 0;
        if (_currentPlaylistId == null || item.Info.SetVideoId == null)
        {
            _log.LogWarning("no setVideoId for {id}, skipping YouTube removal", item.Info.Id);
            return;
        }
        try
        {
            await _innerTube.RemovePlaylistItemAsync(_currentPlaylistId, item.Info.SetVideoId, item.Info.Id, _cts.Token);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "failed to remove video {id} from playlist", item.Info.Id);
        }
    }

    private void OnSignOutClicked(object? sender, RoutedEventArgs e)
    {
        _cookies.Clear();
        if (OperatingSystem.IsMacOS()) AppleWebAuth.ClearWebKitSession();
#if IS_WINDOWS
        else if (OperatingSystem.IsWindows()) WindowsWebAuth.ClearSession();
#endif
        PlaylistList.ItemsSource = null;
        VideoList.ItemsSource = null;
        _videoItems = null;
        _currentPlaylistId = null;
        PlayAllButton.IsEnabled = false;
        ShuffleButton.IsEnabled = false;
        SignOutButton.IsVisible = false;
        ShowStatus("Signed out. Reopen to sign in again.");
    }

    private void OnPlayAllClicked(object? sender, RoutedEventArgs e)
    {
        if (_videoItems == null) return;
        PlayRequested?.Invoke(new PlaybackRequest([.. _videoItems.Select(i => i.Info)], false, PlaylistId: _currentPlaylistId));
        Close();
    }

    private void OnShuffleClicked(object? sender, RoutedEventArgs e)
    {
        if (_videoItems == null) return;
        PlayRequested?.Invoke(new PlaybackRequest([.. _videoItems.Select(i => i.Info)], true, PlaylistId: _currentPlaylistId));
        Close();
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) { _cts.Cancel(); Close(null); }

    private void ShowStatus(string text)
    {
        StatusLabel.Text = text;
        StatusLabel.IsVisible = true;
    }

    private void HideStatus() => StatusLabel.IsVisible = false;
}

internal sealed class VideoListItem(VideoInfo info) : INotifyPropertyChanged
{
    public VideoInfo Info { get; } = info;
    private Bitmap? _thumbnail;
    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        private set { _thumbnail = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task LoadThumbnailAsync(HttpClient http, CancellationToken cancel)
    {
        if (Info.ThumbnailUrl == null) return;
        try
        {
            var bytes = await http.GetByteArrayAsync(Info.ThumbnailUrl, cancel);
            using var ms = new MemoryStream(bytes);
            var bmp = new Bitmap(ms);
            await Dispatcher.UIThread.InvokeAsync(() => Thumbnail = bmp);
        }
        catch (Exception)
        {
            // ignored
        }
    }
}
