using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.AIEngineer.Models;

public sealed class EngineerState
{
    public bool IsEnabled { get; set; }
    public bool IsTelemetryConnected { get; set; }
    public TelemetrySnapshotPayload? LastSnapshot { get; set; }
    public string LastMessage { get; set; } = string.Empty;
    public string VoiceStatus { get; set; } = "Idle";
    public string PushToTalkStatus { get; set; } = "Text fallback";
    public FuelStrategyReport Fuel { get; set; } = FuelStrategyReport.Empty;
    public LapAnalysisReport Pace { get; set; } = LapAnalysisReport.Empty;
}
