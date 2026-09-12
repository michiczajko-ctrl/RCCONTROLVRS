using VRS.RaceControl.Shared.AIEngineer.Models;
using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.AIEngineer.Services;

public sealed class LapAnalysisService
{
    private readonly List<int> _validLapTimes = new();
    private readonly EngineerLocalizationService _text;
    private int? _bestLapMs;

    public LapAnalysisService(EngineerLocalizationService? localization = null)
    {
        _text = localization ?? new EngineerLocalizationService();
    }

    public LapAnalysisReport CurrentReport { get; private set; } = LapAnalysisReport.Empty;

    public IReadOnlyList<EngineerMessage> Analyze(TelemetrySnapshotPayload snapshot, TelemetrySnapshotPayload? previous, EngineerSettings settings)
    {
        var messages = new List<EngineerMessage>();
        var isNewLap = previous?.CompletedLaps.HasValue == true
            && snapshot.CompletedLaps.HasValue
            && snapshot.CompletedLaps.Value > previous.CompletedLaps.Value;

        if (!isNewLap || !FuelStrategyService.IsValidLapTime(snapshot.LastLapTimeMs))
        {
            CurrentReport = BuildReport(null);
            return messages;
        }

        var lapMs = snapshot.LastLapTimeMs!.Value;
        var wasBest = !_bestLapMs.HasValue || lapMs < _bestLapMs.Value;
        _bestLapMs = wasBest ? lapMs : _bestLapMs;
        _validLapTimes.Add(lapMs);
        if (_validLapTimes.Count > 20)
        {
            _validLapTimes.RemoveAt(0);
        }

        CurrentReport = BuildReport(lapMs, wasBest);
        if (!settings.LapMessagesEnabled)
        {
            return messages;
        }

        if (wasBest)
        {
            var text = _text.BestLap();
            messages.Add(Create(EngineerEventType.NewBestLap, EngineerPriority.Low, text, TimeSpan.FromSeconds(20)));
        }
        else if (settings.ReadEveryLap)
        {
            var text = _text.LastLap(lapMs);
            messages.Add(Create(EngineerEventType.LapCompleted, EngineerPriority.Low, text, TimeSpan.FromSeconds(8)));
        }

        var trend = CurrentReport.TrendMs;
        if (trend.HasValue && Math.Abs(trend.Value) >= 500)
        {
            var improved = trend.Value < 0;
            messages.Add(Create(
                improved ? EngineerEventType.PaceImproved : EngineerEventType.PaceDropped,
                EngineerPriority.Normal,
                improved ? "The pace has improved by over half a second." : "The recent laps are on average over half a second slower.",
                TimeSpan.FromMinutes(2)));
        }

        return messages;
    }

    public LapAnalysisReport BuildReport(int? lastLapMs, bool isNewBest = false)
    {
        var best = _bestLapMs ?? (_validLapTimes.Count > 0 ? _validLapTimes.Min() : null);
        return new LapAnalysisReport
        {
            LastLapMs = lastLapMs ?? (_validLapTimes.Count > 0 ? _validLapTimes[^1] : null),
            BestLapMs = best,
            DeltaToBestMs = lastLapMs.HasValue && best.HasValue ? lastLapMs.Value - best.Value : null,
            Last3AverageMs = Average(_validLapTimes.TakeLast(3)),
            Last5AverageMs = Average(_validLapTimes.TakeLast(5)),
            TrendMs = Trend(),
            StabilityMs = Stability(),
            IsNewBest = isNewBest,
            ValidLapCount = _validLapTimes.Count
        };
    }

    private double? Trend()
    {
        if (_validLapTimes.Count < 6)
        {
            return null;
        }

        var recent = Average(_validLapTimes.TakeLast(3));
        var older = Average(_validLapTimes.Skip(Math.Max(0, _validLapTimes.Count - 6)).Take(3));
        return recent.HasValue && older.HasValue ? recent.Value - older.Value : null;
    }

    private double? Stability()
    {
        if (_validLapTimes.Count < 3)
        {
            return null;
        }

        var recent = _validLapTimes.TakeLast(5).Select(v => (double)v).ToList();
        var avg = recent.Average();
        return recent.Max(v => Math.Abs(v - avg));
    }

    private static double? Average(IEnumerable<int> values)
    {
        var list = values.Where(v => v > 0).ToList();
        return list.Count == 0 ? null : list.Average();
    }

    private static EngineerMessage Create(
        EngineerEventType type,
        EngineerPriority priority,
        string text,
        TimeSpan cooldown)
    {
        return new EngineerMessage
        {
            EventType = type,
            Priority = priority,
            TextEn = text,
            Cooldown = cooldown,
            DeduplicationKey = type.ToString()
        };
    }
}
