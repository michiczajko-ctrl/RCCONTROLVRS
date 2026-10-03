namespace VRS.RaceControl.Shared.Services;

public sealed record ResourceLease(string ResourceId, string OperatorId, DateTime ExpiresAtUtc);

public sealed class ResourceLeaseManager
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ResourceLease> _leases = new(StringComparer.Ordinal);

    public ResourceLease? TryAcquire(string resourceId, string operatorId, DateTime nowUtc, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        lock (_gate)
        {
            if (_leases.TryGetValue(resourceId, out var current)
                && current.ExpiresAtUtc > nowUtc
                && !string.Equals(current.OperatorId, operatorId, StringComparison.Ordinal)) return null;
            var lease = new ResourceLease(resourceId, operatorId, nowUtc.Add(duration));
            _leases[resourceId] = lease;
            return lease;
        }
    }

    public bool Release(string resourceId, string operatorId)
    {
        lock (_gate)
            return _leases.TryGetValue(resourceId, out var lease)
                && string.Equals(lease.OperatorId, operatorId, StringComparison.Ordinal)
                && _leases.Remove(resourceId);
    }
}
