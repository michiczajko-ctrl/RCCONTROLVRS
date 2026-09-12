using System.Collections.Concurrent;

namespace VRS.RaceControl.Shared.Protocol;

/// <summary>
/// Bounded, thread-safe cache used by clients, hosts and the relay to reject
/// a message that is delivered more than once after reconnect.
/// </summary>
public sealed class MessageDeduplicator
{
    private readonly ConcurrentDictionary<string, DateTime> _seen = new(StringComparer.Ordinal);
    private readonly TimeSpan _retention;
    private readonly int _capacity;
    private int _operations;

    public MessageDeduplicator(TimeSpan? retention = null, int capacity = 4096)
    {
        _retention = retention ?? TimeSpan.FromMinutes(10);
        _capacity = Math.Max(128, capacity);
    }

    /// <returns>True only for the first observation of the message ID.</returns>
    public bool TryAccept(ProtocolMessage message, DateTime? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(message.Id))
        {
            return false;
        }

        var now = nowUtc ?? DateTime.UtcNow;
        if (!_seen.TryAdd(message.Id, now))
        {
            return false;
        }

        if (Interlocked.Increment(ref _operations) % 128 == 0 || _seen.Count > _capacity)
        {
            RemoveExpired(now);
        }
        return true;
    }

    private void RemoveExpired(DateTime now)
    {
        var cutoff = now - _retention;
        foreach (var item in _seen)
        {
            if (item.Value < cutoff || _seen.Count > _capacity)
            {
                _seen.TryRemove(item.Key, out _);
            }
        }
    }
}
