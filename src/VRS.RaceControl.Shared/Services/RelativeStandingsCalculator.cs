using System.Globalization;
using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

/// <summary>One line of the Relative widget. GapSeconds is negative for cars ahead of the reference, positive for cars behind it.</summary>
public sealed record RelativeRow(
    int VehicleId, string DriverName, string? CarNumber, string? RaceClass, int Position,
    double? GapSeconds, bool IsReference, bool InPit, bool IsAhead);

/// <summary>
/// Builds the "Relative" list (cars around one reference driver with time gaps) from a fleet
/// sample. Gaps are estimated: on-track distance divided by the faster of the two cars' speeds,
/// because the fleet sample carries no timing.
/// </summary>
public static class RelativeStandingsCalculator
{
    private const double MinimumSpeedMetersPerSecond = 5;

    public static IReadOnlyList<RelativeRow> Calculate(IReadOnlyList<TelemetryCar> cars, string? referenceDriver,
        int carsAhead = 3, int carsBehind = 3)
    {
        if (cars.Count == 0 || string.IsNullOrWhiteSpace(referenceDriver))
        {
            return Array.Empty<RelativeRow>();
        }

        var reference = cars.FirstOrDefault(car =>
            string.Equals(car.DriverName, referenceDriver, StringComparison.OrdinalIgnoreCase));
        return reference == null ? Array.Empty<RelativeRow>() : Build(cars, reference, carsAhead, carsBehind);
    }

    /// <summary>Same list, centred on a specific car (matched by vehicle id, so duplicate names cannot confuse it).</summary>
    public static IReadOnlyList<RelativeRow> Calculate(IReadOnlyList<TelemetryCar> cars, TelemetryCar? reference,
        int carsAhead = 3, int carsBehind = 3) =>
        reference == null || cars.Count == 0 ? Array.Empty<RelativeRow>() : Build(cars, reference, carsAhead, carsBehind);

    private static IReadOnlyList<RelativeRow> Build(IReadOnlyList<TelemetryCar> cars, TelemetryCar reference,
        int carsAhead, int carsBehind)
    {
        if (reference.Progress is not { } referenceProgress || reference.TrackLengthMeters is not > 0)
        {
            return Array.Empty<RelativeRow>();
        }

        var trackLength = reference.TrackLengthMeters!.Value;
        var positions = cars.Where(car => car.Progress != null)
            .OrderByDescending(car => car.Lap + car.Progress!.Value)
            .Select((car, index) => (car.VehicleId, Position: index + 1))
            .ToDictionary(item => item.VehicleId, item => item.Position);

        var others = new List<(TelemetryCar Car, double Meters)>();
        foreach (var car in cars)
        {
            if (car.VehicleId == reference.VehicleId || car.Progress is not { } progress)
            {
                continue;
            }

            var delta = progress - referenceProgress;
            delta -= Math.Round(delta); // shortest way round the lap: (-0.5, 0.5]
            others.Add((car, delta * trackLength));
        }

        RelativeRow ToRow(TelemetryCar car, double meters, bool isAhead)
        {
            var speed = Math.Max(reference.SpeedKmh, car.SpeedKmh) / 3.6;
            double? gap = speed < MinimumSpeedMetersPerSecond ? null : -meters / speed;
            return new RelativeRow(car.VehicleId, car.DriverName, car.CarNumber, car.RaceClass,
                positions.GetValueOrDefault(car.VehicleId), gap, false, car.InPitLane == true, isAhead);
        }

        var ahead = others.Where(item => item.Meters > 0).OrderBy(item => item.Meters).Take(carsAhead)
            .OrderByDescending(item => item.Meters).Select(item => ToRow(item.Car, item.Meters, true));
        var behind = others.Where(item => item.Meters <= 0).OrderByDescending(item => item.Meters).Take(carsBehind)
            .Select(item => ToRow(item.Car, item.Meters, false));
        var self = new RelativeRow(reference.VehicleId, reference.DriverName, reference.CarNumber, reference.RaceClass,
            positions.GetValueOrDefault(reference.VehicleId), 0, true, reference.InPitLane == true, false);
        return ahead.Append(self).Concat(behind).ToArray();
    }

    public static string FormatGap(double? gapSeconds)
    {
        if (gapSeconds is not { } gap)
        {
            return "--";
        }

        if (Math.Abs(gap) < 0.05)
        {
            return "0.0";
        }

        return (gap > 0 ? "+" : "-") + Math.Abs(gap).ToString("0.0", CultureInfo.InvariantCulture);
    }
}
