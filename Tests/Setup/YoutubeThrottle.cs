namespace Tests.Setup;

// YouTube throttles bursts of requests from one IP, which surfaces as
// VideoUnavailableException / empty playlist pages on otherwise valid content.
// Integration tests funnel their calls through here so the suite trickles
// requests instead of bursting them.
internal static class YoutubeThrottle
{
    private const int MaxRetries = 3;
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(3);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _lastCallUtc = DateTime.MinValue;

    // serialises callers, guarantees at least MinInterval between successive requests,
    // and retries with escalating backoff when YouTube rejects a throttled request
    public static async Task<T> PaceAsync<T>(Func<Task<T>> action, CancellationToken cancel = default)
    {
        await Gate.WaitAsync(cancel);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var since = DateTime.UtcNow - _lastCallUtc;
                if (since < MinInterval)
                    await Task.Delay(MinInterval - since, cancel);

                try
                {
                    return await action();
                }
                catch (Exception ex) when (attempt < MaxRetries && IsThrottleSymptom(ex))
                {
                    _lastCallUtc = DateTime.UtcNow;
                    await Task.Delay(BackoffFor(attempt), cancel);
                }
            }
        }
        finally
        {
            _lastCallUtc = DateTime.UtcNow;
            Gate.Release();
        }
    }

    // YouTube reports rate limiting as "unavailable" content rather than a 429
    private static bool IsThrottleSymptom(Exception ex) =>
        ex.GetType().Name is "VideoUnavailableException" or "PlaylistUnavailableException"
        || ex is HttpRequestException;

    private static TimeSpan BackoffFor(int attempt) => TimeSpan.FromSeconds(15 * (attempt + 1));

    public static async Task PaceAsync(Func<Task> action, CancellationToken cancel = default) =>
        await PaceAsync<object?>(async () => { await action(); return null; }, cancel);
}
