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
    IReadOnlyList<double> Damage,
    int Sector = 0,
    string? RaceClass = null,
    double? LapDistanceMeters = null,
    double? TrackLengthMeters = null,
    bool? InPitLane = null,
    double? Throttle = null,
    double? Brake = null,
    double? Steering = null,
    double? SampleElapsedSeconds = null,
    string? CarModel = null,
    double? SpeedKmh = null,
    bool VelocityIsWorld = true);

public sealed record IncidentTelemetryFrame(
    string SessionId,
    string Simulator,
    string TrackName,
    string SessionType,
    double SessionElapsedSeconds,
    DateTime TimestampUtc,
    IReadOnlyList<IncidentTelemetryVehicle> Vehicles);
