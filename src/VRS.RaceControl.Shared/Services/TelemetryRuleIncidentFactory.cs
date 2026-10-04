using System.Globalization;
using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

public static class TelemetryRuleIncidentFactory
{
    public static IncidentReport Create(TelemetryRuleReport rule) => new()
    {
        Id = rule.Id, SessionId = rule.SessionId, Source = IncidentSource.Auto,
        TelemetryObservation = new(rule.Id, rule.RuleId, rule.GameEpoch, rule.SourceEpoch, rule.PeriodId,
            [rule.VehicleId], rule.Revision, rule.LastObservedAt, rule.ObservationState, $"speeding:{rule.Policy}"),
        TelemetryRule = rule, TelemetryEvidenceId = rule.Id, ReportedDriver = rule.Car.DriverName,
        ReportedCarNumber = rule.Car.CarNumber ?? "", Lap = rule.Car.Lap, Sector = rule.Car.Sector,
        TrackPositionNormalized = rule.Car.Progress, WorldPosition = rule.Car.Position,
        SessionTimeSeconds = rule.Car.SampleElapsedSeconds, DetectedAtUtc = rule.StartedAt.UtcDateTime,
        CreatedAtUtc = rule.StartedAt.UtcDateTime, UpdatedAtUtc = rule.LastObservedAt.UtcDateTime,
        IncidentType = rule.RuleId is "FCY_SPEEDING" or "SC_SPEEDING" ? IncidentType.SpeedingUnderNeutralization : IncidentType.PitLaneInfringement,
        CorrelationKey = $"{rule.RuleId}:{rule.GameEpoch}:{rule.SourceEpoch}:{rule.PeriodId}:{rule.VehicleId}:{rule.Id}",
        SourceReportIds = [rule.Id],
        Description = string.Create(CultureInfo.InvariantCulture,
            $"{rule.RuleId}: {rule.SpeedAtDetectionKmh:0.0} km/h, limit {rule.LimitKmh:0.0}, +{rule.ExceededByKmh:0.0}; peak {rule.PeakSpeedKmh:0.0}, observed {rule.ConfirmedSeconds:0.0}s ({rule.ObservationState})."),
        IncidentParticipants = [new() { VehicleId = rule.VehicleId, DriverName = rule.Car.DriverName,
            CarNumber = rule.Car.CarNumber, RaceClass = rule.Car.RaceClass, Position = rule.Car.Position,
            Velocity = rule.Car.WorldVelocity, SpeedKmh = rule.Car.SpeedKmh }],
        Evidence = [new() { Kind = "TelemetryRule", Detail = rule.RuleId, TimestampUtc = rule.LastObservedAt.UtcDateTime }]
    };
}
