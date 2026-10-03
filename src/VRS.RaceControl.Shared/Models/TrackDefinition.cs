namespace VRS.RaceControl.Shared.Models;

public sealed record TrackSplinePoint(double DistanceMeters, double WorldX, double WorldZ);
public sealed record TrackSectorBoundary(int Sector, double StartsAtMeters);
public sealed record TrackSectionDefinition(string Name, double StartsAtMeters, double EndsAtMeters);
public sealed record TrackDefinition(string Simulator, string TrackId, string LayoutId, int Revision,
    double LengthMeters, IReadOnlyList<TrackSplinePoint> Spline,
    IReadOnlyList<TrackSectorBoundary> Sectors, IReadOnlyList<TrackSectionDefinition> Sections,
    string CalibrationSource, bool Verified = false)
{
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Simulator) || Simulator.Length > 100
            || string.IsNullOrWhiteSpace(CalibrationSource) || CalibrationSource.Length > 1000
            || string.IsNullOrWhiteSpace(TrackId) || TrackId.Length > 200 || string.IsNullOrWhiteSpace(LayoutId)
            || LayoutId.Length > 100 || !double.IsFinite(LengthMeters) || LengthMeters is < 200 or > 50000 || Revision < 1)
            return "Invalid track identity or length.";
        if (Spline == null || Spline.Count is < 20 or > 20000 || Sectors == null || Sections == null
            || Sectors.Count > 3 || Sections.Count > 200) return "Invalid track geometry.";
        double previous = -1;
        foreach (var point in Spline)
        {
            if (point == null || !double.IsFinite(point.DistanceMeters) || !double.IsFinite(point.WorldX) || !double.IsFinite(point.WorldZ)
                || point.DistanceMeters <= previous || point.DistanceMeters < 0 || point.DistanceMeters >= LengthMeters)
                return "Spline points must have finite world coordinates and increasing lap distance.";
            previous = point.DistanceMeters;
        }
        if (Spline[0].DistanceMeters > 100 || LengthMeters - Spline[^1].DistanceMeters > 100)
            return "Finish-line geometry is incomplete.";
        for (var i = 1; i < Spline.Count; i++)
            if (Spline[i].DistanceMeters - Spline[i - 1].DistanceMeters > 150) return "Track recording contains a gap.";
        double measured = 0;
        for (var i = 1; i < Spline.Count; i++)
        {
            var step = Math.Sqrt(Math.Pow(Spline[i].WorldX - Spline[i - 1].WorldX, 2) + Math.Pow(Spline[i].WorldZ - Spline[i - 1].WorldZ, 2));
            if (step > 250 || step > 2 * (Spline[i].DistanceMeters - Spline[i - 1].DistanceMeters) + 50)
                return "Track recording contains a teleport or coordinate discontinuity.";
            measured += step;
        }
        if (measured < LengthMeters * .5 || measured > LengthMeters * 1.5) return "World geometry does not match scoring track length.";
        var closure = Math.Sqrt(Math.Pow(Spline[0].WorldX - Spline[^1].WorldX, 2) + Math.Pow(Spline[0].WorldZ - Spline[^1].WorldZ, 2));
        if (closure > 100) return "Track recording does not close at the finish line.";
        if (Sectors.Any(s => s == null || s.Sector is < 1 or > 3 || !double.IsFinite(s.StartsAtMeters) || s.StartsAtMeters < 0 || s.StartsAtMeters >= LengthMeters)
            || Sectors.Select(s => s.Sector).Distinct().Count() != Sectors.Count) return "Invalid sector boundaries.";
        if (Sections.Any(s => s == null || string.IsNullOrWhiteSpace(s.Name) || s.Name.Length > 200 || !double.IsFinite(s.StartsAtMeters) || !double.IsFinite(s.EndsAtMeters)
            || s.StartsAtMeters < 0 || s.EndsAtMeters > LengthMeters || s.StartsAtMeters >= s.EndsAtMeters)) return "Invalid named sections.";
        return null;
    }
}
