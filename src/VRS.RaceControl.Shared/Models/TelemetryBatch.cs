using System.Text.Json.Serialization;

namespace VRS.RaceControl.Shared.Models;

public sealed record TelemetryCar(
    int VehicleId, string DriverName, string? CarNumber, string? CarModel, string? RaceClass,
    int Lap, int? Sector, double? LapDistanceMeters, double? TrackLengthMeters,
    IncidentVector3 Position, IncidentVector3? WorldVelocity, double SpeedKmh,
    bool? InPitLane, double? Throttle, double? Brake, double? Steering,
    double SampleElapsedSeconds, double LastImpactElapsedTime = 0,
    double LastImpactMagnitude = 0, IncidentVector3? ImpactPosition = null,
    IReadOnlyList<double>? Damage = null)
{
    public double? Progress => TrackLengthMeters is > 0 && LapDistanceMeters is { } distance
        ? Math.Clamp(distance / TrackLengthMeters.Value, 0, 1) : null;
    public bool IsValid => VehicleId >= 0 && !string.IsNullOrWhiteSpace(DriverName) && DriverName.Length <= 200
        && CarNumber?.Length is not > 40 && CarModel?.Length is not > 200 && RaceClass?.Length is not > 80
        && double.IsFinite(SpeedKmh) && SpeedKmh is >= 0 and <= 600
        && double.IsFinite(SampleElapsedSeconds) && SampleElapsedSeconds >= 0
        && Lap >= 0 && (Sector == null || Sector is >= 1 and <= 3)
        && Finite(LapDistanceMeters) && Finite(TrackLengthMeters) && Finite(Throttle) && Finite(Brake) && Finite(Steering)
        && FiniteVector(Position) && (WorldVelocity == null || FiniteVector(WorldVelocity))
        && (ImpactPosition == null || FiniteVector(ImpactPosition))
        && double.IsFinite(LastImpactElapsedTime) && double.IsFinite(LastImpactMagnitude)
        && (Damage == null || (Damage.Count <= 128 && Damage.All(double.IsFinite)));
    private static bool Finite(double? value) => value == null || double.IsFinite(value.Value);
    private static bool FiniteVector(IncidentVector3? vector) => vector != null
        && double.IsFinite(vector.X) && double.IsFinite(vector.Y) && double.IsFinite(vector.Z);
}

public sealed record TelemetryBatch(string ControlSessionId, string GameEpoch, string SourceId,
    string SourceEpoch, long Sequence, string Simulator, string TrackId, string? LayoutId,
    string SessionType, double SessionElapsedSeconds, DateTimeOffset CapturedAt,
    IReadOnlyList<TelemetryCar> Cars, int ScoredCars = 0, int GameVersion = 0,
    string LayoutVersion = "LMU-SDK-V01", bool HasGap = false,
    string? PublisherUserId = null, DateTimeOffset? ReceivedAt = null,
    string? ClockEpoch = null, double? ClockUncertaintyMs = null, bool FreshnessVerified = false,
    IReadOnlyList<TelemetryEvidenceSample>? DetailedSamples = null, bool DetailedSamplesHaveGap = false)
{
    /// <summary>Provider-local timestamp; never serialized across machines.</summary>
    [JsonIgnore] public long CaptureMonotonicTimestamp { get; init; }
    public string? Validate() => string.IsNullOrWhiteSpace(GameEpoch) || GameEpoch.Length > 128
        || string.IsNullOrWhiteSpace(SourceId) || SourceId.Length > 128
        || string.IsNullOrWhiteSpace(SourceEpoch) || SourceEpoch.Length > 128
        || Simulator?.Length is not <= 80 || TrackId?.Length is not <= 200 || LayoutId?.Length is > 100
        || SessionType?.Length is not <= 80 || LayoutVersion?.Length is not <= 128
        || Sequence < 1 || Cars == null || Cars.Count > 104
        || Cars.Any(car => car == null || !car.IsValid)
        || Cars.Select(car => car.VehicleId).Distinct().Count() != Cars.Count
        || !double.IsFinite(SessionElapsedSeconds) || SessionElapsedSeconds < 0
        || (DetailedSamples != null && (DetailedSamples.Count > 6 || DetailedSamples.Any(s => s == null
            || s.Sequence < 1 || s.Cars == null || s.Cars.Count > 104 || s.Cars.Any(c => c == null || !c.IsValid)
            || s.Cars.Select(c => c.VehicleId).Distinct().Count() != s.Cars.Count
            || s.CapturedAt > CapturedAt || CapturedAt - s.CapturedAt > TimeSpan.FromMilliseconds(400))
            || DetailedSamples.Zip(DetailedSamples.Skip(1)).Any(pair => pair.Second.Sequence <= pair.First.Sequence
                || pair.Second.CapturedAt <= pair.First.CapturedAt)))
        ? "Invalid telemetry batch" : null;
}

public sealed record TelemetryHealth(string Stage, string State, string Detail,
    DateTimeOffset? LastNewSample, double SampleRateHz, double PublishRateHz,
    int ScoredCars, int FreshCars, long DroppedBatches, long SequenceGaps,
    string? SourceId, string? GameEpoch, string? PublisherId, DateTimeOffset? LeaseExpiresAt,
    double? LatencyMs, string RulesState);

public sealed record TelemetryPublisherRequest(Guid OperationId, string SourceId, bool Release = false);
public sealed record TelemetryPublisherResult(Guid OperationId, bool Acquired, string? OwnerId,
    DateTimeOffset? ExpiresAt, string? Error = null);

public sealed record SpeedingPolicy(double FcyLimitKmh = 60, double PitLimitKmh = 60,
    double ToleranceKmh = 3, double GraceSeconds = 10, double MinimumSeconds = 2,
    double ReleaseToleranceKmh = 1, double ReleaseSeconds = 1,
    double MaximumGapSeconds = .4, double MaximumAgeSeconds = .75)
{
    public bool IsValid => FcyLimitKmh is > 0 and <= 300 && PitLimitKmh is > 0 and <= 300
        && ToleranceKmh is >= 0 and <= 20 && GraceSeconds is >= 0 and <= 60
        && MinimumSeconds is >= .2 and <= 30 && ReleaseToleranceKmh >= 0
        && ReleaseToleranceKmh <= ToleranceKmh && ReleaseSeconds is >= .2 and <= 10
        && MaximumGapSeconds is >= .1 and <= 2 && MaximumAgeSeconds is >= .1 and <= 5;
}
