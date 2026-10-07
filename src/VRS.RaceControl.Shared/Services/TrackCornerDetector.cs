using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

/// <summary>
/// Finds the corners of a recorded track from its shape and names them "Turn 1", "Turn 2"... counted from the start/finish line.
/// LMU does not give corner names or numbers, so these are the app's own count: a long complex of several bends is one turn, and
/// the numbers are not the official ones. They only have to be stable for the same map, so reports and stewards can point at them.
/// </summary>
public static class TrackCornerDetector
{
    private const double StepMeters = 10, RadiusLimitMeters = 400, MergeGapMeters = 40, MarginMeters = 5, MinTurnDegrees = 25;

    /// <summary>The map with its corners added as named sections. A map that already has sections is returned unchanged.</summary>
    public static TrackDefinition WithTurns(TrackDefinition map)
    {
        if (map.Sections.Count > 0) return map;
        try
        {
            var enriched = map with { Sections = Detect(map) };
            return enriched.Validate() == null ? enriched : map;
        }
        catch (ArgumentException) { return map; }
    }

    public static IReadOnlyList<TrackSectionDefinition> Detect(TrackDefinition map)
    {
        var length = map.LengthMeters;
        var n = (int)(length / StepMeters);
        if (n < 20) return [];
        var geometry = new TrackGeometry(map);
        var points = Enumerable.Range(0, n).Select(i => geometry.AtDistance(i * StepMeters)).ToArray();
        double Heading(int i)
        {
            var from = points[((i - 2) % n + n) % n]; var to = points[(i + 2) % n];
            return Math.Atan2(to.WorldZ - from.WorldZ, to.WorldX - from.WorldX);
        }
        var heading = Enumerable.Range(0, n).Select(Heading).ToArray();
        static double Wrap(double angle)
        {
            while (angle > Math.PI) angle -= 2 * Math.PI;
            while (angle < -Math.PI) angle += 2 * Math.PI;
            return angle;
        }
        // Curvature in radians per metre; positive and negative are the two turning directions.
        var curvature = Enumerable.Range(0, n).Select(i => Wrap(heading[(i + 1) % n] - heading[((i - 1) % n + n) % n]) / (2 * StepMeters)).ToArray();
        var tight = curvature.Select(value => Math.Abs(value) > 1 / RadiusLimitMeters).ToArray();
        var first = Array.IndexOf(tight, false);
        if (first < 0) return [];

        // Walk once around the lap starting on a straight, so no corner is cut by the start of the walk.
        var regions = new List<(double Start, double End, double Angle)>();
        (double Start, double End, double Angle)? open = null;
        for (var k = 0; k < n; k++)
        {
            var index = (first + k) % n; var at = (first + k) * StepMeters;
            if (tight[index]) open = open is { } current ? (current.Start, at, current.Angle + curvature[index] * StepMeters) : (at, at, curvature[index] * StepMeters);
            else if (open is { } done) { regions.Add(done); open = null; }
        }
        if (open is { } last) regions.Add(last);

        var merged = new List<(double Start, double End, double Angle)>();
        foreach (var region in regions)
        {
            if (merged.Count > 0 && region.Start - merged[^1].End < MergeGapMeters && Math.Sign(region.Angle) == Math.Sign(merged[^1].Angle))
                merged[^1] = (merged[^1].Start, region.End, merged[^1].Angle + region.Angle);
            else merged.Add(region);
        }
        merged = merged.Where(region => Math.Abs(region.Angle) >= MinTurnDegrees * Math.PI / 180).ToList();
        if (merged.Count == 0) return [];

        var corners = new List<(double Start, double End, bool Wraps)>();
        for (var i = 0; i < merged.Count; i++)
        {
            var previousEnd = i > 0 ? merged[i - 1].End : merged[^1].End - length;
            var nextStart = i < merged.Count - 1 ? merged[i + 1].Start : merged[0].Start + length;
            var start = merged[i].Start - Math.Min(MarginMeters, (merged[i].Start - previousEnd) / 2);
            var end = merged[i].End + Math.Min(MarginMeters, (nextStart - merged[i].End) / 2);
            var lapStart = ((start % length) + length) % length;
            corners.Add((lapStart, lapStart + (end - start), lapStart + (end - start) > length));
        }

        // The corner on the start/finish line is Turn 1, the rest follow in driving order.
        var ordered = corners.OrderBy(corner => corner.Wraps ? -1 : corner.Start).ToList();
        var sections = new List<TrackSectionDefinition>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var name = $"Turn {i + 1}";
            var (start, end, wraps) = ordered[i];
            if (!wraps) sections.Add(new(name, start, Math.Min(end, length)));
            else
            {
                sections.Add(new(name, start, length));
                if (end - length > 0) sections.Add(new(name, 0, end - length));
            }
        }
        return sections.Where(section => section.StartsAtMeters < section.EndsAtMeters).OrderBy(section => section.StartsAtMeters).ToArray();
    }
}

/// <summary>Turns a position on the lap into words a steward can use: "Turn 3", or "Between Turn 3 and Turn 4" on a straight.</summary>
public static class TrackTurns
{
    private static bool IsTurn(TrackSectionDefinition section) => section.Name.StartsWith("Turn ", StringComparison.Ordinal);

    /// <param name="normalized">Position on the lap from 0 to 1, as the incident reports store it.</param>
    /// <returns>Null when the map has no turns or the position is unknown.</returns>
    public static string? Describe(TrackDefinition? map, double? normalized)
    {
        if (map == null || normalized is not { } fraction || !double.IsFinite(fraction) || fraction < 0 || fraction > 1) return null;
        var turns = map.Sections.Where(IsTurn).ToArray();
        if (turns.Length == 0) return null;
        var distance = fraction * map.LengthMeters % map.LengthMeters;
        if (turns.FirstOrDefault(section => distance >= section.StartsAtMeters && distance < section.EndsAtMeters) is { } inside) return inside.Name;
        // On a straight: the corner just behind and the one just ahead, going round the lap.
        var behind = turns.Where(section => section.EndsAtMeters <= distance).OrderByDescending(section => section.EndsAtMeters).FirstOrDefault()
            ?? turns.OrderByDescending(section => section.EndsAtMeters).First();
        var ahead = turns.Where(section => section.StartsAtMeters > distance).OrderBy(section => section.StartsAtMeters).FirstOrDefault()
            ?? turns.OrderBy(section => section.StartsAtMeters).First();
        return behind.Name == ahead.Name ? "After " + behind.Name : $"Between {behind.Name} and {ahead.Name}";
    }

    /// <summary>The incident's location line: the turn first, then whatever the report already says ("Track Position 11%").</summary>
    public static string Location(TrackDefinition? map, double? normalized, string? trackSection)
    {
        var turn = Describe(map, normalized); var section = trackSection?.Trim() ?? "";
        return turn == null ? section : section.Length == 0 ? turn : $"{turn} · {section}";
    }
}
