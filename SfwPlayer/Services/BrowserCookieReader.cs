using System.Net;
using System.Text;

namespace SfwPlayer.Services;

internal static class BrowserCookieReader
{
    // macOS 12+ sandboxes Safari; try the container path first, fall back to the traditional path
    private static readonly string[] SafariCookiePaths =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Containers", "com.apple.Safari", "Data", "Library", "Cookies", "Cookies.binarycookies"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Cookies", "Cookies.binarycookies"),
    ];

    internal static List<Cookie> TryReadSafariCookies()
    {
        foreach (var path in SafariCookiePaths)
        {
            var cookies = TryReadFromFile(path);
            if (cookies.Count > 0) return cookies;
        }
        return [];
    }

    internal static List<Cookie> TryReadFromFile(string path)
    {
        if (!File.Exists(path)) return [];
        try { return Parse(File.ReadAllBytes(path)); }
        catch { return []; }
    }

    internal static List<Cookie> Parse(byte[] data)
    {
        if (data.Length < 8) return [];
        if (data[0] != 'c' || data[1] != 'o' || data[2] != 'o' || data[3] != 'k') return [];

        var pageCount = ReadBE32(data, 4);
        var result = new List<Cookie>();
        var offset = 8 + pageCount * 4;

        for (var i = 0; i < pageCount; i++)
        {
            var pageSize = ReadBE32(data, 8 + i * 4);
            ParsePage(data, offset, result);
            offset += pageSize;
        }

        return result;
    }

    private static void ParsePage(byte[] data, int start, List<Cookie> result)
    {
        if (start + 8 > data.Length) return;
        // page magic: 0x00 0x00 0x01 0x00
        if (data[start] != 0 || data[start + 1] != 0 || data[start + 2] != 1 || data[start + 3] != 0) return;

        var count = ReadLE32(data, start + 4);
        for (var i = 0; i < count; i++)
        {
            var recordOffset = start + ReadLE32(data, start + 8 + i * 4);
            TryParseCookie(data, recordOffset, result);
        }
    }

    private static void TryParseCookie(byte[] data, int start, List<Cookie> result)
    {
        if (start + 56 > data.Length) return;
        var size = ReadLE32(data, start);
        if (size < 56 || start + size > data.Length) return;

        var flags = ReadLE32(data, start + 8);
        var domainOff = ReadLE32(data, start + 16);
        var nameOff = ReadLE32(data, start + 20);
        var pathOff = ReadLE32(data, start + 24);
        var valueOff = ReadLE32(data, start + 28);
        var expiry = BitConverter.ToDouble(data.AsSpan(start + 40, 8));

        var domain = CStr(data, start + domainOff);
        var name = CStr(data, start + nameOff);
        var path = CStr(data, start + pathOff);
        var value = CStr(data, start + valueOff);

        if (name.Length == 0 || domain.Length == 0) return;
        try
        {
            var c = new Cookie(name, value, path.Length > 0 ? path : "/", domain)
            {
                Secure = (flags & 1) != 0,
                HttpOnly = (flags & 4) != 0,
            };
            // mac absolute time: seconds since 2001-01-01 UTC
            if (expiry > 0)
            {
                var exp = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(expiry);
                if (exp > DateTime.UtcNow) c.Expires = exp;
            }
            result.Add(c);
        }
        catch { /* skip malformed cookies */ }
    }

    private static int ReadBE32(byte[] d, int i) =>
        (d[i] << 24) | (d[i + 1] << 16) | (d[i + 2] << 8) | d[i + 3];

    private static int ReadLE32(byte[] d, int i) =>
        d[i] | (d[i + 1] << 8) | (d[i + 2] << 16) | (d[i + 3] << 24);

    private static string CStr(byte[] d, int i)
    {
        if (i < 0 || i >= d.Length) return "";
        var end = i;
        while (end < d.Length && d[end] != 0) end++;
        return Encoding.UTF8.GetString(d, i, end - i);
    }
}
