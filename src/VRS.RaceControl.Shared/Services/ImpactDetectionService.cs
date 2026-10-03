using VRS.RaceControl.Shared.Models;
using VRS.RaceControl.Shared.Services;

namespace VRS.RaceControl.Shared.Services;

public class ImpactDetectionService
{
    private sealed record ImpactState(double ElapsedTime, double Magnitude,
        IncidentVector3 Position, double Damage);
    private readonly Dictionary<int, ImpactState> _lastImpacts = new();
    private readonly Dictionary<int, (string Name, string? Model)> _identities = new();
    private readonly Dictionary<string, double> _lastCorrelationTimes = new(StringComparer.Ordinal);
    private string? _sessionId;
    private double _lastFrameElapsed;

    public IReadOnlyList<IncidentReport> Process(IncidentTelemetryFrame frame, IncidentSettings settings)
    {
        if (!string.Equals(_sessionId, frame.SessionId, StringComparison.Ordinal)
            || frame.SessionElapsedSeconds < _lastFrameElapsed)
        {
            Reset();
            _sessionId = frame.SessionId;
        }
        _lastFrameElapsed = frame.SessionElapsedSeconds;
        if (!settings.DetectionEnabled) return Array.Empty<IncidentReport>();
        settings.Normalize();
        var incidents = new List<IncidentReport>();

        foreach (var vehicle in frame.Vehicles)
        {
            var identity = (vehicle.DriverName, vehicle.CarModel);
            if (_identities.TryGetValue(vehicle.VehicleId, out var priorIdentity) && priorIdentity != identity)
                _lastImpacts.Remove(vehicle.VehicleId);
            _identities[vehicle.VehicleId] = identity;
            var current = new ImpactState(
                vehicle.LastImpactElapsedTime,
                vehicle.LastImpactMagnitude,
                vehicle.LastImpactPosition,
                vehicle.Damage.Sum());
            if (!_lastImpacts.TryGetValue(vehicle.VehicleId, out var previous))
            {
                _lastImpacts[vehicle.VehicleId] = current;
                continue; // Never report an impact that predates detector startup.
            }
            _lastImpacts[vehicle.VehicleId] = current;
            if (current.ElapsedTime <= 0
                || current.ElapsedTime <= previous.ElapsedTime + 0.0001
                || current.Magnitude < settings.MinimumImpactMagnitude)
            {
                continue;
            }

            var partner = FindPartner(vehicle, frame.Vehicles, settings);
            var key = CreateCorrelationKey(frame, vehicle, partner, settings);
            if (_lastCorrelationTimes.TryGetValue(key, out var priorTime)
                && frame.SessionElapsedSeconds - priorTime <= settings.DeduplicationCooldownSeconds)
            {
                continue;
            }
            _lastCorrelationTimes[key] = frame.SessionElapsedSeconds;
            incidents.Add(CreateReport(frame, vehicle, partner, key, settings,
                Math.Max(0, current.Damage - previous.Damage)));
        }

        foreach (var stale in _lastCorrelationTimes
                     .Where(pair => frame.SessionElapsedSeconds - pair.Value > 120)
                     .Select(pair => pair.Key).ToArray())
        {
            _lastCorrelationTimes.Remove(stale);
        }
        var activeIds = frame.Vehicles.Select(vehicle => vehicle.VehicleId).ToHashSet();
        foreach (var staleId in _lastImpacts.Keys.Where(id => !activeIds.Contains(id)).ToArray())
        { _lastImpacts.Remove(staleId); _identities.Remove(staleId); }
        return incidents;
    }

    public void Reset()
    {
        _lastImpacts.Clear();
        _identities.Clear();
        _lastCorrelationTimes.Clear();
        _sessionId = null;
        _lastFrameElapsed = 0;
    }

    private static IncidentTelemetryVehicle? FindPartner(
        IncidentTelemetryVehicle source,
        IReadOnlyList<IncidentTelemetryVehicle> vehicles,
        IncidentSettings settings) => vehicles
        .Where(candidate => candidate.VehicleId != source.VehicleId)
        .Select(candidate => new
        {
            Vehicle = candidate,
            Distance = source.Position.DistanceTo(candidate.Position),
            ImpactDelta = Math.Abs(source.LastImpactElapsedTime - candidate.LastImpactElapsedTime)
        })
        .Where(candidate => candidate.Distance <= settings.DistanceToleranceMeters
                            && candidate.Vehicle.LastImpactElapsedTime > 0
                            && candidate.ImpactDelta <= settings.TimestampToleranceSeconds)
        .OrderBy(candidate => candidate.ImpactDelta)
        .ThenBy(candidate => candidate.Distance)
        .Select(candidate => candidate.Vehicle)
        .FirstOrDefault();

    private static IncidentReport CreateReport(
        IncidentTelemetryFrame frame,
        IncidentTelemetryVehicle vehicle,
        IncidentTelemetryVehicle? partner,
        string key,
        IncidentSettings settings,
        double damageDelta)
    {
        var severity = GetSeverity(vehicle.LastImpactMagnitude, settings);
        var bothImpacted = partner != null
            && Math.Abs(vehicle.LastImpactElapsedTime - partner.LastImpactElapsedTime)
                <= settings.TimestampToleranceSeconds;
        var relativeSpeed = partner == null || !vehicle.VelocityIsWorld || !partner.VelocityIsWorld
            ? (double?)null : RelativeSpeed(vehicle.Velocity, partner.Velocity);
        var type = partner != null
            ? bothImpacted ? IncidentType.CarToCarContact : IncidentType.PossibleContact
            : severity >= IncidentSeverity.Heavy
                ? IncidentType.HeavyImpact : IncidentType.UnknownImpact;
        var confidence = partner == null
            ? IncidentConfidence.Low
            : bothImpacted ? IncidentConfidence.High : IncidentConfidence.Medium;
        var participants = new[] { vehicle, partner }.Where(item => item != null).Cast<IncidentTelemetryVehicle>().ToArray();
        var reportedVehicle = partner ?? vehicle;

        var report = new IncidentReport
        {
            Source = IncidentSource.Auto,
            Severity = severity,
            Confidence = confidence,
            CorrelationKey = key,
            SessionId = frame.SessionId,
            ReporterId = "AUTO-LMU",
            ReporterName = "LMU detector",
            ReportedDriver = partner?.DriverName ?? vehicle.DriverName,
            ReportedCarNumber = partner?.CarNumber ?? vehicle.CarNumber ?? string.Empty,
            Lap = Math.Max(1, vehicle.Lap),
            Sector = reportedVehicle.Sector > 0 ? reportedVehicle.Sector : null,
            SessionTimeSeconds = frame.SessionElapsedSeconds,
            TrackSection = vehicle.TrackPositionNormalized.HasValue
                ? $"Track Position {vehicle.TrackPositionNormalized.Value:P0}" : string.Empty,
            TrackPositionNormalized = vehicle.TrackPositionNormalized,
            WorldPosition = vehicle.Position,
            ImpactPosition = vehicle.LastImpactPosition,
            RelativeSpeedKmh = relativeSpeed,
            ImpactMagnitude = vehicle.LastImpactMagnitude,
            DetectedAtUtc = frame.TimestampUtc,
            IncidentType = type,
            Priority = severity switch
            {
                IncidentSeverity.Minor => IncidentPriority.Low,
                IncidentSeverity.Medium => IncidentPriority.Normal,
                IncidentSeverity.Heavy => IncidentPriority.High,
                _ => IncidentPriority.Urgent
            },
            Participants = string.Join(" ↔ ", participants.Select(DisplayParticipant)),
            IncidentParticipants = participants.Select(ToParticipant).ToList(),
            Evidence =
            [
                new IncidentEvidence { Kind = "LastImpactMagnitude", Detail = vehicle.LastImpactMagnitude.ToString("0.###"), TimestampUtc = frame.TimestampUtc },
                new IncidentEvidence { Kind = "DamageDelta", Detail = damageDelta.ToString("0.###"), TimestampUtc = frame.TimestampUtc },
                new IncidentEvidence { Kind = "SeverityReason", Detail = $"Impact magnitude {vehicle.LastImpactMagnitude:0.##}; damage increase {damageDelta:0.##} (context only)", TimestampUtc = frame.TimestampUtc },
                new IncidentEvidence { Kind = "ConfidenceReason", Detail = partner == null
                    ? "One vehicle reported an impact; no matching second impact"
                    : $"Both vehicles reported impacts {Math.Abs(vehicle.LastImpactElapsedTime - partner.LastImpactElapsedTime):0.##} s apart, {vehicle.Position.DistanceTo(partner.Position):0.#} m apart",
                    TimestampUtc = frame.TimestampUtc }
            ],
            CreatedAtUtc = frame.TimestampUtc,
            UpdatedAtUtc = frame.TimestampUtc,
            History =
            [
                new IncidentHistoryEntry { TimestampUtc = frame.TimestampUtc, Actor = "AUTO-LMU", Action = "Incident detected", NewStatus = IncidentStatus.New }
            ]
        };
        report.Description = ImpactMessageFormatter.Format(report);
        return report;
    }

    private static string DisplayParticipant(IncidentTelemetryVehicle vehicle) =>
        !string.IsNullOrWhiteSpace(vehicle.CarNumber) ? $"#{vehicle.CarNumber} {vehicle.DriverName}" : vehicle.DriverName;

    private static IncidentParticipant ToParticipant(IncidentTelemetryVehicle vehicle) => new()
    {
        VehicleId = vehicle.VehicleId,
        DriverName = vehicle.DriverName,
        CarNumber = vehicle.CarNumber,
        RaceClass = IncidentRaceClass.Normalize(vehicle.RaceClass),
        Position = vehicle.Position,
        Velocity = vehicle.Velocity,
        SpeedKmh = vehicle.SpeedKmh ?? Math.Sqrt(vehicle.Velocity.X * vehicle.Velocity.X + vehicle.Velocity.Y * vehicle.Velocity.Y + vehicle.Velocity.Z * vehicle.Velocity.Z) * 3.6
    };

    private static IncidentSeverity GetSeverity(double magnitude, IncidentSettings settings) =>
        magnitude >= settings.SevereMagnitude ? IncidentSeverity.Severe
        : magnitude >= settings.HeavyMagnitude ? IncidentSeverity.Heavy
        : magnitude >= settings.MediumMagnitude ? IncidentSeverity.Medium
        : IncidentSeverity.Minor;

    private static double RelativeSpeed(IncidentVector3 first, IncidentVector3 second)
    {
        var x = first.X - second.X;
        var y = first.Y - second.Y;
        var z = first.Z - second.Z;
        return Math.Sqrt(x * x + y * y + z * z) * 3.6;
    }

    private static string CreateCorrelationKey(
        IncidentTelemetryFrame frame,
        IncidentTelemetryVehicle first,
        IncidentTelemetryVehicle? second,
        IncidentSettings settings)
    {
        var ids = second == null
            ? first.VehicleId.ToString()
            : string.Join('-', new[] { first.VehicleId, second.VehicleId }.Order());
        return $"{frame.SessionId}:{ids}";
    }
}
