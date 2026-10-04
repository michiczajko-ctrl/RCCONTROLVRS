namespace VRS.RaceControl.Shared.Models;

/// <summary>Opt-in report rules. Unsupported measurements never imply an infringement.</summary>
public sealed record AdvancedTelemetryPolicy(
    bool ScSpeeding = false, bool StationaryCar = false, bool WrongWay = false,
    bool SuspiciousTelemetry = false, bool OvertakingUnderFcy = false,
    bool JumpStart = false, bool TrackLimits = false,
    double ScLimitKmh = 60, double StationarySeconds = 10, double WrongWaySeconds = 3,
    double TrackLimitsSeconds = 2, double OvertakingSeconds = 2,
    double StandingStartMovementMeters = 2)
{
    public bool IsValid => double.IsFinite(ScLimitKmh) && ScLimitKmh is >= 20 and <= 300
        && double.IsFinite(StationarySeconds) && StationarySeconds is >= 3 and <= 120
        && double.IsFinite(WrongWaySeconds) && WrongWaySeconds is >= 2 and <= 30
        && double.IsFinite(TrackLimitsSeconds) && TrackLimitsSeconds is >= 1 and <= 30
        && double.IsFinite(OvertakingSeconds) && OvertakingSeconds is >= 1 and <= 30
        && double.IsFinite(StandingStartMovementMeters) && StandingStartMovementMeters is >= 1 and <= 20;
}
