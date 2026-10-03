using System.Collections.Concurrent;
using VRS.RaceControl.Shared.Models;

public sealed record AuthorityReadiness(bool Ready, TimeSpan DeliveryLead, string? Reason);

public sealed partial class RelaySession
{
    private sealed record ClockReport(string Epoch, double UncertaintyMs, DateTimeOffset ReceivedAt);
    private readonly ConcurrentDictionary<string, ClockReport> _authorityClockReports = new();
    private readonly SemaphoreSlim _authorityRenewGate = new(1, 1);
    private DateTimeOffset _lastAuthorityRenewal;

    public AuthorityReadiness GetAuthorityReadiness()
    {
        var authority = _authority;
        if (authority?.IsWriterActive != true) return new(false, TimeSpan.FromMilliseconds(500), "Authority storage is unavailable.");
        var required = _clients.Values.Where(client => client.Role == "host" || HasAuthorityControl(client)).ToArray();
        if (required.Length == 0) return new(false, TimeSpan.FromMilliseconds(500), "No control operator is connected.");
        foreach (var client in required)
            if (!_authorityClockReports.TryGetValue(client.Id, out var report) || report.Epoch != authority.ClockEpoch
                || authority.Now - report.ReceivedAt > TimeSpan.FromSeconds(15) || report.UncertaintyMs > 50)
                return new(false, TimeSpan.FromMilliseconds(500), "A control station needs clock resynchronization.");
        var leadMs = Math.Max(500, ObservedAuthorityDeliveryP99() + 2 * required.Max(c => _authorityClockReports[c.Id].UncertaintyMs));
        if (leadMs > 1500) return new(false, TimeSpan.FromMilliseconds(1500), "Recent delivery latency is too high to arm GREEN.");
        var lead = TimeSpan.FromMilliseconds(leadMs);
        return new(true, lead, null);
    }

    private void RecordAuthorityClock(RelayClient client, TimeSyncRequestPayload request)
    {
        if (_authority == null || request.ClockEpoch != _authority.ClockEpoch
            || request.UncertaintyMs is not { } uncertainty || !double.IsFinite(uncertainty) || uncertainty is < 0 or > 5000)
            return;
        _authorityClockReports[client.Id] = new(request.ClockEpoch, uncertainty, _authority.Now);
    }

    public async Task RenewAuthorityFromHeartbeatAsync(RelayClient client, CancellationToken token = default)
    {
        if (_authority == null || !HasAuthorityControl(client)) return;
        // Heartbeat receive loops never queue behind an earlier renewal.
        if (!await _authorityRenewGate.WaitAsync(0, token)) return;
        try
        {
            if (_authority.Now - _lastAuthorityRenewal < TimeSpan.FromSeconds(2)) return;
            if (await _authority.RenewAsync(client.UserId, token)) _lastAuthorityRenewal = _authority.Now;
        }
        finally { _authorityRenewGate.Release(); }
    }

    public async Task RunAuthoritySchedulerAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                if (_authority == null) return;
                foreach (var stale in _authorityClockReports.Keys.Where(id => !_clients.ContainsKey(id)))
                    _authorityClockReports.TryRemove(stale, out _);
                var readiness = GetAuthorityReadiness();
                await TickAuthorityAsync(readiness.Ready, readiness.DeliveryLead, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
