using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

/// <summary>A point on the track in world coordinates, with the unit direction of travel there.</summary>
public readonly record struct TrackPoint(double WorldX, double WorldZ, double DirectionX, double DirectionZ);

public static class TrackStartFinish
{
    /// <summary>
    /// A recording starts when the car crosses the line, so the first spline point lies a few metres (never more than
    /// 100) past it. The line is that distance back along the first segment. Null when the geometry cannot say.
    /// </summary>
    public static TrackPoint? Locate(TrackDefinition definition)
    {
        var spline = definition.Spline;
        if (spline == null || spline.Count < 2) return null;
        var first = spline[0]; var second = spline[1];
        if (!Direction(first, second, out var dx, out var dz)) return null;
        var back = Math.Clamp(first.DistanceMeters, 0, 100);
        return new(first.WorldX - dx * back, first.WorldZ - dz * back, dx, dz);
    }

    /// <summary>
    /// The point <paramref name="meters"/> along the lap (interpolated between recorded points, clamped to the recorded
    /// range) and the direction of travel there. Used for sector boundaries. Null when the geometry cannot say.
    /// </summary>
    public static TrackPoint? At(TrackDefinition definition, double meters)
    {
        var spline = definition.Spline;
        if (spline == null || spline.Count < 2 || !double.IsFinite(meters)) return null;
        meters = Math.Clamp(meters, spline[0].DistanceMeters, spline[^1].DistanceMeters);
        var index = 1;
        while (index < spline.Count - 1 && spline[index].DistanceMeters < meters) index++;
        var from = spline[index - 1]; var to = spline[index];
        if (!Direction(from, to, out var dx, out var dz)) return null;
        var span = to.DistanceMeters - from.DistanceMeters;
        var fraction = span > 0 ? Math.Clamp((meters - from.DistanceMeters) / span, 0, 1) : 0;
        return new(from.WorldX + (to.WorldX - from.WorldX) * fraction, from.WorldZ + (to.WorldZ - from.WorldZ) * fraction, dx, dz);
    }

    private static bool Direction(TrackSplinePoint from, TrackSplinePoint to, out double dx, out double dz)
    {
        dx = to.WorldX - from.WorldX; dz = to.WorldZ - from.WorldZ;
        var length = Math.Sqrt(dx * dx + dz * dz);
        if (!(length > .01) || !double.IsFinite(length)) return false;
        dx /= length; dz /= length;
        return true;
    }
}
