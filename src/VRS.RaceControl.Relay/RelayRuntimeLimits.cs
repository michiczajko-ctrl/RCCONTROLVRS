using System.Buffers;
using System.Globalization;
using System.Text;

public sealed record RelayLimits(
    int MaxSessions,
    int MaxClientsPerSession,
    int MaxConnections,
    int MaxMessageBytes,
    int MemoryRejectMb,
    int MemoryHealthMb)
{
    public const int DefaultMaxSessions = 16;
    public const int DefaultMaxClientsPerSession = 64;
    public const int DefaultMaxConnections = 192;
    public const int DefaultMaxMessageBytes = 256 * 1024;
    public const int DefaultMemoryRejectMb = 440;
    public const int DefaultMemoryHealthMb = 480;
    public const int DeduplicationCapacityPerSession = 1024;

    private const long BytesPerMebibyte = 1024L * 1024L;

    public long MemoryRejectBytes => MemoryRejectMb * BytesPerMebibyte;
    public long MemoryHealthBytes => MemoryHealthMb * BytesPerMebibyte;

    public static RelayLimits FromEnvironment() =>
        FromValues(Environment.GetEnvironmentVariable);

    public static RelayLimits FromValues(Func<string, string?> readValue)
    {
        ArgumentNullException.ThrowIfNull(readValue);

        var maxSessions = ReadBoundedInt(
            readValue,
            "VRS_MAX_SESSIONS",
            DefaultMaxSessions,
            minimum: 1,
            maximum: 64);
        var maxClientsPerSession = ReadBoundedInt(
            readValue,
            "VRS_MAX_CLIENTS_PER_SESSION",
            DefaultMaxClientsPerSession,
            minimum: 2,
            maximum: 128);
        var maxConnections = ReadBoundedInt(
            readValue,
            "VRS_MAX_CONNECTIONS",
            DefaultMaxConnections,
            minimum: 8,
            maximum: 256);
        maxConnections = Math.Max(maxConnections, maxClientsPerSession);

        var maxMessageBytes = ReadBoundedInt(
            readValue,
            "VRS_MAX_MESSAGE_BYTES",
            DefaultMaxMessageBytes,
            minimum: 16 * 1024,
            maximum: 1024 * 1024);
        var memoryRejectMb = ReadBoundedInt(
            readValue,
            "VRS_MEMORY_REJECT_MB",
            DefaultMemoryRejectMb,
            minimum: 256,
            maximum: 470);
        var memoryHealthMb = ReadBoundedInt(
            readValue,
            "VRS_MEMORY_HEALTH_MB",
            DefaultMemoryHealthMb,
            minimum: memoryRejectMb + 16,
            maximum: 496);

        return new RelayLimits(
            maxSessions,
            maxClientsPerSession,
            maxConnections,
            maxMessageBytes,
            memoryRejectMb,
            memoryHealthMb);
    }

    private static int ReadBoundedInt(
        Func<string, string?> readValue,
        string name,
        int defaultValue,
        int minimum,
        int maximum)
    {
        var raw = readValue(name);
        var parsed = int.TryParse(
            raw,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : defaultValue;
        return Math.Clamp(parsed, minimum, maximum);
    }
}

public static class RelayMemoryPolicy
{
    public static bool CanAcceptConnection(long workingSetBytes, long rejectAtBytes) =>
        workingSetBytes >= 0
        && rejectAtBytes > 0
        && workingSetBytes < rejectAtBytes;

    public static bool IsHealthy(long workingSetBytes, long unhealthyAtBytes) =>
        workingSetBytes >= 0
        && unhealthyAtBytes > 0
        && workingSetBytes < unhealthyAtBytes;
}

public sealed class RelayCapacityGate
{
    private readonly int _maximum;
    private int _active;

    public RelayCapacityGate(int maximum)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, 1);
        _maximum = maximum;
    }

    public int ActiveCount => Volatile.Read(ref _active);

    public bool TryAcquire()
    {
        while (true)
        {
            var current = Volatile.Read(ref _active);
            if (current >= _maximum)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _active, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    public void Release()
    {
        var remaining = Interlocked.Decrement(ref _active);
        if (remaining < 0)
        {
            Interlocked.Exchange(ref _active, 0);
            throw new InvalidOperationException("Relay connection gate was released too many times.");
        }
    }
}

public sealed class RelayMessageAccumulator
{
    private readonly ArrayBufferWriter<byte> _buffer;
    private readonly int _maximumBytes;

    public RelayMessageAccumulator(int maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
        _maximumBytes = maximumBytes;
        _buffer = new ArrayBufferWriter<byte>(Math.Min(maximumBytes, 8192));
    }

    public int Length => _buffer.WrittenCount;

    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > _maximumBytes - _buffer.WrittenCount)
        {
            throw new InvalidDataException(
                $"Message exceeds the {_maximumBytes}-byte relay limit.");
        }

        bytes.CopyTo(_buffer.GetSpan(bytes.Length));
        _buffer.Advance(bytes.Length);
    }

    public string ToUtf8String() => Encoding.UTF8.GetString(_buffer.WrittenSpan);
}
