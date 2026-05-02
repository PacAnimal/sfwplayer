#pragma warning disable CA1873 // logging calls with cheap args don't need IsEnabled guards
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SfwPlayer.Services;

public class CookieStore(ILogger<CookieStore> log)
{
    internal string DataPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SfwPlayer", "cookies.json");

    internal static string TestCookiePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "sfwplayer", "test-cookies.json");

    private List<Cookie> _cookies = [];

    public bool HasCookies => _cookies.Count > 0;

    public IReadOnlyList<Cookie> GetCookies() => _cookies;

    public void Save(IEnumerable<Cookie> cookies)
    {
        _cookies = [.. cookies.Where(c =>
            c.Domain.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
            c.Domain.Contains("google.com", StringComparison.OrdinalIgnoreCase))];

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DataPath)!);
            var dtos = _cookies.Select(c => new CookieDto(c.Name, c.Value, c.Domain, c.Path,
                c.Secure, c.HttpOnly, c.Expires == DateTime.MinValue ? null : c.Expires.ToString("O")));
            File.WriteAllText(DataPath, JsonSerializer.Serialize(dtos, SerializerOptions));
            log.LogInformation("saved {count} cookies", _cookies.Count);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "failed to persist cookies");
        }
    }

    public void TryLoad()
    {
        if (!File.Exists(DataPath)) return;
        try
        {
            var dtos = JsonSerializer.Deserialize<CookieDto[]>(File.ReadAllText(DataPath), SerializerOptions);
            if (dtos == null) return;
            _cookies = [.. dtos.Select(d =>
            {
                var c = new Cookie(d.Name, d.Value, d.Path, d.Domain)
                {
                    Secure = d.Secure,
                    HttpOnly = d.HttpOnly,
                };
                if (d.Expires != null && DateTime.TryParse(d.Expires, out var exp))
                    c.Expires = exp;
                return c;
            })];
            log.LogInformation("loaded {count} cookies", _cookies.Count);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "failed to load cookies");
        }
    }

    public void Clear()
    {
        _cookies.Clear();
        try { if (File.Exists(DataPath)) File.Delete(DataPath); }
        catch { /* best effort */ }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private record CookieDto(string Name, string Value, string Domain, string Path,
        bool Secure, bool HttpOnly, string? Expires);
}
