using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

/// <summary>
/// Which saved or recorded profile may be drawn for the current telemetry. LMU does not report a layout id and
/// regenerates its game epoch on every session change, so identity is simulator + track name (which already
/// contains the layout, e.g. "Paul Ricard - 1A") + the scoring track length, never an epoch.
/// </summary>
public static class TrackProfileMatchPolicy
{
    public static bool Matches(TrackDefinition definition, TelemetryBatch fleet)
    {
        if (!definition.Simulator.Equals(fleet.Simulator, StringComparison.OrdinalIgnoreCase)
            || !definition.TrackId.Equals(fleet.TrackId, StringComparison.OrdinalIgnoreCase)) return false;
        if (fleet.LayoutId != null && !definition.LayoutId.Equals(fleet.LayoutId, StringComparison.OrdinalIgnoreCase)) return false;
        var lengths = fleet.Cars.Select(car => car.TrackLengthMeters).Where(length => length is > 200)
            .Select(length => length!.Value).ToArray();
        if (lengths.Length == 0) return true;
        var tolerance = Math.Max(20, definition.LengthMeters * 0.01);
        return lengths.Any(length => Math.Abs(length - definition.LengthMeters) <= tolerance);
    }
}

/// <summary>
/// Builds a track profile with no operator action: every car in the fleet gets its own <see cref="TrackLapRecorder"/>,
/// so the first clean lap by any car (pit, gap and stale-data laps are rejected by the recorder) completes the map.
/// A finished lap is then verified against the scoring length and the finish line before it is trusted.
/// </summary>
public sealed class TrackAutoMapper
{
    public const int MaxRecorders = 128;
    private const int MinimumPoints = 100;
    private const double MinimumLengthRatio = 0.95, MaximumLengthRatio = 1.04, MaximumClosureMeters = 60;
    private readonly Dictionary<int, TrackLapRecorder> _recorders = [];
    private string? _trackKey;
    public string Status { get; private set; } = "Auto map: waiting for a full clean lap.";

    public void Reset() { _recorders.Clear(); _trackKey = null; Status = "Auto map: waiting for a full clean lap."; }

    /// <summary>The layout id stored with an automatic profile. LMU puts the layout in the track name.</summary>
    public static string LayoutIdFor(TelemetryBatch batch) =>
        batch.LayoutId ?? (batch.TrackId.Length <= 100 ? batch.TrackId : batch.TrackId[..100]);

    /// <summary>Returns a verified profile once any car completes an acceptable lap, otherwise null.</summary>
    public TrackDefinition? Observe(TelemetryBatch batch, DateTimeOffset? observedNow = null)
    {
        var key = $"{batch.Simulator}:{batch.TrackId}:{batch.LayoutId}";
        if (_trackKey != key) { _recorders.Clear(); _trackKey = key; Status = "Auto map: waiting for a full clean lap."; }
        var layout = LayoutIdFor(batch);
        string? progress = null;
        foreach (var car in batch.Cars)
        {
            if (!_recorders.TryGetValue(car.VehicleId, out var recorder))
            {
                if (_recorders.Count >= MaxRecorders) continue;
                _recorders[car.VehicleId] = recorder = new TrackLapRecorder(car.VehicleId, layout);
            }
            recorder.Observe(batch, observedNow);
            if (recorder.Completed is { } lap)
            {
                if (Verify(lap) is { } reason)
                {
                    Status = $"Auto map: lap by {car.DriverName} rejected ({reason}); waiting for another clean lap.";
                    _recorders[car.VehicleId] = new TrackLapRecorder(car.VehicleId, layout);
                    continue;
                }
                Status = $"Auto map: recorded from {car.DriverName}'s lap ({lap.Spline.Count} points, {lap.LengthMeters:0} m).";
                return lap with { Verified = true, CalibrationSource = "auto-lmu; " + lap.CalibrationSource };
            }
            if (progress == null && recorder.Status.StartsWith("Recording lap", StringComparison.Ordinal))
                progress = $"Auto map: {car.DriverName} - {recorder.Status}";
        }
        if (progress != null) Status = progress;
        else if (Status.StartsWith("Auto map: ", StringComparison.Ordinal) && !Status.Contains("rejected", StringComparison.Ordinal))
            Status = "Auto map: waiting for a full clean lap.";
        return null;
    }

    /// <summary>Extra checks beyond <see cref="TrackDefinition.Validate"/>: enough points, and a path that matches the scoring length and closes.</summary>
    public static string? Verify(TrackDefinition lap)
    {
        if (lap.Validate() is { } error) return error;
        if (lap.Spline.Count < MinimumPoints) return $"only {lap.Spline.Count} points";
        double path = 0;
        for (var i = 1; i < lap.Spline.Count; i++)
            path += Math.Sqrt(Math.Pow(lap.Spline[i].WorldX - lap.Spline[i - 1].WorldX, 2) + Math.Pow(lap.Spline[i].WorldZ - lap.Spline[i - 1].WorldZ, 2));
        var ratio = path / lap.LengthMeters;
        if (ratio < MinimumLengthRatio || ratio > MaximumLengthRatio) return $"path is {ratio:P0} of the scoring length";
        var first = lap.Spline[0]; var last = lap.Spline[^1];
        var closure = Math.Sqrt(Math.Pow(first.WorldX - last.WorldX, 2) + Math.Pow(first.WorldZ - last.WorldZ, 2));
        return closure > MaximumClosureMeters ? $"lap does not close ({closure:0} m)" : null;
    }
}
