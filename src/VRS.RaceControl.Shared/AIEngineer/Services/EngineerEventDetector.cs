using VRS.RaceControl.Shared.AIEngineer.Models;
using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.AIEngineer.Services;

public sealed class EngineerEventDetector
{
    private readonly FuelStrategyService _fuel;
    private readonly LapAnalysisService _laps;
    private readonly TyreAnalysisService _tyres;

    public EngineerEventDetector(FuelStrategyService? fuel = null, LapAnalysisService? laps = null, TyreAnalysisService? tyres = null)
    {
        _fuel = fuel ?? new FuelStrategyService();
        _laps = laps ?? new LapAnalysisService();
        _tyres = tyres ?? new TyreAnalysisService();
    }

    public FuelStrategyReport FuelReport => _fuel.CurrentReport;
    public LapAnalysisReport LapReport => _laps.CurrentReport;

    public IReadOnlyList<EngineerMessage> Analyze(TelemetrySnapshotPayload snapshot, TelemetrySnapshotPayload? previous, EngineerSettings settings)
    {
        var messages = new List<EngineerMessage>();
        if (!settings.Enabled)
        {
            return messages;
        }

        if (previous == null && snapshot.IsAcConnected)
        {
            messages.Add(new EngineerMessage
            {
                EventType = EngineerEventType.TelemetryRecovered,
                Priority = EngineerPriority.Informational,
                TextEn = $"{snapshot.Simulator ?? "Simulator"} telemetry is active.",
                Cooldown = TimeSpan.FromMinutes(2),
                DeduplicationKey = "telemetry-active"
            });
        }
        else if (previous != null && previous.IsAcConnected != snapshot.IsAcConnected)
        {
            messages.Add(new EngineerMessage
            {
                EventType = snapshot.IsAcConnected ? EngineerEventType.TelemetryRecovered : EngineerEventType.TelemetryLost,
                Priority = snapshot.IsAcConnected ? EngineerPriority.Informational : EngineerPriority.Critical,
                TextEn = snapshot.IsAcConnected ? "Telemetry recovered." : "Simulator telemetry lost.",
                Cooldown = TimeSpan.FromSeconds(20),
                CanInterruptLowerPriority = !snapshot.IsAcConnected,
                DeduplicationKey = snapshot.IsAcConnected ? "telemetry-recovered" : "telemetry-lost"
            });
        }

        if (previous?.IsInPitLane != snapshot.IsInPitLane && snapshot.IsInPitLane.HasValue)
        {
            messages.Add(new EngineerMessage
            {
                EventType = snapshot.IsInPitLane.Value ? EngineerEventType.EnteredPitLane : EngineerEventType.ExitedPitLane,
                Priority = EngineerPriority.Informational,
                TextEn = snapshot.IsInPitLane.Value ? "Entered pit lane." : "Exited pit lane.",
                Cooldown = TimeSpan.FromSeconds(5),
                DeduplicationKey = snapshot.IsInPitLane.Value ? "pit-in" : "pit-out"
            });
        }

        if (previous?.PitLimiterOn != snapshot.PitLimiterOn && snapshot.PitLimiterOn.HasValue)
        {
            messages.Add(new EngineerMessage
            {
                EventType = snapshot.PitLimiterOn.Value ? EngineerEventType.PitLimiterOn : EngineerEventType.PitLimiterOff,
                Priority = EngineerPriority.Informational,
                TextEn = snapshot.PitLimiterOn.Value ? "Pit limiter on." : "Pit limiter off.",
                Cooldown = TimeSpan.FromSeconds(5),
                DeduplicationKey = snapshot.PitLimiterOn.Value ? "limiter-on" : "limiter-off"
            });
        }

        messages.AddRange(_fuel.Analyze(snapshot, previous, settings));
        messages.AddRange(_laps.Analyze(snapshot, previous, settings));
        messages.AddRange(_tyres.Analyze(snapshot, settings));
        return messages;
    }
}
