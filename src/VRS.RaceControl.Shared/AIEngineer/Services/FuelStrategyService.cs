using VRS.RaceControl.Shared.AIEngineer.Models;
using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.AIEngineer.Services;

public sealed class FuelStrategyService
{
    private readonly List<double> _validConsumptions = new();
    private readonly EngineerLocalizationService _text;
    private double? _lapStartFuel;
    private int? _lapStartNumber;
    private double? _lastReportedDeficit;
    private double? _lastReportedLapsRemaining;

    public FuelStrategyService(EngineerLocalizationService? localization = null)
    {
        _text = localization ?? new EngineerLocalizationService();
    }

    public FuelStrategyReport CurrentReport { get; private set; } = FuelStrategyReport.Empty;
    public IReadOnlyList<double> ValidConsumptions => _validConsumptions;

    public void AddLapConsumption(double liters)
    {
        if (liters > 0.05 && liters < 20)
        {
            _validConsumptions.Add(liters);
            if (_validConsumptions.Count > 20)
            {
                _validConsumptions.RemoveAt(0);
            }
        }
    }

    public IReadOnlyList<EngineerMessage> Analyze(TelemetrySnapshotPayload snapshot, TelemetrySnapshotPayload? previous, EngineerSettings settings)
    {
        var messages = new List<EngineerMessage>();
        if (snapshot.FuelLiters.HasValue && _lapStartFuel == null)
        {
            _lapStartFuel = snapshot.FuelLiters.Value;
            _lapStartNumber = snapshot.CompletedLaps;
        }

        if (previous != null
            && snapshot.CompletedLaps.HasValue
            && previous.CompletedLaps.HasValue
            && snapshot.CompletedLaps.Value > previous.CompletedLaps.Value)
        {
            TryAddCompletedLap(snapshot, previous);
            _lapStartFuel = snapshot.FuelLiters;
            _lapStartNumber = snapshot.CompletedLaps;
        }

        CurrentReport = BuildReport(snapshot, settings);
        if (!settings.FuelMessagesEnabled)
        {
            return messages;
        }

        if (CurrentReport.LapsRemaining.HasValue)
        {
            var laps = CurrentReport.LapsRemaining.Value;
            var importantChange = !_lastReportedLapsRemaining.HasValue || Math.Abs(laps - _lastReportedLapsRemaining.Value) >= 2;
            if (laps <= settings.CriticalFuelLapsThreshold)
            {
                var text = _text.FuelRange(laps);
                messages.Add(Create(EngineerEventType.CriticalFuel, EngineerPriority.Critical, text, TimeSpan.FromSeconds(20), true));
                _lastReportedLapsRemaining = laps;
            }
            else if (laps <= settings.LowFuelLapsThreshold && importantChange)
            {
                var text = _text.FuelRange(laps);
                messages.Add(Create(EngineerEventType.LowFuel, EngineerPriority.High, text, TimeSpan.FromSeconds(45), true));
                _lastReportedLapsRemaining = laps;
            }
        }

        if (settings.StrategyMessagesEnabled && CurrentReport.FuelDeltaToFinish.HasValue)
        {
            var delta = CurrentReport.FuelDeltaToFinish.Value;
            if (delta < -0.2)
            {
                var deficit = Math.Abs(delta);
                var significant = !_lastReportedDeficit.HasValue || deficit - _lastReportedDeficit.Value > 0.8;
                if (significant)
                {
                    var text = _text.FuelDeficit(deficit);
                    messages.Add(Create(EngineerEventType.FuelDeficit, EngineerPriority.Normal, text, TimeSpan.FromMinutes(2), false));
                    _lastReportedDeficit = deficit;
                }
            }
        }

        return messages;
    }

    public FuelStrategyReport BuildReport(TelemetrySnapshotPayload snapshot, EngineerSettings settings)
    {
        var average = Average(_validConsumptions);
        var median = Median(_validConsumptions);
        var last3 = Average(_validConsumptions.TakeLast(3));
        var last5 = Average(_validConsumptions.TakeLast(5));
        var consumption = last3 ?? average ?? snapshot.FuelPerLap;
        var fuel = snapshot.FuelLiters;
        double? lapsRemaining = fuel.HasValue && consumption.HasValue && consumption.Value > 0.01
            ? fuel.Value / consumption.Value
            : snapshot.EstimatedLapsLeft;

        var lapsToFinish = EstimateLapsToFinish(snapshot);
        double? needed = lapsToFinish.HasValue && consumption.HasValue
            ? consumption.Value * (lapsToFinish.Value + settings.FuelReserveLaps) + settings.FuelReserveLiters
            : null;
        double? delta = fuel.HasValue && needed.HasValue ? fuel.Value - needed.Value : null;

        return new FuelStrategyReport
        {
            LastLapConsumption = _validConsumptions.Count > 0 ? _validConsumptions[^1] : null,
            AverageConsumption = average,
            MedianConsumption = median,
            Last3Average = last3,
            Last5Average = last5,
            ConsumptionTrendPercent = TrendPercent(),
            LapsRemaining = lapsRemaining,
            FuelNeededToFinish = needed,
            FuelDeltaToFinish = delta,
            FuelToAdd = delta.HasValue && delta.Value < 0 ? Math.Abs(delta.Value) : 0,
            PitWindowInLaps = lapsRemaining.HasValue && lapsRemaining.Value > settings.FuelReserveLaps
                ? Math.Max(0, (int)Math.Floor(lapsRemaining.Value - settings.FuelReserveLaps))
                : null,
            ValidLapCount = _validConsumptions.Count
        };
    }

    private void TryAddCompletedLap(TelemetrySnapshotPayload snapshot, TelemetrySnapshotPayload previous)
    {
        if (!_lapStartFuel.HasValue || !snapshot.FuelLiters.HasValue)
        {
            return;
        }

        if (snapshot.IsInPitLane == true || previous.IsInPitLane == true || snapshot.IsInPit == true || previous.IsInPit == true)
        {
            return;
        }

        var lapMs = snapshot.LastLapTimeMs ?? previous.LastLapTimeMs;
        if (!IsValidLapTime(lapMs))
        {
            return;
        }

        var consumption = _lapStartFuel.Value - snapshot.FuelLiters.Value;
        AddLapConsumption(consumption);
    }

    public static bool IsValidLapTime(int? lapMs)
    {
        return lapMs is > 30_000 and < 600_000;
    }

    private static double? EstimateLapsToFinish(TelemetrySnapshotPayload snapshot)
    {
        if (snapshot.TotalLaps.HasValue && snapshot.CompletedLaps.HasValue && snapshot.TotalLaps.Value > snapshot.CompletedLaps.Value)
        {
            return snapshot.TotalLaps.Value - snapshot.CompletedLaps.Value;
        }

        return null;
    }

    private double? TrendPercent()
    {
        if (_validConsumptions.Count < 5)
        {
            return null;
        }

        var recent = Average(_validConsumptions.TakeLast(3));
        var older = Average(_validConsumptions.Take(Math.Max(1, _validConsumptions.Count - 3)));
        return recent.HasValue && older.HasValue && older.Value > 0.01
            ? ((recent.Value - older.Value) / older.Value) * 100
            : null;
    }

    private static double? Average(IEnumerable<double> values)
    {
        var list = values.Where(v => v > 0).ToList();
        return list.Count == 0 ? null : list.Average();
    }

    private static double? Median(IEnumerable<double> values)
    {
        var list = values.Where(v => v > 0).OrderBy(v => v).ToList();
        if (list.Count == 0) return null;
        var mid = list.Count / 2;
        return list.Count % 2 == 0 ? (list[mid - 1] + list[mid]) / 2.0 : list[mid];
    }

    private static EngineerMessage Create(
        EngineerEventType type,
        EngineerPriority priority,
        string text,
        TimeSpan cooldown,
        bool interrupt)
    {
        return new EngineerMessage
        {
            EventType = type,
            Priority = priority,
            TextEn = text,
            Cooldown = cooldown,
            CanInterruptLowerPriority = interrupt,
            DeduplicationKey = type.ToString()
        };
    }
}
