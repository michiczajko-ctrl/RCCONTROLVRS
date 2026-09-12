namespace VRS.RaceControl.Shared.AIEngineer.Models;

public sealed class LapAnalysisReport
{
    public static LapAnalysisReport Empty { get; } = new();

    public int? LastLapMs { get; init; }
    public int? BestLapMs { get; init; }
    public int? DeltaToBestMs { get; init; }
    public double? Last3AverageMs { get; init; }
    public double? Last5AverageMs { get; init; }
    public double? TrendMs { get; init; }
    public double? StabilityMs { get; init; }
    public bool IsNewBest { get; init; }
    public int ValidLapCount { get; init; }
}
