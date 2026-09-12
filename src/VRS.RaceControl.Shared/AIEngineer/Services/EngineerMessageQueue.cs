using VRS.RaceControl.Shared.AIEngineer.Models;

namespace VRS.RaceControl.Shared.AIEngineer.Services;

public sealed class EngineerMessageQueue
{
    private readonly List<EngineerMessage> _queue = new();
    private readonly Dictionary<string, DateTime> _cooldowns = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly int _maxLength;

    public EngineerMessageQueue(int maxLength = 16)
    {
        _maxLength = Math.Max(1, maxLength);
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

    public bool Enqueue(EngineerMessage message, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var key = string.IsNullOrWhiteSpace(message.DeduplicationKey)
            ? message.EventType.ToString()
            : message.DeduplicationKey;
        EngineerMessage? interrupted = null;
        var superseded = new List<EngineerMessage>();

        lock (_gate)
        {
            RemoveExpiredLocked(now);

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
                _queue.RemoveAt(_queue.Count - 1);
            }
        }

        foreach (var item in superseded)
        {
            MessageSuperseded?.Invoke(item);
        }
        if (interrupted != null)
        {
            MessageInterrupted?.Invoke(interrupted);
        }
        MessageEnqueued?.Invoke(message);
        return true;
    }

    public bool TryStartNext(out EngineerMessage message, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        lock (_gate)
        {
            RemoveExpiredLocked(now);
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

    public void CompleteCurrent()
    {
        lock (_gate)
        {
            CurrentMessage = null;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            CurrentMessage = null;
            _queue.Clear();
        }
    }

    private void RemoveExpiredLocked(DateTime now)
    {
        _queue.RemoveAll(m => m.IsExpired(now));
    }
}
