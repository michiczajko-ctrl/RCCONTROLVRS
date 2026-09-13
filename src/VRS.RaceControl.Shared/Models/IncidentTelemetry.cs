namespace VRS.RaceControl.Shared.Models;

public sealed record IncidentTelemetryVehicle(
    int VehicleId,
    string DriverName,
    string? CarNumber,
    int Lap,
    double? TrackPositionNormalized,
    IncidentVector3 Position,
    IncidentVector3 Velocity,
    double LastImpactElapsedTime,
    double LastImpactMagnitude,
    IncidentVector3 LastImpactPosition,
    IReadOnlyList<double> Damage);

public sealed record IncidentTelemetryFrame(
    string SessionId,
    string Simulator,
    string TrackName,
    string SessionType,
    double SessionElapsedSeconds,
    DateTime TimestampUtc,
    IReadOnlyList<IncidentTelemetryVehicle> Vehicles);
