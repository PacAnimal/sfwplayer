#pragma warning disable CA1873 // logging calls with cheap args don't need IsEnabled guards
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SfwPlayer.Models;

namespace SfwPlayer.Services;

public partial class InnerTubeService(CookieStore cookies, ILogger<InnerTubeService> log)
{
    public const string ChromeUa =
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
        AddAuthHeader(http);

        var results = new List<VideoInfo>();
        var seen = new HashSet<string>();
        var usedTokens = new HashSet<string>();

        // wgYCCAA= is the "show unavailable videos" browse params
        var initBody = JsonSerializer.Serialize(new
        {
            context = new { client = new { clientName = "WEB", clientVersion = "2.20240101.00.00", hl = "en", gl = "US" } },
            browseId = $"VL{playlistId}",
            @params = "wgYCCAA="
        });
        using var initContent = new StringContent(initBody, Encoding.UTF8, "application/json");
        var initResp = await http.PostAsync("https://www.youtube.com/youtubei/v1/browse", initContent, cancel);
        if (!initResp.IsSuccessStatusCode)
        {
            log.LogWarning("browse request returned {status}", initResp.StatusCode);
            return results;
        }
        var initJson = await initResp.Content.ReadAsStringAsync(cancel);
        string? token;
        try
        {
            using var doc = JsonDocument.Parse(initJson, new JsonDocumentOptions { AllowTrailingCommas = true });
            WalkForVideos(doc.RootElement, results, seen, 0);
            token = ExtractContinuationToken(doc.RootElement);
        }
        catch (JsonException ex)
        {
            log.LogWarning("failed to parse browse response json: {msg}", ex.Message);
            return results;
        }

        while (token != null && usedTokens.Add(token))
        {
            log.LogInformation("fetching continuation for playlist {id} ({count} so far)", playlistId, results.Count);
            var contBody = JsonSerializer.Serialize(new
            {
                context = new { client = new { clientName = "WEB", clientVersion = "2.20240101.00.00", hl = "en", gl = "US" } },
                continuation = token
            });
            using var reqContent = new StringContent(contBody, Encoding.UTF8, "application/json");
            var resp = await http.PostAsync("https://www.youtube.com/youtubei/v1/browse", reqContent, cancel);
            if (!resp.IsSuccessStatusCode)
            {
                log.LogWarning("continuation request returned {status}", resp.StatusCode);
                break;
            }
            var contJson = await resp.Content.ReadAsStringAsync(cancel);
            string? nextToken;
            try
            {
                using var contDoc = JsonDocument.Parse(contJson, new JsonDocumentOptions { AllowTrailingCommas = true });
                WalkForVideos(contDoc.RootElement, results, seen, 0);
                nextToken = ExtractContinuationToken(contDoc.RootElement);
            }
            catch (JsonException ex)
            {
                log.LogWarning("failed to parse continuation json: {msg}", ex.Message);
                break;
            }
            token = nextToken;
        }

        log.LogInformation("found {count} videos in playlist {id}", results.Count, playlistId);
        return results;
    }

    public HttpClient BuildClient()
    {
        var handler = new HttpClientHandler { UseCookies = false };
        var http = new HttpClient(handler);
        http.DefaultRequestHeaders.UserAgent.ParseAdd(ChromeUa);
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");

        var youtubeCookies = cookies.GetCookies()
            .Where(c => c.Domain.Contains("youtube.com", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (youtubeCookies.Count > 0)
            http.DefaultRequestHeaders.Add("Cookie",
                string.Join("; ", youtubeCookies.Select(c => $"{c.Name}={c.Value}")));

        return http;
    }

    public async Task RemovePlaylistItemAsync(string playlistId, string setVideoId, string videoId, CancellationToken cancel)
    {
        log.LogInformation("removing video {videoId} (set={setVideoId}) from playlist {playlistId}", videoId, setVideoId, playlistId);
        using var http = BuildClient();
        AddAuthHeader(http);
        var body = JsonSerializer.Serialize(new
        {
            context = new { client = new { clientName = "WEB", clientVersion = "2.20240101.00.00", hl = "en", gl = "US" } },
            playlistId,
            actions = new[] { new { action = "ACTION_REMOVE_VIDEO", setVideoId, removedVideoId = videoId } }
        });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var resp = await http.PostAsync("https://www.youtube.com/youtubei/v1/browse/edit_playlist", content, cancel);
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(cancel);
            log.LogWarning("edit_playlist returned {status}: {body}", resp.StatusCode, text);
            resp.EnsureSuccessStatusCode();
        }
        log.LogInformation("removed video {videoId} from playlist {playlistId}", videoId, playlistId);
    }

    private void AddAuthHeader(HttpClient http)
    {
        var sapisid = cookies.GetCookies().FirstOrDefault(c => c.Name == "SAPISID")?.Value;
        if (sapisid == null) return;
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var hash = Convert.ToHexString(SHA1.HashData(
            Encoding.UTF8.GetBytes($"{ts} {sapisid} https://www.youtube.com")
        )).ToLowerInvariant();
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"SAPISIDHASH {ts}_{hash}");
        http.DefaultRequestHeaders.Add("X-Origin", "https://www.youtube.com");
    }

    // --- HTML extraction ---

    internal static string? ExtractYtInitialData(string html)
    {
        const string marker = "var ytInitialData = ";
        var idx = html.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        idx += marker.Length;
        var end = html.IndexOf(";</script>", idx, StringComparison.Ordinal);
        return end < 0 ? null : html[idx..end];
    }

    internal static string? ExtractContinuationToken(JsonElement el, int depth = 0)
    {
        if (depth > 30) return null;
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("continuationCommand", out var cmd) &&
                cmd.TryGetProperty("token", out var tokenEl))
                return tokenEl.GetString();
            foreach (var prop in el.EnumerateObject())
            {
                var found = ExtractContinuationToken(prop.Value, depth + 1);
                if (found != null) return found;
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                var found = ExtractContinuationToken(item, depth + 1);
                if (found != null) return found;
            }
        }
        return null;
    }

    internal static int ExtractPlaylistVideoCount(JsonElement el, int depth = 0)
    {
        if (depth > 20) return -1;
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("numVideosText", out var numVideosText))
            {
                var m = MyRegex().Match(GetText(numVideosText));
                if (m.Success && int.TryParse(m.Value, out var n)) return n;
            }
            foreach (var prop in el.EnumerateObject())
            {
                var found = ExtractPlaylistVideoCount(prop.Value, depth + 1);
                if (found >= 0) return found;
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                var found = ExtractPlaylistVideoCount(item, depth + 1);
                if (found >= 0) return found;
            }
        }
        return -1;
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

    private static void WalkForPlaylists(JsonElement el, List<PlaylistInfo> results, HashSet<string> seen, int depth)
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
                    results.Add(new PlaylistInfo(id, title, count, null));
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
                    results.Add(new PlaylistInfo(id, title, count, null));
                }
            }
            foreach (var prop in el.EnumerateObject())
                WalkForPlaylists(prop.Value, results, seen, depth + 1);
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
                WalkForPlaylists(item, results, seen, depth + 1);
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

    private static void WalkForVideos(JsonElement el, List<VideoInfo> results, HashSet<string> seen, int depth)
    {
        if (depth > 20) return;
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("videoId", out var idEl) && el.TryGetProperty("title", out var titleEl))
            {
                var id = idEl.GetString() ?? "";
                var title = GetText(titleEl);
                if (id.Length > 0 && title.Length > 0 && seen.Add(id))
                {
                    string? duration = el.TryGetProperty("lengthText", out var lenEl) ? GetText(lenEl) : null;
                    string? setVideoId = el.TryGetProperty("setVideoId", out var setIdEl) ? setIdEl.GetString() : null;
                    results.Add(new VideoInfo(id, title, $"https://i.ytimg.com/vi/{id}/mqdefault.jpg", results.Count, duration, setVideoId));
                }
            }
            foreach (var prop in el.EnumerateObject())
                WalkForVideos(prop.Value, results, seen, depth + 1);
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
                WalkForVideos(item, results, seen, depth + 1);
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
