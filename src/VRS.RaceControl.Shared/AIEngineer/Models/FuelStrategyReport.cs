namespace VRS.RaceControl.Shared.AIEngineer.Models;

public sealed class FuelStrategyReport
{
    public static FuelStrategyReport Empty { get; } = new();

    public double? LastLapConsumption { get; init; }
    public double? AverageConsumption { get; init; }
    public double? MedianConsumption { get; init; }
    public double? Last3Average { get; init; }
    public double? Last5Average { get; init; }
    public double? ConsumptionTrendPercent { get; init; }
    public double? LapsRemaining { get; init; }
    public double? FuelNeededToFinish { get; init; }
    public double? FuelDeltaToFinish { get; init; }
    public double? FuelToAdd { get; init; }
    public int? PitWindowInLaps { get; init; }
    public int ValidLapCount { get; init; }
    public bool HasDeficit => FuelDeltaToFinish.HasValue && FuelDeltaToFinish.Value < 0;
}
