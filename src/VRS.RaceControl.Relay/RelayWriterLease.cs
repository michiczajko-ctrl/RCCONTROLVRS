using System.Diagnostics;

/// <summary>Conservative local deadline; database lease is longer and fences each authority mutation.</summary>
public sealed class RelayWriterLease(IAuthorityStore store, string sessionId, string clockEpoch, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private long _lastConfirmedRequest;
    private int _lost;
    private readonly SemaphoreSlim _renewGate = new(1, 1);
    public bool IsHeld
    {
        get
        {
            if (!store.SupportsRelayWriterLease) return true;
            if (Volatile.Read(ref _lost) != 0) return false;
            var confirmed = Interlocked.Read(ref _lastConfirmedRequest);
            if (confirmed == 0) return false;
            if (_time.GetElapsedTime(confirmed) < TimeSpan.FromSeconds(10)) return true;
            Interlocked.Exchange(ref _lost, 1); return false;
        }
    }
    public async Task AcquireAsync(CancellationToken token)
    {
        if (!store.SupportsRelayWriterLease) return;
        var started = _time.GetTimestamp();
        if (!await store.AcquireRelayWriterAsync(sessionId, clockEpoch, token)
            || _time.GetElapsedTime(started) >= TimeSpan.FromSeconds(10))
            throw new IOException("Another Relay owns the session, or writer acquisition is uncertain.");
        Interlocked.Exchange(ref _lastConfirmedRequest, started);
    }
    public async Task RenewAsync(CancellationToken token)
    {
        if (!store.SupportsRelayWriterLease || !IsHeld || !await _renewGate.WaitAsync(0, token)) return;
        try
        {
            var started = _time.GetTimestamp();
            try
            {
                var renewed = await store.RenewRelayWriterAsync(sessionId, clockEpoch, token);
                if (!renewed) { Interlocked.Exchange(ref _lost, 1); return; }
                // A slow reply cannot resurrect a writer whose local deadline has already expired.
                if (IsHeld) Interlocked.Exchange(ref _lastConfirmedRequest, started);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            { _ = IsHeld; } // Retain only the previous conservative deadline, never extend on failure.
        }
        finally { _renewGate.Release(); }
    }
}
