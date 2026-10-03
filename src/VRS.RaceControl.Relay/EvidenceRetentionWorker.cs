public sealed record EvidenceRetentionStatus(string State, long DeletedClips, DateTimeOffset? LastRun);

public sealed class EvidenceRetentionWorker(Func<CancellationToken, Task<int>> purge,
    Action<string> failure, TimeSpan? interval = null)
{
    private long _deletedClips, _lastRunTicks;
    private volatile string _state = "idle";
    public EvidenceRetentionStatus Status => new(_state, Interlocked.Read(ref _deletedClips),
        Interlocked.Read(ref _lastRunTicks) is var ticks && ticks > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null);

    public async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(interval ?? TimeSpan.FromHours(1));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                _state = "running";
                try
                {
                    var count = await purge(token);
                    if (count is < 0 or > 100) throw new InvalidDataException("Invalid retention batch count.");
                    Interlocked.Add(ref _deletedClips, count); _state = "healthy";
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                { _state = "degraded"; failure(ex.GetType().Name); }
                finally { Interlocked.Exchange(ref _lastRunTicks, DateTimeOffset.UtcNow.UtcTicks); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
