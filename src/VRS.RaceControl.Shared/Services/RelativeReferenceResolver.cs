using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

/// <summary>
/// Chooses the car the Relative widget is centred on: the car the operator pinned (by number such as
/// "32" or "#32", or by driver name), otherwise the HOST's own driver, otherwise the race leader
/// (LMU publishes no camera focus while spectating, so the operator picks the car to watch).
/// </summary>
public static class RelativeReferenceResolver
{
    public static string NormalizeNumber(string? value) => (value ?? string.Empty).Trim().TrimStart('#').Trim();

    public static TelemetryCar? Find(IReadOnlyList<TelemetryCar> cars, string? pin)
    {
        var key = (pin ?? string.Empty).Trim();
        if (key.Length == 0)
        {
            return null;
        }

        var number = NormalizeNumber(key);
        return cars.FirstOrDefault(car => number.Length > 0
                   && string.Equals(NormalizeNumber(car.CarNumber), number, StringComparison.OrdinalIgnoreCase))
               ?? cars.FirstOrDefault(car => string.Equals(car.DriverName, key, StringComparison.OrdinalIgnoreCase));
    }

    public static TelemetryCar? Resolve(IReadOnlyList<TelemetryCar> cars, string? pin, string? playerName)
    {
        if (cars.Count == 0)
        {
            return null;
        }

        return Find(cars, pin)
               ?? Find(cars, playerName)
               ?? cars.Where(car => car.Progress != null).OrderByDescending(car => car.Lap + car.Progress!.Value)
                   .FirstOrDefault();
    }

    /// <summary>Label such as "#32 Name" for selectors and the widget header.</summary>
    public static string Label(TelemetryCar car) => string.IsNullOrWhiteSpace(car.CarNumber)
        ? car.DriverName
        : $"#{NormalizeNumber(car.CarNumber)} {car.DriverName}";
}
