using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using SfwPlayer.Services;

namespace Tests.YouTube;

[TestFixture]
public class CookieStoreTests
{
    private string _tempPath = null!;
    private CookieStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), $"SfwPlayer-test-{Guid.NewGuid()}.json");
        _store = new CookieStore(NullLogger<CookieStore>.Instance) { DataPath = _tempPath };
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_tempPath)) File.Delete(_tempPath);
    }

    // --- HasCookies ---

    [Test]
    public void HasCookies_FalseInitially()
    {
        Assert.That(_store.HasCookies, Is.False);
    }

    [Test]
    public void HasCookies_TrueAfterSavingYoutubeCookie()
    {
        _store.Save([YoutubeCookie("SID", "abc")]);
        Assert.That(_store.HasCookies, Is.True);
    }

    // --- Save / GetCookies ---

    [Test]
    public void Save_StoresYoutubeCookiesInMemory()
    {
        _store.Save([YoutubeCookie("SID", "abc"), YoutubeCookie("HSID", "xyz")]);
        Assert.That(_store.GetCookies(), Has.Count.EqualTo(2));
    }

    [Test]
    public void Save_FiltersOutNonYoutubeNonGoogleCookies()
    {
        _store.Save([
            YoutubeCookie("SID", "abc"),
            new Cookie("tracker", "val", "/", ".example.com"),
            GoogleCookie("GAPS", "g1"),
        ]);
        var cookies = _store.GetCookies();
        Assert.That(cookies, Has.Count.EqualTo(2));
        Assert.That(cookies.Select(c => c.Domain), Has.All.Matches<string>(d =>
            d.Contains("youtube.com") || d.Contains("google.com")));
    }

    [Test]
    public void Save_PersistsCookiesToFile()
    {
        _store.Save([YoutubeCookie("SID", "mysecret")]);
        Assert.That(File.Exists(_tempPath), Is.True);
        var text = File.ReadAllText(_tempPath);
        Assert.That(text, Does.Contain("mysecret"));
    }

    // --- TryLoad ---

    [Test]
    public void TryLoad_DoesNothing_WhenFileAbsent()
    {
        _store.TryLoad(); // no file exists
        Assert.That(_store.HasCookies, Is.False);
    }

    [Test]
    public void TryLoad_RestoresCookies_AfterSave()
    {
        _store.Save([YoutubeCookie("SID", "roundtrip"), YoutubeCookie("HSID", "xyz")]);

        var loaded = new CookieStore(NullLogger<CookieStore>.Instance) { DataPath = _tempPath };
        loaded.TryLoad();

        Assert.That(loaded.GetCookies(), Has.Count.EqualTo(2));
        Assert.That(loaded.GetCookies().Any(c => c.Name == "SID" && c.Value == "roundtrip"), Is.True);
    }

    [Test]
    public void TryLoad_PreservesCookieProperties()
    {
        var original = new Cookie("SID", "val", "/", ".youtube.com")
        {
            Secure = true,
            HttpOnly = true,
            Expires = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        _store.Save([original]);

        var loaded = new CookieStore(NullLogger<CookieStore>.Instance) { DataPath = _tempPath };
        loaded.TryLoad();

        var c = loaded.GetCookies()[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(c.Name, Is.EqualTo("SID"));
            Assert.That(c.Value, Is.EqualTo("val"));
            Assert.That(c.Secure, Is.True);
            Assert.That(c.HttpOnly, Is.True);
        }
    }

    [Test]
    public void TryLoad_DoesNotThrow_OnCorruptFile()
    {
        File.WriteAllText(_tempPath, "not valid json {{{{");
        Assert.DoesNotThrow(() => _store.TryLoad());
        Assert.That(_store.HasCookies, Is.False);
    }

    // --- Clear ---

    [Test]
    public void Clear_RemovesCookiesFromMemory()
    {
        _store.Save([YoutubeCookie("SID", "abc")]);
        _store.Clear();
        Assert.That(_store.HasCookies, Is.False);
    }

    [Test]
    public void Clear_DeletesPersistedFile()
    {
        _store.Save([YoutubeCookie("SID", "abc")]);
        Assert.That(File.Exists(_tempPath), Is.True);
        _store.Clear();
        Assert.That(File.Exists(_tempPath), Is.False);
    }

    [Test]
    public void Clear_DoesNotThrow_WhenNoFileExists()
    {
        Assert.DoesNotThrow(() => _store.Clear());
    }

    // --- helpers ---

    private static Cookie YoutubeCookie(string name, string value) =>
        new(name, value, "/", ".youtube.com");

    private static Cookie GoogleCookie(string name, string value) =>
        new(name, value, "/", ".google.com");
}
