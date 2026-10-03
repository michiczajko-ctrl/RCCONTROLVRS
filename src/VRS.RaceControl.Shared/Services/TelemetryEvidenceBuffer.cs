using System.Security.Cryptography;
using System.Text.Json;
using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

/// <summary>Bounded 30-second fleet history; clips contain only the selected cars.</summary>
public sealed class TelemetryEvidenceBuffer
{
    private readonly object _gate = new();
    private readonly Queue<TelemetryBatch> _frames = new();
    private readonly Dictionary<string, PendingClip> _pending = new(StringComparer.Ordinal);
    private string? _epoch;
    private long _sequence;
    private int _retainedCars, _retainedDamageValues;
    private sealed record PendingClip(string Id, string SessionId, string GameEpoch, string SourceEpoch,
        DateTimeOffset At, int[] Vehicles, List<TelemetryEvidenceSample> Samples, HashSet<string> Reasons,
        TrackDefinitionReference? TrackDefinition);

    public TelemetryEvidenceClip Begin(string incidentId, TelemetryBatch source, DateTimeOffset at, params int[] vehicles)
        => Begin(incidentId, source, at, null, vehicles);
    public TelemetryEvidenceClip Begin(string incidentId, TelemetryBatch source, DateTimeOffset at,
        TrackDefinitionReference? track, params int[] vehicles)
    {
        if (string.IsNullOrWhiteSpace(incidentId) || incidentId.Length > 80 || vehicles.Length is < 1 or > 8)
            throw new ArgumentException("Invalid evidence identity or participants.");
        lock (_gate)
        {
            if (_pending.TryGetValue(incidentId, out var existing)) return Build(existing, "collecting");
            if (_pending.Count >= 128) throw new InvalidOperationException("Too many evidence clips are collecting.");
            var pending = new PendingClip(incidentId, source.ControlSessionId, source.GameEpoch, source.SourceEpoch,
                at, vehicles.Distinct().ToArray(), [], [], track);
            foreach (var frame in _frames.Where(f => f.GameEpoch == source.GameEpoch && f.SourceEpoch == source.SourceEpoch)) AddSample(pending, frame);
            if (pending.Samples.Count == 0 || pending.Samples[0].CapturedAt > at.AddSeconds(-4.7))
                pending.Reasons.Add("pre-incident history incomplete");
            _pending[incidentId] = pending;
            return Build(pending, "collecting");
        }
    }

    public IReadOnlyList<TelemetryEvidenceClip> Add(TelemetryBatch batch)
    {
        lock (_gate)
        {
            var epoch = $"{batch.ControlSessionId}:{batch.GameEpoch}:{batch.SourceId}:{batch.SourceEpoch}:{batch.ClockEpoch}";
            var completed = new List<TelemetryEvidenceClip>();
            if (!batch.FreshnessVerified || batch.Validate() != null) return Interrupt("unverified telemetry");
            if (_epoch != epoch)
            {
                completed.AddRange(Interrupt("telemetry source changed"));
                _frames.Clear(); _retainedCars = _retainedDamageValues = 0; _sequence = 0; _epoch = epoch;
            }
            if (batch.Sequence <= _sequence) return completed;
            if (batch.HasGap || (_sequence > 0 && batch.Sequence != _sequence + 1))
                foreach (var pending in _pending.Values) pending.Reasons.Add("packet gap");
            _sequence = batch.Sequence;
            batch = batch with { Cars = batch.Cars.Select(CloneCar).ToArray(),
                DetailedSamples = batch.DetailedSamples?.Select(sample => sample with { Cars = sample.Cars.Select(CloneCar).ToArray() }).ToArray() };
            _frames.Enqueue(batch);
            var cost = Cost(batch); _retainedCars += cost.Cars; _retainedDamageValues += cost.Damage;
            while (_frames.Count > 160 || _retainedCars > 32_000 || _retainedDamageValues > 500_000
                || (_frames.Count > 0 && batch.CapturedAt - _frames.Peek().CapturedAt > TimeSpan.FromSeconds(30)))
            {
                var removed = Cost(_frames.Dequeue()); _retainedCars -= removed.Cars; _retainedDamageValues -= removed.Damage;
            }
            foreach (var pending in _pending.Values.ToArray())
            {
                AddSample(pending, batch);
                if (batch.CapturedAt >= pending.At.AddSeconds(2))
                {
                    if (pending.Samples.Count == 0 || pending.Samples[^1].CapturedAt < pending.At.AddSeconds(1.7))
                        pending.Reasons.Add("post-incident history incomplete");
                    completed.Add(Build(pending, pending.Reasons.Count == 0 ? "complete" : "partial"));
                    _pending.Remove(pending.Id);
                }
            }
            return completed;
        }
    }

    private static (int Cars, int Damage) Cost(TelemetryBatch frame)
    {
        var cars = frame.Cars.Concat(frame.DetailedSamples?.SelectMany(sample => sample.Cars) ?? []);
        return (frame.Cars.Count + (frame.DetailedSamples?.Sum(sample => sample.Cars.Count) ?? 0),
            cars.Sum(car => car.Damage?.Count ?? 0));
    }

    public IReadOnlyList<TelemetryEvidenceClip> Interrupt(string reason)
    {
        lock (_gate)
        {
            var completed = _pending.Values.Select(p => { p.Reasons.Add(reason); return Build(p, "partial"); }).ToArray();
            _pending.Clear();
            return completed;
        }
    }

    private static void AddSample(PendingClip pending, TelemetryBatch batch)
    {
        if (batch.DetailedSamplesHaveGap) pending.Reasons.Add("detailed source sample gap");
        var samples = batch.DetailedSamples is { Count: > 0 } detail ? detail
            : new[] { new TelemetryEvidenceSample(batch.CapturedAt, batch.Sequence, batch.Cars) };
        if (batch.DetailedSamples is { Count: 0 }) pending.Reasons.Add("detailed samples unavailable; fleet fallback");
        foreach (var sample in samples)
        {
            if (sample.CapturedAt < pending.At.AddSeconds(-5) || sample.CapturedAt > pending.At.AddSeconds(2)) continue;
            if (pending.Samples.Count > 0 && sample.CapturedAt <= pending.Samples[^1].CapturedAt) continue;
            if (pending.Samples.Count >= 160) { pending.Reasons.Add("evidence sample limit reached"); return; }
            if (pending.Samples.Count > 0 && (sample.CapturedAt - pending.Samples[^1].CapturedAt).TotalSeconds > .4)
                pending.Reasons.Add("sample gap");
            var cars = sample.Cars.Where(c => pending.Vehicles.Contains(c.VehicleId)).ToArray();
            if (cars.Length != pending.Vehicles.Length) pending.Reasons.Add("vehicle data unavailable");
            pending.Samples.Add(new(sample.CapturedAt, sample.Sequence, cars));
        }
    }

    private static TelemetryCar CloneCar(TelemetryCar car) => car with
    {
        Position = new(car.Position.X, car.Position.Y, car.Position.Z),
        WorldVelocity = car.WorldVelocity is { } velocity ? new(velocity.X, velocity.Y, velocity.Z) : null,
        ImpactPosition = car.ImpactPosition is { } impact ? new(impact.X, impact.Y, impact.Z) : null,
        Damage = car.Damage?.ToArray()
    };

    private static TelemetryEvidenceClip Build(PendingClip pending, string state)
    {
        var samples = pending.Samples.Select(s => s with { Cars = s.Cars.Select(CloneCar).ToArray() }).ToArray();
        var checksum = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(samples, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        return new(new(pending.Id, pending.SessionId, pending.Id, pending.GameEpoch, pending.SourceEpoch, pending.At,
            pending.At.AddSeconds(-5), pending.At.AddSeconds(2), state, samples.Length, checksum, pending.Reasons.Order().ToArray(), pending.TrackDefinition), samples);
    }
}
