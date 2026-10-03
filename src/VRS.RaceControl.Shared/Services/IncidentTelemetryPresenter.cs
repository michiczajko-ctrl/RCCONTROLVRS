using System.Globalization;
using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

public sealed record IncidentTelemetryRow(string Car, int Lap, string Sector, string TrackPosition,
    string Speed, string Throttle, string Brake, string Steering);
public static class IncidentTelemetryPresenter
{
    private static string Format(double? value, string format, string suffix = "") => value is { } number && double.IsFinite(number)
        ? number.ToString(format, CultureInfo.CurrentCulture) + suffix : "—";
    public static IReadOnlyList<IncidentTelemetryRow> Rows(TelemetryEvidenceSample sample, TrackDefinition? definition = null) => sample.Cars.Select(car => new IncidentTelemetryRow(
        $"#{car.CarNumber ?? "—"} {car.DriverName}", car.Lap, car.Sector?.ToString() ?? "—",
        $"{Format(car.Progress * 100, "0.0", "%")} · {Format(DistanceToFinish(car), "0", " m")}" + NamedSection(car, definition),
        Format(car.SpeedKmh, "0.0", " km/h"), Format(car.Throttle * 100, "0", "%"), Format(car.Brake * 100, "0", "%"),
        Format(car.Steering, "0.00"))).ToArray();
    private static string NamedSection(TelemetryCar car, TrackDefinition? definition) =>
        definition?.Verified == true && car.LapDistanceMeters is { } distance
            && definition.Sections.FirstOrDefault(section => distance >= section.StartsAtMeters && distance < section.EndsAtMeters) is { } section
            ? $" · {section.Name}" : "";
    public static string Comparison(TelemetryEvidenceSample sample)
    {
        if (sample.Cars.Count < 2) return "—";
        var a = sample.Cars[0]; var b = sample.Cars[1];
        return $"Δv {Format(a.SpeedKmh - b.SpeedKmh, "+0.0;-0.0;0.0", " km/h")} · spatial {Format(a.Position.DistanceTo(b.Position), "0.0", " m")} · along track {Format(AlongTrackDistance(a, b), "0.0", " m")}";
    }
    public static double? AlongTrackDistance(TelemetryCar a, TelemetryCar b)
    {
        if (a.TrackLengthMeters is not { } length || b.TrackLengthMeters is not { } otherLength
            || !double.IsFinite(length) || !double.IsFinite(otherLength) || length <= 0 || Math.Abs(length - otherLength) > .1
            || a.LapDistanceMeters is not { } first || b.LapDistanceMeters is not { } second
            || !double.IsFinite(first) || !double.IsFinite(second) || first < 0 || second < 0 || first > length || second > length) return null;
        var delta = Math.Abs(first - second);
        return Math.Min(delta, length - delta);
    }
    private static double? DistanceToFinish(TelemetryCar car) => car.TrackLengthMeters is > 0
        && car.LapDistanceMeters is >= 0 && car.LapDistanceMeters <= car.TrackLengthMeters
        ? car.TrackLengthMeters - car.LapDistanceMeters : null;
}
