namespace VRS.RaceControl.Shared.Models;

public sealed record TelemetryRuleContext(string SessionId, string? FcyPeriodId,
    DateTimeOffset? FcyActiveAt, long PolicyRevision, SpeedingPolicy Policy, DateTimeOffset ReceivedAt,
    ImpactDetectionPolicy? ImpactPolicy = null, long ImpactPolicyRevision = 0,
    AdvancedTelemetryPolicy? AdvancedPolicy = null, long AdvancedPolicyRevision = 0,
    VRS.RaceControl.Shared.Enums.FlagType Flag = VRS.RaceControl.Shared.Enums.FlagType.None,
    DateTimeOffset? FlagActivatedAt = null, DateTimeOffset? StandingStartArmedAt = null,
    StandingStartGrid? StandingGrid = null);

public sealed record TelemetryRuleReport(string Id, string RuleId, string SessionId, string GameEpoch,
    string SourceEpoch, string PeriodId, int VehicleId, TelemetryCar Car,
    DateTimeOffset StartedAt, DateTimeOffset LastObservedAt, DateTimeOffset? EndedAt,
    double SpeedAtDetectionKmh, double LimitKmh, double PeakSpeedKmh, double ConfirmedSeconds,
    long Revision, string ObservationState, SpeedingPolicy Policy)
{
    public double ExceededByKmh => SpeedAtDetectionKmh - LimitKmh;
}
