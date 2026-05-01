using System.Net;
using System.Text;
using SfwPlayer.Services;

namespace Tests.Auth;

[TestFixture]
public class BrowserCookieReaderTests
{
    // --- parse correctness (synthetic binary data) ---

    [Test]
    public void Parse_EmptyData_ReturnsEmpty()
    {
        Assert.That(BrowserCookieReader.Parse([]), Is.Empty);
    }

    [Test]
    public void Parse_WrongMagic_ReturnsEmpty()
    {
        var data = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x00, 0x00, 0x00, 0x00 };
        Assert.That(BrowserCookieReader.Parse(data), Is.Empty);
    }

    [Test]
    public void Parse_SingleCookie_ExtractsNameValueDomain()
    {
        var data = BuildFile(
            domain: ".youtube.com",
            name: "SID",
            path: "/",
            value: "test_token_123",
            flags: 0,
            expiryMacAbs: 1_000_000_000.0);

        var cookies = BrowserCookieReader.Parse(data);

        Assert.That(cookies, Has.Count.EqualTo(1));
        var c = cookies[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(c.Name, Is.EqualTo("SID"));
            Assert.That(c.Value, Is.EqualTo("test_token_123"));
            Assert.That(c.Domain, Is.EqualTo(".youtube.com"));
            Assert.That(c.Path, Is.EqualTo("/"));
        }
    }

    [Test]
    public void Parse_SecureFlag_SetsCookieSecure()
    {
        var data = BuildFile(flags: 1);
        var c = BrowserCookieReader.Parse(data)[0];
        Assert.That(c.Secure, Is.True);
        Assert.That(c.HttpOnly, Is.False);
    }

    [Test]
    public void Parse_HttpOnlyFlag_SetsCookieHttpOnly()
    {
        var data = BuildFile(flags: 4);
        var c = BrowserCookieReader.Parse(data)[0];
        Assert.That(c.Secure, Is.False);
        Assert.That(c.HttpOnly, Is.True);
    }

    [Test]
    public void Parse_BothFlags_SetsBothProperties()
    {
        var data = BuildFile(flags: 5);
        var c = BrowserCookieReader.Parse(data)[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(c.Secure, Is.True);
            Assert.That(c.HttpOnly, Is.True);
        }
    }

    [Test]
    public void Parse_FutureExpiry_SetsExpiresProperty()
    {
        // 2030-01-01 in mac absolute time (seconds since 2001-01-01)
        var data = BuildFile(expiryMacAbs: 915_148_800.0);
        var c = BrowserCookieReader.Parse(data)[0];
        Assert.That(c.Expires, Is.GreaterThan(DateTime.UtcNow));
    }

    [Test]
    public void Parse_ZeroExpiry_NoExpirySet()
    {
        var data = BuildFile(expiryMacAbs: 0.0);
        var c = BrowserCookieReader.Parse(data)[0];
        // session cookie — Expires is DateTime.MinValue (no expiry set)
        Assert.That(c.Expires, Is.LessThanOrEqualTo(DateTime.MinValue.AddSeconds(1)));
    }

    [Test]
    public void Parse_PastExpiry_NoExpirySet()
    {
        // 2002-01-01 in mac absolute time — definitely in the past
        var data = BuildFile(expiryMacAbs: 31_536_000.0);
        var c = BrowserCookieReader.Parse(data)[0];
        // past expiry → not set (we skip it)
        Assert.That(c.Expires, Is.LessThanOrEqualTo(DateTime.MinValue.AddSeconds(1)));
    }

    [Test]
    public void Parse_EmptyPath_DefaultsToSlash()
    {
        var data = BuildFile(path: "");
        var c = BrowserCookieReader.Parse(data)[0];
        Assert.That(c.Path, Is.EqualTo("/"));
    }

    [Test]
    public void Parse_MultipleCookies_ParsesAll()
    {
        var page1 = BuildPage([
            BuildCookieRecord(".youtube.com", "SID", "/", "sid_val"),
            BuildCookieRecord(".google.com", "GAPS", "/", "gaps_val"),
        ]);
        var data = BuildFileFromPages([page1]);

        var cookies = BrowserCookieReader.Parse(data);

        Assert.That(cookies, Has.Count.EqualTo(2));
        Assert.That(cookies.Select(c => c.Name), Is.EquivalentTo(["SID", "GAPS"]));
    }

    [Test]
    public void Parse_MultiplePages_ParsesAll()
    {
        var page1 = BuildPage([BuildCookieRecord(".youtube.com", "SID", "/", "v1")]);
        var page2 = BuildPage([BuildCookieRecord(".google.com", "GAPS", "/", "v2")]);
        var data = BuildFileFromPages([page1, page2]);

        var cookies = BrowserCookieReader.Parse(data);

        Assert.That(cookies, Has.Count.EqualTo(2));
    }

    // --- integration: read actual Safari cookie file ---

    [Test]
    public void TryReadSafariCookies_DoesNotThrow()
    {
        if (!OperatingSystem.IsMacOS()) Assert.Ignore("macOS only");
        List<Cookie>? result = null;
        Assert.DoesNotThrow(() => result = BrowserCookieReader.TryReadSafariCookies());
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public void TryReadSafariCookies_IfYouTubeSessionExists_HasSidCookie()
    {
        if (!OperatingSystem.IsMacOS()) Assert.Ignore("macOS only");
        var cookies = BrowserCookieReader.TryReadSafariCookies();
        if (cookies.Count == 0)
            Assert.Ignore("no Safari cookies found — sign in to YouTube in Safari and re-run");

        // if we have Safari cookies, they should all have valid names and domains
        foreach (var c in cookies)
        {
            Assert.That(c.Name, Is.Not.Empty);
            Assert.That(c.Domain, Is.Not.Empty);
        }
    }

    // --- binary builder helpers ---

    private static byte[] BuildFile(
        string domain = ".youtube.com",
        string name = "SID",
        string path = "/",
        string value = "test_value",
        int flags = 0,
        double expiryMacAbs = 1_000_000_000.0)
    {
        var page = BuildPage([BuildCookieRecord(domain, name, path, value, flags, expiryMacAbs)]);
        return BuildFileFromPages([page]);
    }

    private static byte[] BuildFileFromPages(List<byte[]> pages)
    {
        var buf = new List<byte>();
        buf.AddRange(Encoding.ASCII.GetBytes("cook")); // file magic
        buf.AddRange(BE32(pages.Count));               // page count
        foreach (var p in pages) buf.AddRange(BE32(p.Length)); // page sizes
        foreach (var p in pages) buf.AddRange(p);               // page data
        return [.. buf];
    }

    private static byte[] BuildPage(List<byte[]> cookieRecords)
    {
        // offsets are from page start; cookies follow the header area:
        // 4 (page magic) + 4 (count) + count*4 (offset table)
        var headerSize = 4 + 4 + cookieRecords.Count * 4;
        var offsets = new int[cookieRecords.Count];
        var pos = headerSize;
        for (var i = 0; i < cookieRecords.Count; i++)
        {
            offsets[i] = pos;
            pos += cookieRecords[i].Length;
        }

        var buf = new List<byte>();
        buf.AddRange([0x00, 0x00, 0x01, 0x00]); // page magic
        buf.AddRange(LE32(cookieRecords.Count));
        foreach (var off in offsets) buf.AddRange(LE32(off));
        foreach (var rec in cookieRecords) buf.AddRange(rec);
        return [.. buf];
    }

    private static byte[] BuildCookieRecord(
        string domain,
        string name,
        string path,
        string value,
        int flags = 0,
        double expiryMacAbs = 1_000_000_000.0)
    {
        var domainB = Encoding.UTF8.GetBytes(domain + "\0");
        var nameB = Encoding.UTF8.GetBytes(name + "\0");
        var pathB = Encoding.UTF8.GetBytes(path.Length > 0 ? path + "\0" : "/\0");
        var valueB = Encoding.UTF8.GetBytes(value + "\0");

        // offsets from record start; 56-byte fixed header precedes the strings
        var domainOff = 56;
        var nameOff = domainOff + domainB.Length;
        var pathOff = nameOff + nameB.Length;
        var valueOff = pathOff + pathB.Length;
        var recordSize = valueOff + valueB.Length;

        var buf = new List<byte>();
        buf.AddRange(LE32(recordSize));
        buf.AddRange(LE32(0));           // version
        buf.AddRange(LE32(flags));       // flags
        buf.AddRange(LE32(0));           // has_port
        buf.AddRange(LE32(domainOff));
        buf.AddRange(LE32(nameOff));
        buf.AddRange(LE32(pathOff));
        buf.AddRange(LE32(valueOff));
        buf.AddRange(LE32(0));           // comment offset
        buf.AddRange(LE32(0));           // comment url offset
        buf.AddRange(DoubleLE(expiryMacAbs));
        buf.AddRange(DoubleLE(0.0));     // creation
        buf.AddRange(domainB);
        buf.AddRange(nameB);
        buf.AddRange(pathB);
        buf.AddRange(valueB);
        return [.. buf];
    }

    private static byte[] BE32(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
    private static byte[] LE32(int v) => [(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)];
    private static byte[] DoubleLE(double v) => BitConverter.GetBytes(v);
}
