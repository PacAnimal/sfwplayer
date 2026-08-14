using System.Text.Json;
using SfwPlayer.Models;
using SfwPlayer.Services;
using Tests.Setup;

namespace Tests.YouTube;

[TestFixture]
public class InnerTubeServiceTests
{
    private static readonly string[] PlaylistIds = ["PL1", "PL2"];

    private static readonly long[] Positions = [0, 1, 2];

    // --- ExtractYtInitialData ---

    [Test]
    public void ExtractYtInitialData_ReturnsJson_WhenMarkerPresent()
    {
        var html = """<script>var ytInitialData = {"key":"value"};</script>""";
        var result = InnerTubeService.ExtractYtInitialData(html);
        Assert.That(result, Is.EqualTo("""{"key":"value"}"""));
    }

    [Test]
    public void ExtractYtInitialData_ReturnsNull_WhenMarkerAbsent()
    {
        var result = InnerTubeService.ExtractYtInitialData("<html></html>");
        Assert.That(result, Is.Null);
    }

    [Test]
    public void ExtractYtInitialData_ReturnsNull_WhenClosingTagAbsent()
    {
        var html = "<script>var ytInitialData = {\"key\":\"value\"}";
        var result = InnerTubeService.ExtractYtInitialData(html);
        Assert.That(result, Is.Null);
    }

    [Test]
    public void ExtractYtInitialData_IgnoresContentBeforeMarker()
    {
        var html = """<html><head></head><body><script>var ytInitialData = {"a":1};</script></body></html>""";
        var result = InnerTubeService.ExtractYtInitialData(html);
        Assert.That(result, Is.EqualTo("""{"a":1}"""));
    }

    // --- ParsePlaylists ---

    [Test]
    public void ParsePlaylists_ParsesSimpleTextTitle()
    {
        var json = """
            {
              "contents": {
                "playlistId": "PLabc123",
                "title": { "simpleText": "My Playlist" },
                "videoCountText": { "simpleText": "42 videos" }
              }
            }
            """;
        var result = InnerTubeService.ParsePlaylists(json);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].Id, Is.EqualTo("PLabc123"));
            Assert.That(result[0].Title, Is.EqualTo("My Playlist"));
            Assert.That(result[0].VideoCount, Is.EqualTo(42));
        }
    }

    [Test]
    public void ParsePlaylists_ParsesRunsTitle()
    {
        var json = """
            {
              "playlistId": "PLruns",
              "title": { "runs": [{ "text": "Run " }, { "text": "Title" }] }
            }
            """;
        var result = InnerTubeService.ParsePlaylists(json);
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Title, Is.EqualTo("Run Title"));
    }

    [Test]
    public void ParsePlaylists_DeduplicatesById()
    {
        var json = """
            [
              { "playlistId": "PL1", "title": { "simpleText": "First" } },
              { "playlistId": "PL1", "title": { "simpleText": "First Dupe" } },
              { "playlistId": "PL2", "title": { "simpleText": "Second" } }
            ]
            """;
        var result = InnerTubeService.ParsePlaylists(json);
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Select(p => p.Id), Is.EquivalentTo(PlaylistIds));
    }

    [Test]
    public void ParsePlaylists_SkipsEntriesWithEmptyIdOrTitle()
    {
        var json = """
            [
              { "playlistId": "", "title": { "simpleText": "No ID" } },
              { "playlistId": "PLok", "title": { "simpleText": "" } },
              { "playlistId": "PLgood", "title": { "simpleText": "Good" } }
            ]
            """;
        var result = InnerTubeService.ParsePlaylists(json);
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Id, Is.EqualTo("PLgood"));
    }

    [Test]
    public void ParsePlaylists_VideoCountDefaultsToZero_WhenAbsent()
    {
        var json = """{ "playlistId": "PL1", "title": { "simpleText": "No Count" } }""";
        var result = InnerTubeService.ParsePlaylists(json);
        Assert.That(result[0].VideoCount, Is.Zero);
    }

    [Test]
    public void ParsePlaylists_ReturnsEmpty_OnInvalidJson()
    {
        var result = InnerTubeService.ParsePlaylists("not json at all");
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void ParsePlaylists_ParsesLockupViewModelFormat()
    {
        var json = """
            {
              "contentId": "PLtest123",
              "contentType": "LOCKUP_CONTENT_TYPE_PLAYLIST",
              "metadata": {
                "lockupMetadataViewModel": {
                  "title": { "content": "My Playlist" }
                }
              }
            }
            """;
        var result = InnerTubeService.ParsePlaylists(json);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].Id, Is.EqualTo("PLtest123"));
            Assert.That(result[0].Title, Is.EqualTo("My Playlist"));
        }
    }

    [Test]
    public void ParsePlaylists_ParsesLockupViewModelVideoCount()
    {
        var json = """
            {
              "contentId": "PLtest456",
              "contentType": "LOCKUP_CONTENT_TYPE_PLAYLIST",
              "metadata": {
                "lockupMetadataViewModel": {
                  "title": { "content": "Counted Playlist" }
                }
              },
              "contentImage": {
                "collectionThumbnailViewModel": {
                  "primaryThumbnail": {
                    "thumbnailViewModel": {
                      "overlays": [
                        {
                          "thumbnailOverlayBadgeViewModel": {
                            "thumbnailBadges": [
                              {
                                "thumbnailBadgeViewModel": {
                                  "text": "42 videos"
                                }
                              }
                            ]
                          }
                        }
                      ]
                    }
                  }
                }
              }
            }
            """;
        var result = InnerTubeService.ParsePlaylists(json);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].Id, Is.EqualTo("PLtest456"));
            Assert.That(result[0].VideoCount, Is.EqualTo(42));
        }
    }

    [Test]
    public void ParsePlaylists_WalksNestedStructures()
    {
        var json = """
            {
              "outer": {
                "inner": {
                  "deep": {
                    "playlistId": "PLdeep",
                    "title": { "simpleText": "Deep Playlist" }
                  }
                }
              }
            }
            """;
        var result = InnerTubeService.ParsePlaylists(json);
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Id, Is.EqualTo("PLdeep"));
    }

    // --- ParseVideos ---

    [Test]
    public void ParseVideos_ParsesBasicVideo()
    {
        var json = """
            {
              "videoId": "abc123",
              "title": { "runs": [{ "text": "My Video" }] }
            }
            """;
        var result = InnerTubeService.ParseVideos(json);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].Id, Is.EqualTo("abc123"));
            Assert.That(result[0].Title, Is.EqualTo("My Video"));
        }
    }

    [Test]
    public void ParseVideos_IncludesNonPlayableVideos()
    {
        // non-playable items (private/deleted) stay in the list so they can be deleted from playlists
        var json = """
            [
              { "videoId": "v1", "title": { "simpleText": "Playable" } },
              { "videoId": "v2", "title": { "simpleText": "Not Playable" }, "isPlayable": false },
              { "videoId": "v3", "title": { "simpleText": "Also Playable" }, "isPlayable": true }
            ]
            """;
        var result = InnerTubeService.ParseVideos(json);
        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result.Select(v => v.Id), Is.EquivalentTo(["v1", "v2", "v3"]));
    }

    [Test]
    public void ParseVideos_DeduplicatesById()
    {
        var json = """
            [
              { "videoId": "v1", "title": { "simpleText": "Video 1" } },
              { "videoId": "v1", "title": { "simpleText": "Video 1 Dupe" } }
            ]
            """;
        var result = InnerTubeService.ParseVideos(json);
        Assert.That(result, Has.Count.EqualTo(1));
    }

    [Test]
    public void ParseVideos_AssignsPositionsInOrder()
    {
        var json = """
            [
              { "videoId": "v1", "title": { "simpleText": "First" } },
              { "videoId": "v2", "title": { "simpleText": "Second" } },
              { "videoId": "v3", "title": { "simpleText": "Third" } }
            ]
            """;
        var result = InnerTubeService.ParseVideos(json);
        Assert.That(result.Select(v => v.Position), Is.EqualTo(Positions));
    }

    [Test]
    public void ParseVideos_ReturnsEmpty_OnInvalidJson()
    {
        var result = InnerTubeService.ParseVideos("not json");
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void ParseVideos_SetsThumbnailUrl()
    {
        var json = """{ "videoId": "abc123", "title": { "simpleText": "My Video" } }""";
        var result = InnerTubeService.ParseVideos(json);
        Assert.That(result[0].ThumbnailUrl, Is.EqualTo("https://i.ytimg.com/vi/abc123/mqdefault.jpg"));
    }

    [Test]
    public void ParseVideos_ParsesDuration_WhenLengthTextPresent()
    {
        var json = """
            {
              "videoId": "abc123",
              "title": { "simpleText": "My Video" },
              "lengthText": { "simpleText": "3:45" }
            }
            """;
        var result = InnerTubeService.ParseVideos(json);
        Assert.That(result[0].Duration, Is.EqualTo("3:45"));
    }

    [Test]
    public void ParseVideos_DurationIsNull_WhenLengthTextAbsent()
    {
        var json = """{ "videoId": "abc123", "title": { "simpleText": "My Video" } }""";
        var result = InnerTubeService.ParseVideos(json);
        Assert.That(result[0].Duration, Is.Null);
    }

    // --- ExtractContinuationToken ---

    [Test]
    public void ExtractContinuationToken_ReturnsNull_WhenAbsent()
    {
        using var doc = JsonDocument.Parse("""{"key":"value"}""");
        Assert.That(InnerTubeService.ExtractContinuationToken(doc.RootElement), Is.Null);
    }

    [Test]
    public void ExtractContinuationToken_ReturnsToken_WhenPresent()
    {
        var json = """{ "continuationCommand": { "token": "abc123tok" } }""";
        using var doc = JsonDocument.Parse(json);
        Assert.That(InnerTubeService.ExtractContinuationToken(doc.RootElement), Is.EqualTo("abc123tok"));
    }

    [Test]
    public void ExtractContinuationToken_FindsNestedToken()
    {
        var json = """
            {
              "continuationItemRenderer": {
                "continuationEndpoint": {
                  "continuationCommand": {
                    "token": "nested_tok",
                    "request": "CONTINUATION_REQUEST_TYPE_BROWSE"
                  }
                }
              }
            }
            """;
        using var doc = JsonDocument.Parse(json);
        Assert.That(InnerTubeService.ExtractContinuationToken(doc.RootElement), Is.EqualTo("nested_tok"));
    }

    [Test]
    public void ExtractContinuationToken_FindsTokenInArray()
    {
        var json = """
            [
              { "videoId": "v1", "title": { "simpleText": "Video 1" } },
              { "continuationItemRenderer": { "continuationEndpoint": { "continuationCommand": { "token": "arr_tok" } } } }
            ]
            """;
        using var doc = JsonDocument.Parse(json);
        Assert.That(InnerTubeService.ExtractContinuationToken(doc.RootElement), Is.EqualTo("arr_tok"));
    }

    // integration tests: require ~/.config/sfwplayer/test-cookies.json (gitignored; use 'Save Test Cookies' in the app)

    private static readonly SemaphoreSlim FirstVideosGate = new(1, 1);
    private static List<VideoInfo>? _firstVideos;

    [Test]
    [CancelAfter(180_000)]
    public async Task GetPlaylistsAsync_WithRealAuth_ReturnsPlaylists(CancellationToken cancel)
    {
        var store = RequireTestStore();
        var svc = new InnerTubeService(store, TestLog.CreateLogger<InnerTubeService>());
        var playlists = await YoutubeThrottle.PaceAsync(() => svc.GetPlaylistsAsync(cancel), cancel);
        Assert.That(playlists, Is.Not.Empty, "should return playlists when authenticated");
    }

    [Test]
    [CancelAfter(180_000)]
    public async Task GetPlaylistVideosAsync_WithRealAuth_ReturnsVideos(CancellationToken cancel)
    {
        var videos = await RequireFirstVideosAsync(cancel);
        Assert.That(videos, Is.Not.Empty);
    }

    [Test]
    [CancelAfter(180_000)]
    public async Task GetPlaylistVideosAsync_WithRealAuth_VideoHasThumbnailUrl(CancellationToken cancel)
    {
        var videos = await RequireFirstVideosAsync(cancel);
        var video = videos.First();
        Assert.That(video.ThumbnailUrl, Does.StartWith("https://i.ytimg.com/vi/").And.EndsWith("/mqdefault.jpg"),
            $"video {video.Id} should have a mqdefault thumbnail URL");
    }

    [Test]
    [CancelAfter(180_000)]
    public async Task GetPlaylistVideosAsync_WithRealAuth_SomeVideosHaveDuration(CancellationToken cancel)
    {
        var videos = await RequireFirstVideosAsync(cancel);
        Assert.That(videos.Any(v => v.Duration != null), Is.True,
            "at least one video should have a duration parsed from lengthText");
    }

    [Test]
    [CancelAfter(180_000)]
    public async Task GetPlaylistVideosAsync_WithRealAuth_ThumbnailIsDownloadable(CancellationToken cancel)
    {
        var videos = await RequireFirstVideosAsync(cancel);
        var video = videos.First(v => v.ThumbnailUrl != null);
        using var http = new HttpClient();
        var response = await http.GetAsync(video.ThumbnailUrl, cancel);
        using (Assert.EnterMultipleScope())
        {
            Assert.That((int)response.StatusCode, Is.EqualTo(200), $"thumbnail for {video.Id} should return 200");
            Assert.That(response.Content.Headers.ContentType?.MediaType, Does.StartWith("image/"),
                "thumbnail response should be an image");
        }
    }

    [Test]
    [CancelAfter(180_000)]
    public async Task GetStreamUrl_WithRealAuth_FromPlaylistVideo_ReturnsUrl(CancellationToken cancel)
    {
        var store = RequireTestStore();
        var svc = new InnerTubeService(store, TestLog.CreateLogger<InnerTubeService>());
        var videos = await RequireFirstVideosAsync(svc, cancel);
        var yt = new YoutubeService(TestLog.CreateLogger<YoutubeService>(), store);
        var url = await YoutubeThrottle.PaceAsync(() => yt.GetStreamUrl(videos.First().Id, cancel), cancel);
        Assert.That(url, Does.StartWith("https://"), $"stream URL for {videos.First().Id} should be https");
    }


    [Test]
    [CancelAfter(240_000)]
    public async Task GetPlaylistVideosAsync_WatchLater_LoadsAllDeclaredVideos(CancellationToken cancel)
    {
        var store = RequireTestStore();
        var svc = new InnerTubeService(store, TestLog.CreateLogger<InnerTubeService>());
        using var http = svc.BuildClient();
        var html = await YoutubeThrottle.PaceAsync(
            () => http.GetStringAsync("https://www.youtube.com/playlist?list=WL", cancel), cancel);
        var json = InnerTubeService.ExtractYtInitialData(html);
        Assert.That(json, Is.Not.Null, "ytInitialData not found in Watch Later page");
        using var doc = JsonDocument.Parse(json!);
        var declared = InnerTubeService.ExtractPlaylistVideoCount(doc.RootElement);
        Assume.That(declared, Is.GreaterThan(0), "Watch Later appears empty or count not parseable; skipping");

        var videos = await YoutubeThrottle.PaceAsync(() => svc.GetPlaylistVideosAsync("WL", cancel), cancel);
        Assert.That(videos, Has.Count.EqualTo(declared),
            $"Watch Later declared {declared} videos but loaded {videos.Count}");
    }

    // finds the first playlist that has videos; skips the test if none found
    private static async Task<List<VideoInfo>> RequireFirstVideosAsync(CancellationToken cancel)
    {
        var store = RequireTestStore();
        var svc = new InnerTubeService(store, TestLog.CreateLogger<InnerTubeService>());
        return await RequireFirstVideosAsync(svc, cancel);
    }

    // several tests assert on the same playlist payload; fetching it once keeps the
    // suite from re-paginating every playlist per test and tripping YouTube's throttle
    private static async Task<List<VideoInfo>> RequireFirstVideosAsync(InnerTubeService svc, CancellationToken cancel)
    {
        await FirstVideosGate.WaitAsync(cancel);
        try
        {
            if (_firstVideos != null) return _firstVideos;

            var playlists = await YoutubeThrottle.PaceAsync(() => svc.GetPlaylistsAsync(cancel), cancel);
            if (playlists.Count == 0)
                Assert.Ignore("no playlists found; check test credentials");

            foreach (var playlist in playlists)
            {
                var videos = await YoutubeThrottle.PaceAsync(() => svc.GetPlaylistVideosAsync(playlist.Id, cancel), cancel);
                if (videos.Count > 0) return _firstVideos = videos;
            }
        }
        finally
        {
            FirstVideosGate.Release();
        }
        Assert.Ignore("no playlist with videos found; check test credentials");
        return []; // unreachable
    }

    private static CookieStore RequireTestStore()
    {
        var cookiePath = CookieStore.TestCookiePath;
        if (!File.Exists(cookiePath))
            Assert.Ignore($"test cookies not found at {cookiePath}; use 'Save Test Cookies' in the app");

        var store = new CookieStore(TestLog.CreateLogger<CookieStore>()) { DataPath = cookiePath };
        store.TryLoad();
        if (!store.HasCookies)
            Assert.Ignore("no cookies in test store; re-sign-in to the app first");
        return store;
    }
}
