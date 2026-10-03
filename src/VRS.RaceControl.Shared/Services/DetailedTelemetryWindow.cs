using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

/// <summary>At most six new source samples per 5 Hz publication. Never replays a prior game epoch.</summary>
public sealed class DetailedTelemetryWindow
{
    private readonly Queue<TelemetryEvidenceSample> _samples = new();
    private string? _epoch;
    private long _sequence;
    private DateTimeOffset? _lastAt;
    private bool _gap;
    public void Add(string epoch, DateTimeOffset at, long captureStamp, IReadOnlyList<TelemetryCar> cars)
    {
        if (_epoch != epoch) { _samples.Clear(); _sequence = 0; _lastAt = null; _gap = false; _epoch = epoch; }
        if (_lastAt is { } previous && at <= previous) { _gap = true; return; }
        if (_lastAt is { } prior && at - prior > TimeSpan.FromMilliseconds(150)) _gap = true;
        _lastAt = at;
        _samples.Enqueue(new(at, ++_sequence, cars.Select(Clone).ToArray()) { CaptureMonotonicTimestamp = captureStamp });
        while (_samples.Count > 6) { _samples.Dequeue(); _gap = true; }
    }
    public (IReadOnlyList<TelemetryEvidenceSample> Samples, bool HasGap) Drain(DateTimeOffset latestAt)
    {
        while (_samples.TryPeek(out var old) && latestAt - old.CapturedAt > TimeSpan.FromMilliseconds(400))
        { _samples.Dequeue(); _gap = true; }
        var result = (_samples.ToArray(), _gap); _samples.Clear(); _gap = false; return result;
    }
    internal static TelemetryCar Clone(TelemetryCar car) => car with
    {
        Position = new(car.Position.X, car.Position.Y, car.Position.Z),
        WorldVelocity = car.WorldVelocity is { } v ? new(v.X, v.Y, v.Z) : null,
        ImpactPosition = car.ImpactPosition is { } p ? new(p.X, p.Y, p.Z) : null,
        Damage = car.Damage?.ToArray()
    };
}
