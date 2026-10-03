using VRS.RaceControl.Shared.AIEngineer.Models;

namespace VRS.RaceControl.Shared.AIEngineer.Services;

public sealed class EngineerMessageQueue
{
    private readonly List<EngineerMessage> _queue = new();
    private readonly Dictionary<string, DateTime> _cooldowns = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private int _maxLength;

    public EngineerMessageQueue(int maxLength = 16)
    {
        _maxLength = Math.Max(1, maxLength);
    }

    /// <summary>
    /// Applies a new capacity in place, trimming only the lowest-priority tail if the queue is
    /// now over the new limit. Callers reacting to a settings change must use this instead of
    /// constructing a replacement queue — swapping the queue object out from under a message
    /// that's currently being spoken (<see cref="CurrentMessage"/>) silently orphans it along
    /// with anything still waiting behind it, with no error and no log entry.
    /// </summary>
    public void SetMaxLength(int maxLength)
    {
        var discarded = new List<EngineerMessage>();
        lock (_gate)
        {
            _maxLength = Math.Max(1, maxLength);
            while (_queue.Count > _maxLength)
            {
                discarded.Add(_queue[_queue.Count - 1]);
                _queue.RemoveAt(_queue.Count - 1);
            }
        }
        foreach (var message in discarded) MessageSuperseded?.Invoke(message);
    }

    public EngineerMessage? CurrentMessage { get; private set; }
    public IReadOnlyList<EngineerMessage> PendingMessages
    {
        get
        {
            lock (_gate)
            {
                return _queue.ToArray();
            }
        }
    }

    public event Action<EngineerMessage>? MessageEnqueued;
    public event Action<EngineerMessage>? MessageInterrupted;
    public event Action<EngineerMessage>? MessageSuperseded;
    public event Action<EngineerMessage>? MessageExpired;

    public bool Enqueue(EngineerMessage message, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        ExpirePending(now);
        var key = string.IsNullOrWhiteSpace(message.DeduplicationKey)
            ? message.EventType.ToString()
            : message.DeduplicationKey;
        EngineerMessage? interrupted = null;
        var superseded = new List<EngineerMessage>();
        var accepted = false;

        lock (_gate)
        {
            if (_cooldowns.TryGetValue(key, out var last) && now - last < message.Cooldown)
            {
                return false;
            }

            if (_queue.Any(m => string.Equals(m.DeduplicationKey, key, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(message.SupersessionKey))
            {
                for (var index = _queue.Count - 1; index >= 0; index--)
                {
                    if (string.Equals(
                        _queue[index].SupersessionKey,
                        message.SupersessionKey,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        superseded.Add(_queue[index]);
                        _queue.RemoveAt(index);
                    }
                }

                if (CurrentMessage != null
                    && string.Equals(
                        CurrentMessage.SupersessionKey,
                        message.SupersessionKey,
                        StringComparison.OrdinalIgnoreCase))
                {
                    interrupted = CurrentMessage;
                    CurrentMessage = null;
                }
            }

            if (interrupted == null
                && CurrentMessage != null
                && message.CanInterruptLowerPriority
                && message.Priority > CurrentMessage.Priority)
            {
                interrupted = CurrentMessage;
                CurrentMessage = null;
            }

            _cooldowns[key] = now;
            _queue.Add(message);
            _queue.Sort((a, b) =>
            {
                var priority = b.Priority.CompareTo(a.Priority);
                return priority != 0 ? priority : a.TimestampUtc.CompareTo(b.TimestampUtc);
            });

            while (_queue.Count > _maxLength)
            {
                superseded.Add(_queue[_queue.Count - 1]);
                _queue.RemoveAt(_queue.Count - 1);
            }
            accepted = _queue.Contains(message);
            if (!accepted) _cooldowns.Remove(key);
        }

        foreach (var item in superseded)
        {
            MessageSuperseded?.Invoke(item);
        }
        if (interrupted != null)
        {
            MessageInterrupted?.Invoke(interrupted);
        }
        if (accepted) MessageEnqueued?.Invoke(message);
        return accepted;
    }

    public bool TryStartNext(out EngineerMessage message, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        ExpirePending(now);
        lock (_gate)
        {
            if (CurrentMessage != null || _queue.Count == 0)
            {
                message = null!;
                return false;
            }

            message = _queue[0];
            _queue.RemoveAt(0);
            CurrentMessage = message;
            return true;
        }
    }

    public void CompleteCurrent(string? expectedMessageId = null)
    {
        lock (_gate)
        {
            if (expectedMessageId == null || CurrentMessage?.Id == expectedMessageId)
                CurrentMessage = null;
        }
    }

    public void Clear()
    {
        EngineerMessage? interrupted;
        EngineerMessage[] removed;
        lock (_gate)
        {
            interrupted = CurrentMessage; removed = _queue.ToArray();
            CurrentMessage = null;
            _queue.Clear();
        }
        if (interrupted != null) MessageInterrupted?.Invoke(interrupted);
        foreach (var message in removed) MessageSuperseded?.Invoke(message);
    }
    public bool IsCurrent(string id) { lock (_gate) return CurrentMessage?.Id == id; }
    public IReadOnlyList<EngineerMessage> RemovePending(Func<EngineerMessage, bool> predicate)
    {
        lock (_gate)
        {
            var removed = _queue.Where(predicate).ToArray();
            _queue.RemoveAll(message => predicate(message));
            return removed;
        }
    }

    private void ExpirePending(DateTime now)
    {
        EngineerMessage[] expired;
        lock (_gate)
        {
            expired = _queue.Where(m => m.IsExpired(now)).ToArray();
            _queue.RemoveAll(m => expired.Contains(m));
        }
        foreach (var message in expired) MessageExpired?.Invoke(message);
    }
}
