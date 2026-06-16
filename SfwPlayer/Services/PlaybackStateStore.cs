using System.Text.Json;
using Microsoft.Extensions.Logging;
using SfwPlayer.Models;

namespace SfwPlayer.Services;

public class PlaybackStateStore(ILogger<PlaybackStateStore> log)
{
    internal string DataPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SfwPlayer", "playback-state.json");

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public void Save(PlaybackState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DataPath)!);
            File.WriteAllText(DataPath, JsonSerializer.Serialize(state, _json));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "failed to save playback state");
        }
    }

    public PlaybackState? TryLoad()
    {
        if (!File.Exists(DataPath)) return null;
        try
        {
            var state = JsonSerializer.Deserialize<PlaybackState>(File.ReadAllText(DataPath), _json);
            if (state?.Queue is { Count: > 0 }) log.LogInformation("restored playback state: {title}", state.Queue[state.QueueIndex].Title);
            return state;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "failed to load playback state");
            return null;
        }
    }
}
