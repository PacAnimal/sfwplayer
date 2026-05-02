#pragma warning disable CA1873 // logging calls with cheap args don't need IsEnabled guards
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SfwPlayer.Models;

namespace SfwPlayer.Services;

public partial class InnerTubeService(CookieStore cookies, ILogger<InnerTubeService> log)
{
    public const string ChromeUA =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    public async Task<List<PlaylistInfo>> GetPlaylistsAsync(CancellationToken cancel)
    {
        log.LogInformation("fetching playlists page");
        using var http = BuildClient();
        var html = await http.GetStringAsync("https://www.youtube.com/feed/playlists", cancel);
        var json = ExtractYtInitialData(html);
        if (json == null)
        {
            log.LogWarning("ytInitialData not found in playlists page");
            return [];
        }
        var results = ParsePlaylists(json);
        log.LogInformation("found {count} playlists", results.Count);
        return results;
    }

    public async Task<List<VideoInfo>> GetPlaylistVideosAsync(string playlistId, CancellationToken cancel)
    {
        log.LogInformation("fetching videos for playlist {id}", playlistId);
        using var http = BuildClient();
        var html = await http.GetStringAsync(
            $"https://www.youtube.com/playlist?list={Uri.EscapeDataString(playlistId)}", cancel);
        var json = ExtractYtInitialData(html);
        if (json == null)
        {
            log.LogWarning("ytInitialData not found in playlist page");
            return [];
        }
        var results = ParseVideos(json);
        log.LogInformation("found {count} videos", results.Count);
        return results;
    }

    public HttpClient BuildClient()
    {
        var handler = new HttpClientHandler { UseCookies = false };
        var http = new HttpClient(handler);
        http.DefaultRequestHeaders.UserAgent.ParseAdd(ChromeUA);
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");

        var youtubeCookies = cookies.GetCookies()
            .Where(c => c.Domain.Contains("youtube.com", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (youtubeCookies.Count > 0)
            http.DefaultRequestHeaders.Add("Cookie",
                string.Join("; ", youtubeCookies.Select(c => $"{c.Name}={c.Value}")));

        return http;
    }

    // --- HTML extraction ---

    internal static string? ExtractYtInitialData(string html)
    {
        const string Marker = "var ytInitialData = ";
        var idx = html.IndexOf(Marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        idx += Marker.Length;
        var end = html.IndexOf(";</script>", idx, StringComparison.Ordinal);
        return end < 0 ? null : html[idx..end];
    }

    // --- JSON parsing ---

    internal static List<PlaylistInfo> ParsePlaylists(string json)
    {
        var results = new List<PlaylistInfo>();
        var seen = new HashSet<string>();
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
            WalkForPlaylists(doc.RootElement, results, seen, 0);
        }
        catch (JsonException) { }
        return results;
    }

    private static void WalkForPlaylists(JsonElement el, List<PlaylistInfo> out_, HashSet<string> seen, int depth)
    {
        if (depth > 20) return;
        if (el.ValueKind == JsonValueKind.Object)
        {
            // new lockupViewModel format (YouTube 2024+): contentId + contentType + nested title
            if (el.TryGetProperty("contentId", out var newIdEl) &&
                el.TryGetProperty("contentType", out var typeEl) &&
                typeEl.GetString() == "LOCKUP_CONTENT_TYPE_PLAYLIST" &&
                el.TryGetProperty("metadata", out var outerMeta) &&
                outerMeta.TryGetProperty("lockupMetadataViewModel", out var innerMeta) &&
                innerMeta.TryGetProperty("title", out var newTitleEl) &&
                newTitleEl.TryGetProperty("content", out var contentEl))
            {
                var id = newIdEl.GetString() ?? "";
                var title = contentEl.GetString() ?? "";
                if (id.Length > 0 && title.Length > 0 && seen.Add(id))
                {
                    long count = 0;
                    if (el.TryGetProperty("contentImage", out var contentImage))
                    {
                        var badgeText = WalkForBadgeText(contentImage);
                        if (badgeText != null)
                        {
                            var m = MyRegex().Match(badgeText);
                            if (m.Success) _ = long.TryParse(m.Value, out count);
                        }
                    }
                    out_.Add(new PlaylistInfo(id, title, count, null));
                }
            }
            // legacy gridPlaylistRenderer format
            if (el.TryGetProperty("playlistId", out var idEl) && el.TryGetProperty("title", out var titleEl))
            {
                var id = idEl.GetString() ?? "";
                var title = GetText(titleEl);
                if (id.Length > 0 && title.Length > 0 && seen.Add(id))
                {
                    long count = 0;
                    if (el.TryGetProperty("videoCountText", out var countEl))
                    {
                        var countText = GetText(countEl);
                        var m = MyRegex().Match(countText);
                        if (m.Success) _ = long.TryParse(m.Value, out count);
                    }
                    out_.Add(new PlaylistInfo(id, title, count, null));
                }
            }
            foreach (var prop in el.EnumerateObject())
                WalkForPlaylists(prop.Value, out_, seen, depth + 1);
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
                WalkForPlaylists(item, out_, seen, depth + 1);
        }
    }

    internal static List<VideoInfo> ParseVideos(string json)
    {
        var results = new List<VideoInfo>();
        var seen = new HashSet<string>();
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
            WalkForVideos(doc.RootElement, results, seen, 0);
        }
        catch (JsonException) { }
        return results;
    }

    private static void WalkForVideos(JsonElement el, List<VideoInfo> out_, HashSet<string> seen, int depth)
    {
        if (depth > 20) return;
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("videoId", out var idEl) && el.TryGetProperty("title", out var titleEl))
            {
                var id = idEl.GetString() ?? "";
                var title = GetText(titleEl);
                var playable = !el.TryGetProperty("isPlayable", out var p) || p.ValueKind != JsonValueKind.False;
                if (id.Length > 0 && title.Length > 0 && playable && seen.Add(id))
                {
                    string? duration = el.TryGetProperty("lengthText", out var lenEl) ? GetText(lenEl) : null;
                    out_.Add(new VideoInfo(id, title, $"https://i.ytimg.com/vi/{id}/mqdefault.jpg", out_.Count, duration));
                }
            }
            foreach (var prop in el.EnumerateObject())
                WalkForVideos(prop.Value, out_, seen, depth + 1);
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
                WalkForVideos(item, out_, seen, depth + 1);
        }
    }

    private static string? WalkForBadgeText(JsonElement el)
    {
        if (!el.TryGetProperty("collectionThumbnailViewModel", out var collVm)) return null;
        if (!collVm.TryGetProperty("primaryThumbnail", out var primary)) return null;
        if (!primary.TryGetProperty("thumbnailViewModel", out var thumbVm)) return null;
        if (!thumbVm.TryGetProperty("overlays", out var overlays) || overlays.ValueKind != JsonValueKind.Array) return null;
        foreach (var overlay in overlays.EnumerateArray())
        {
            if (!overlay.TryGetProperty("thumbnailOverlayBadgeViewModel", out var badgeVm)) continue;
            if (!badgeVm.TryGetProperty("thumbnailBadges", out var badges) || badges.ValueKind != JsonValueKind.Array) continue;
            foreach (var badge in badges.EnumerateArray())
            {
                if (!badge.TryGetProperty("thumbnailBadgeViewModel", out var badgeModel)) continue;
                if (!badgeModel.TryGetProperty("text", out var textEl)) continue;
                var text = textEl.GetString();
                if (!string.IsNullOrEmpty(text)) return text;
            }
        }
        return null;
    }

    private static string GetText(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String) return el.GetString() ?? "";
        if (el.TryGetProperty("simpleText", out var simple)) return simple.GetString() ?? "";
        if (el.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
            return string.Concat(runs.EnumerateArray()
                .Where(r => r.TryGetProperty("text", out _))
                .Select(r => r.GetProperty("text").GetString() ?? ""));
        return "";
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex MyRegex();
}
