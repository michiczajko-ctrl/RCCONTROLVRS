using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

/// <summary>Records a real clean lap. Never invents geometry when data is missing.</summary>
public sealed class TrackLapRecorder(int vehicleId, string confirmedLayoutId)
{
    private readonly List<TrackSplinePoint> _points = [];
    private readonly Dictionary<int, double> _sectors = [];
    private string? _sourceEpoch;
    private int? _previousLap, _recordedLap;
    private double _length;
    private long _sequence;
    public string Status { get; private set; } = "Waiting for a clean lap crossing the finish line.";
    public TrackDefinition? Completed { get; private set; }
    public void Observe(TelemetryBatch batch, DateTimeOffset? observedNow = null)
    {
        if (Completed != null) return;
        var epoch = $"{batch.GameEpoch}:{batch.SourceId}:{batch.SourceEpoch}";
        var car = batch.Cars.FirstOrDefault(c => c.VehicleId == vehicleId);
        if (_sourceEpoch != null && _sourceEpoch != epoch) Reset("Telemetry session/source changed; waiting for a new lap.");
        _sourceEpoch = epoch;
        if (batch.Sequence <= _sequence) return;
        _sequence = batch.Sequence;
        var age = ((observedNow ?? DateTimeOffset.UtcNow) - batch.CapturedAt).TotalSeconds;
        var fresh = (batch.FreshnessVerified && age is >= -.1 and <= .75) || (batch.CaptureMonotonicTimestamp > 0
            && System.Diagnostics.Stopwatch.GetElapsedTime(batch.CaptureMonotonicTimestamp).TotalSeconds < .75);
        if (!fresh) { Reset("Telemetry freshness is unverified."); return; }
        if (batch.HasGap || car == null || car.InPitLane != false || car.LapDistanceMeters is not { } distance
            || car.TrackLengthMeters is not > 200 || !car.IsValid)
        { Reset("Telemetry gap or pit lane; waiting for a clean lap."); return; }
        if (_recordedLap != null && Math.Abs(_length - car.TrackLengthMeters.Value) > 5)
        { Reset("Track length changed; waiting for a new lap."); return; }
        if (_recordedLap != null && car.Lap != _recordedLap && car.Lap != _recordedLap + 1)
        { Reset("Lap changed unexpectedly."); _previousLap = car.Lap; return; }
        if (_recordedLap != null && car.Lap == _recordedLap + 1 && distance < 100)
        {
            var candidate = new TrackDefinition(batch.Simulator, batch.TrackId, confirmedLayoutId, 1, _length,
                _points.ToArray(), _sectors.Select(s => new TrackSectorBoundary(s.Key, s.Value)).OrderBy(s => s.StartsAtMeters).ToArray(), [],
                $"LMU telemetry vehicle {vehicleId}; game {batch.GameEpoch}; source {batch.SourceEpoch}");
            if (candidate.Validate() is { } error) { Reset(error); _previousLap = car.Lap; return; }
            Completed = candidate; Status = "Lap recorded. Press SAVE MAP to keep it."; return;
        }
        if (_recordedLap == null && _previousLap != null && car.Lap == _previousLap + 1 && distance < 100)
        { _recordedLap = car.Lap; _length = car.TrackLengthMeters.Value; _sectors[1] = 0; }
        _previousLap = car.Lap;
        if (_recordedLap != car.Lap) return;
        if (_points.Count > 0 && distance <= _points[^1].DistanceMeters) { Reset("Lap distance went backwards."); return; }
        if (_points.Count >= 20000) { Reset("Track recording limit reached."); return; }
        _points.Add(new(distance, car.Position.X, car.Position.Z));
        if (car.Sector is { } sector && !_sectors.ContainsKey(sector)) _sectors[sector] = distance;
        Status = $"Recording lap {car.Lap}: {distance / _length:P0} ({_points.Count} points).";
    }
    private void Reset(string reason) { _points.Clear(); _sectors.Clear(); _recordedLap = null; _previousLap = null; _sequence = 0; Status = reason; }
}
