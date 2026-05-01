using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SfwPlayer.Models;
using SfwPlayer.Platform.MacOS;
using SfwPlayer.Services;

namespace SfwPlayer.Views;

public partial class PlaylistPickerWindow : Window
{
    private readonly InnerTubeService _innerTube;
    private readonly CookieStore _cookies;
    private readonly ILogger<PlaylistPickerWindow> _log;

    public PlaylistPickerWindow(IServiceProvider services)
    {
        InitializeComponent();
        _innerTube = services.GetRequiredService<InnerTubeService>();
        _cookies = services.GetRequiredService<CookieStore>();
        _log = services.GetRequiredService<ILogger<PlaylistPickerWindow>>();
        Opened += OnOpened;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (!_cookies.HasCookies)
        {
            ShowStatus("Opening YouTube sign-in...");
            var ok = await SignInAsync();
            if (!ok) { ShowStatus("Sign-in cancelled or failed."); return; }
        }

        await LoadPlaylistsAsync();
    }

    private async Task<bool> SignInAsync()
    {
        if (!OperatingSystem.IsMacOS())
        {
            ShowStatus("Sign-in is only supported on macOS.");
            return false;
        }

        var nsView = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        var cookies = await AppleWebAuth.SignInAsync(nsView);
        if (cookies == null || cookies.Count == 0) return false;
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
        PlayAllButton.IsEnabled = false;
        ShuffleButton.IsEnabled = false;
        ShowStatus("Loading videos...");

        try
        {
            var videos = await _innerTube.GetPlaylistVideosAsync(playlist.Id, CancellationToken.None);
            VideoList.ItemsSource = videos;
            PlayAllButton.IsEnabled = videos.Count > 0;
            ShuffleButton.IsEnabled = videos.Count > 0;
            HideStatus();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "failed to load videos for {id}", playlist.Id);
            ShowStatus($"Error loading videos: {ex.Message}");
        }
    }

    private void OnSignOutClicked(object? sender, RoutedEventArgs e)
    {
        _cookies.Clear();
        PlaylistList.ItemsSource = null;
        VideoList.ItemsSource = null;
        PlayAllButton.IsEnabled = false;
        ShuffleButton.IsEnabled = false;
        SignOutButton.IsVisible = false;
        ShowStatus("Signed out. Reopen to sign in again.");
    }

    private void OnPlayAllClicked(object? sender, RoutedEventArgs e)
    {
        if (VideoList.ItemsSource is not List<VideoInfo> videos) return;
        Close(new PlaybackRequest(videos, false));
    }

    private void OnShuffleClicked(object? sender, RoutedEventArgs e)
    {
        if (VideoList.ItemsSource is not List<VideoInfo> videos) return;
        Close(new PlaybackRequest(videos, true));
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(null);

    private void ShowStatus(string text)
    {
        StatusLabel.Text = text;
        StatusLabel.IsVisible = true;
    }

    private void HideStatus() => StatusLabel.IsVisible = false;
}
