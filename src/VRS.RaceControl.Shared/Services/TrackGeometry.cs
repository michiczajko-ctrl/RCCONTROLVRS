using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

public sealed class TrackGeometry
{
    public TrackDefinition Definition { get; }
    public double MinX { get; } public double MinZ { get; } public double Width { get; } public double Height { get; }
    public TrackGeometry(TrackDefinition definition)
    {
        if (definition.Validate() is { } error) throw new ArgumentException(error, nameof(definition));
        Definition = definition;
        MinX = definition.Spline.Min(p => p.WorldX); MinZ = definition.Spline.Min(p => p.WorldZ);
        Width = Math.Max(1, definition.Spline.Max(p => p.WorldX) - MinX); Height = Math.Max(1, definition.Spline.Max(p => p.WorldZ) - MinZ);
    }
    public (double X, double Y) Project(double worldX, double worldZ, double width, double height)
    {
        var scale = Math.Max(.001, Math.Min(Math.Max(1, width - 40) / Width, Math.Max(1, height - 40) / Height));
        return ((worldX - MinX) * scale + (width - Width * scale) / 2,
            (worldZ - MinZ) * scale + (height - Height * scale) / 2);
    }
    public TrackSplinePoint AtDistance(double distance)
    {
        if (!double.IsFinite(distance)) throw new ArgumentException("Invalid distance.", nameof(distance));
        distance = ((distance % Definition.LengthMeters) + Definition.LengthMeters) % Definition.LengthMeters;
        var points = Definition.Spline;
        var index = 0;
        while (index + 1 < points.Count && points[index + 1].DistanceMeters <= distance) index++;
        var first = points[index]; var second = index + 1 < points.Count ? points[index + 1] : points[0] with { DistanceMeters = points[0].DistanceMeters + Definition.LengthMeters };
        if (distance < points[0].DistanceMeters)
        { first = points[^1] with { DistanceMeters = points[^1].DistanceMeters - Definition.LengthMeters }; second = points[0]; }
        var ratio = Math.Clamp((distance - first.DistanceMeters) / (second.DistanceMeters - first.DistanceMeters), 0, 1);
        return new(distance, first.WorldX + (second.WorldX - first.WorldX) * ratio, first.WorldZ + (second.WorldZ - first.WorldZ) * ratio);
    }
    public int? Sector(double distance) => Definition.Sectors.Where(s => s.StartsAtMeters <= distance)
        .OrderByDescending(s => s.StartsAtMeters).Select(s => (int?)s.Sector).FirstOrDefault();
}
