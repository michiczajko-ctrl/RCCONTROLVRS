using System.Text.Json;
using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

/// <summary>
/// Learns the pit lane from a car driving through it. LMU gives no pit-lane geometry, but every car reports its position and whether
/// it is in the pit lane, so the path of one car from pit entry to pit exit is the pit lane. A pass counts when the car was moving,
/// the path is a plausible length and both ends join the racing line.
/// </summary>
public sealed class PitLaneRecorder
{
    private const double MinSpeedKmh = 8, MinPointSpacing = 4, MaxJump = 120, MinLength = 120, MaxLength = 1500, MaxEndDistance = 70;
    private const int MinPoints = 12, MaxStoredPoints = 60;
    private sealed class Run { public List<(double X, double Z)> Points { get; } = []; }
    private readonly Dictionary<int, Run> _runs = [];
    private string? _epoch;
    public string Status { get; private set; } = "Pit lane: waiting for a car to drive through it.";

    /// <summary>Returns the pit lane when a car has just finished a valid pass through it, otherwise null.</summary>
    public IReadOnlyList<TrackSplinePoint>? Observe(TelemetryBatch batch, TrackDefinition map)
    {
        var epoch = $"{batch.GameEpoch}:{batch.SourceEpoch}";
        if (_epoch != null && _epoch != epoch) _runs.Clear();
        _epoch = epoch;
        IReadOnlyList<TrackSplinePoint>? found = null;
        foreach (var gone in _runs.Keys.Except(batch.Cars.Select(car => car.VehicleId)).ToArray())
        { found ??= Finish(gone, map); }
        foreach (var car in batch.Cars)
        {
            var x = car.Position.X; var z = car.Position.Z;
            if (!double.IsFinite(x) || !double.IsFinite(z)) continue;
            if (car.InPitLane == true)
            {
                if (!(car.SpeedKmh >= MinSpeedKmh)) continue;   // in the box or queueing: not part of the lane's line
                if (!_runs.TryGetValue(car.VehicleId, out var run)) _runs[car.VehicleId] = run = new();
                if (run.Points.Count > 0)
                {
                    var last = run.Points[^1]; var gap = Math.Sqrt((x - last.X) * (x - last.X) + (z - last.Z) * (z - last.Z));
                    if (gap > MaxJump) { found ??= Finish(car.VehicleId, map); _runs[car.VehicleId] = run = new(); }
                    else if (gap < MinPointSpacing) continue;
                }
                run.Points.Add((x, z));
            }
            else if (car.InPitLane == false && _runs.ContainsKey(car.VehicleId)) found ??= Finish(car.VehicleId, map);
        }
        return found;
    }

    private IReadOnlyList<TrackSplinePoint>? Finish(int vehicleId, TrackDefinition map)
    {
        if (!_runs.Remove(vehicleId, out var run)) return null;
        var points = run.Points;
        if (points.Count < MinPoints) { Status = "Pit lane: that pass was too short."; return null; }
        double length = 0;
        for (var i = 1; i < points.Count; i++) length += Distance(points[i - 1], points[i]);
        if (length is < MinLength or > MaxLength) { Status = "Pit lane: that pass was not a plausible pit lane length."; return null; }
        if (DistanceToTrack(map, points[0]) > MaxEndDistance || DistanceToTrack(map, points[^1]) > MaxEndDistance)
        { Status = "Pit lane: that pass did not start and end at the track."; return null; }
        var simplified = Simplify(points);
        var result = new List<TrackSplinePoint>(); double along = 0;
        for (var i = 0; i < simplified.Count; i++)
        {
            if (i > 0) along += Distance(simplified[i - 1], simplified[i]);
            result.Add(new TrackSplinePoint(along, simplified[i].X, simplified[i].Z));
        }
        Status = $"Pit lane learned: {along:0} m.";
        return result;
    }

    private static double Distance((double X, double Z) a, (double X, double Z) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    /// <summary>Distance from a point to the racing line, in metres.</summary>
    private static double DistanceToTrack(TrackDefinition map, (double X, double Z) point)
    {
        var spline = map.Spline; var best = double.MaxValue;
        for (var i = 0; i < spline.Count; i++)
        {
            var a = spline[i]; var b = spline[(i + 1) % spline.Count];
            var dx = b.WorldX - a.WorldX; var dz = b.WorldZ - a.WorldZ; var lengthSquared = dx * dx + dz * dz;
            var t = lengthSquared <= 0 ? 0 : Math.Clamp(((point.X - a.WorldX) * dx + (point.Z - a.WorldZ) * dz) / lengthSquared, 0, 1);
            var px = a.WorldX + dx * t; var pz = a.WorldZ + dz * t;
            best = Math.Min(best, Math.Sqrt((point.X - px) * (point.X - px) + (point.Z - pz) * (point.Z - pz)));
        }
        return best;
    }

    /// <summary>Douglas-Peucker: fewest points that keep the line within a metre or two, and never more than the stored limit.</summary>
    private static List<(double X, double Z)> Simplify(List<(double X, double Z)> points)
    {
        for (var tolerance = 1.5; ; tolerance *= 1.5)
        {
            var keep = new bool[points.Count]; keep[0] = keep[^1] = true;
            var stack = new Stack<(int From, int To)>(); stack.Push((0, points.Count - 1));
            while (stack.Count > 0)
            {
                var (from, to) = stack.Pop();
                var worst = -1; var worstDistance = tolerance;
                for (var i = from + 1; i < to; i++)
                {
                    var d = PerpendicularDistance(points[i], points[from], points[to]);
                    if (d > worstDistance) { worstDistance = d; worst = i; }
                }
                if (worst >= 0) { keep[worst] = true; stack.Push((from, worst)); stack.Push((worst, to)); }
            }
            var result = points.Where((_, i) => keep[i]).ToList();
            if (result.Count <= MaxStoredPoints) return result;
        }
    }

    private static double PerpendicularDistance((double X, double Z) point, (double X, double Z) a, (double X, double Z) b)
    {
        var dx = b.X - a.X; var dz = b.Z - a.Z; var length = Math.Sqrt(dx * dx + dz * dz);
        return length <= 0 ? Distance(point, a) : Math.Abs(dz * point.X - dx * point.Z + b.X * a.Z - b.Z * a.X) / length;
    }
}

/// <summary>Saves the learned pit lane next to the track maps, one file per simulator and track. The optional directory is for tests.</summary>
public static class PitLaneStore
{
    private static string FilePath(string simulator, string track, string? directory) =>
        Path.Combine(directory ?? TrackDefinitionStore.DirectoryPath, "pit-" + TrackDefinitionStore.Key(simulator, track, "PIT-LANE") + ".pit");

    public static IReadOnlyList<TrackSplinePoint>? Load(string simulator, string track, string? directory = null)
    {
        try
        {
            var path = FilePath(simulator, track, directory);
            if (!File.Exists(path)) return null;
            var points = JsonSerializer.Deserialize<List<TrackSplinePoint>>(File.ReadAllText(path));
            return points is { Count: >= 2 and <= 200 } && points.All(p => double.IsFinite(p.WorldX) && double.IsFinite(p.WorldZ)) ? points : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public static bool Save(string simulator, string track, IReadOnlyList<TrackSplinePoint> points, string? directory = null)
    {
        try
        {
            var path = FilePath(simulator, track, directory);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(points));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public static void Delete(string simulator, string track, string? directory = null)
    {
        var path = FilePath(simulator, track, directory);
        if (File.Exists(path)) File.Delete(path);
    }
}
