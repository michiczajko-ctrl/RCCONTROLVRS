using System.Globalization;
using System.Text.Json;
using VRS.RaceControl.Shared.Enums;
using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

/// <summary>Report-only rules, fed by the same fenced, fresh Relay stream as FCY and impacts.</summary>
public sealed class AdvancedTelemetryReportRule : ITelemetryReportRule
{
    private readonly SpeedingTelemetryRule _sc = new(false);
    private readonly Dictionary<int, CarHistory> _history = [];
    private readonly Dictionary<string, Episode> _episodes = [];
    private readonly Dictionary<string, double> _pairOrder = [];
    private string? _context;
    public string Id => "ADVANCED";

    public IReadOnlyList<IncidentReport> Process(TelemetryBatch batch, TelemetryRuleContext context)
    {
        var policy = context.AdvancedPolicy ?? new();
        if (!policy.IsValid) return Reset(context.ReceivedAt, "invalid policy");
        var period = $"{context.AdvancedPolicyRevision}:{context.Flag}:{context.FlagActivatedAt:O}:{context.StandingStartArmedAt:O}:{context.PolicyRevision}";
        var reports = new List<IncidentReport>();
        if (_context != period) { reports.AddRange(Reset(context.ReceivedAt, "rule context changed")); _context = period; }
        if (policy.ScSpeeding && context.Flag == FlagType.SafetyCar && context.FlagActivatedAt is { } scAt)
        {
            var scContext = context with { FcyPeriodId = $"SC:{period}", FcyActiveAt = scAt,
                Policy = context.Policy with { FcyLimitKmh = policy.ScLimitKmh } };
            reports.AddRange(_sc.Process(batch, scContext).Select(r => TelemetryRuleIncidentFactory.Create(r with { RuleId = "SC_SPEEDING" })));
        }
        else reports.AddRange(_sc.Reset(context.ReceivedAt, "SC inactive").Select(r => TelemetryRuleIncidentFactory.Create(r with { RuleId = "SC_SPEEDING" })));

        var fresh = new Dictionary<int, TelemetryCar>();
        var seen = new HashSet<string>();
        foreach (var car in batch.Cars)
        {
            _history.TryGetValue(car.VehicleId, out var old);
            if (old != null && old.Car.DriverName == car.DriverName && old.Car.CarModel == car.CarModel
                && car.SampleElapsedSeconds <= old.Car.SampleElapsedSeconds)
            {
                // Keep existing episodes through a repeated sample only while that vehicle remains fresh.
                if ((batch.CapturedAt - old.At).TotalSeconds <= context.Policy.MaximumGapSeconds)
                    foreach (var key in _episodes.Where(p => p.Value.VehicleIds.Contains(car.VehicleId)).Select(p => p.Key)) seen.Add(key);
                continue;
            }
            var contiguous = old != null && old.Car.DriverName == car.DriverName && old.Car.CarModel == car.CarModel
                && car.SampleElapsedSeconds > old.Car.SampleElapsedSeconds
                && car.SampleElapsedSeconds - old.Car.SampleElapsedSeconds <= context.Policy.MaximumGapSeconds;
            if (contiguous && Distance(car) is { } currentProgress && Distance(old!.Car) is { } oldProgress
                && Math.Abs(currentProgress - oldProgress) > Math.Max(10,
                    Math.Max(car.SpeedKmh, old.Car.SpeedKmh) / 3.6 * (car.SampleElapsedSeconds - old.Car.SampleElapsedSeconds) * 2 + 5))
                contiguous = false; // A scoring jump/teleport cannot establish an overtaking or direction episode.
            if (!contiguous)
            {
                foreach (var key in _episodes.Where(p => p.Value.VehicleIds.Contains(car.VehicleId)).Select(p => p.Key).ToArray())
                    End(key, reports, "vehicle observation interrupted");
                RemovePairs(car.VehicleId);
            }
            var history = contiguous ? old! : new CarHistory(car, batch.CapturedAt, batch.CapturedAt);
            fresh[car.VehicleId] = car;
            var onTrack = car.InPitLane == false;
            if (!onTrack) RemovePairs(car.VehicleId); // Pit order changes cannot become FCY overtaking evidence.
            var raceRunning = batch.SessionType.Contains("Race", StringComparison.OrdinalIgnoreCase)
                && context.Flag is FlagType.Green or FlagType.FullCourseYellow or FlagType.SafetyCar;
            var baselineReady = (batch.CapturedAt - history.FirstAt).TotalSeconds >= context.Policy.GraceSeconds;
            if (policy.StationaryCar && raceRunning && onTrack && baselineReady && car.SpeedKmh < 1)
                Observe("CAR_STOPPED_ON_TRACK", [car], policy.StationarySeconds, batch, context, period,
                    "Vehicle stationary outside pit lane; review track obstruction.", reports, seen);

            var dt = contiguous ? car.SampleElapsedSeconds - history.Car.SampleElapsedSeconds : 0;
            if (policy.WrongWay && raceRunning && onTrack && baselineReady && contiguous
                && Distance(car) is { } distance && Distance(history.Car) is { } previousDistance
                && car.SpeedKmh > 15 && distance < previousDistance - .5
                && previousDistance - distance <= Math.Max(10, car.SpeedKmh / 3.6 * dt * 2 + 5))
                Observe("WRONG_WAY", [car], policy.WrongWaySeconds, batch, context, period,
                    "Sustained backwards scoring progress; steward must verify direction.", reports, seen);

            if (policy.SuspiciousTelemetry && contiguous && dt > 0
                && Math.Abs(car.SpeedKmh - history.Car.SpeedKmh) / 3.6 / dt > 80)
                Observe("SUSPICIOUS_TELEMETRY", [car], .4, batch, context, period,
                    "Repeated acceleration discontinuity above 80 m/s²; this is a data-quality report.", reports, seen);

            if (policy.TrackLimits && raceRunning && onTrack && car.OffTrackConfirmed == true)
                Observe("TRACK_LIMITS", [car], policy.TrackLimitsSeconds, batch, context, period,
                    "Provider-confirmed track-boundary excursion; review sporting exceptions.", reports, seen);

            if (policy.JumpStart && context.Flag == FlagType.ReadyForGreen && context.StandingStartArmedAt is { } armed
                && onTrack && batch.CapturedAt >= armed && context.StandingGrid is { } standingGrid
                && standingGrid.GameEpoch == batch.GameEpoch && standingGrid.SourceEpoch == batch.SourceEpoch)
            {
                var baseline = standingGrid.Cars.FirstOrDefault(c => c.VehicleId == car.VehicleId
                    && c.DriverName == car.DriverName && c.CarModel == car.CarModel);
                if (baseline != null && car.SpeedKmh > 3
                    && baseline.Position.DistanceTo(car.Position) > policy.StandingStartMovementMeters)
                    Observe("JUMP_START", [car], .4, batch, context, period,
                        "Movement from an explicitly armed standing-grid baseline before GREEN.", reports, seen);
            }
            history.Car = car; history.At = batch.CapturedAt; _history[car.VehicleId] = history;
        }
        if (policy.OvertakingUnderFcy && context.Flag == FlagType.FullCourseYellow && context.FcyActiveAt is { } fcyAt
            && batch.CapturedAt >= fcyAt.AddSeconds(context.Policy.GraceSeconds))
        {
            var cars = fresh.Values.Where(c => c.InPitLane == false && Distance(c) != null
                && (batch.CapturedAt - _history[c.VehicleId].FirstAt).TotalSeconds >= context.Policy.GraceSeconds)
                .OrderBy(c => c.VehicleId).ToArray();
            for (var i = 0; i < cars.Length; i++)
            for (var j = i + 1; j < cars.Length; j++)
            {
                var a = cars[i]; var b = cars[j];
                if (a.TrackLengthMeters != b.TrackLengthMeters) continue;
                var key = $"{a.VehicleId}:{b.VehicleId}";
                var difference = Distance(a)!.Value - Distance(b)!.Value;
                if (!_pairOrder.TryGetValue(key, out var original))
                { if (Math.Abs(difference) > 5) _pairOrder[key] = difference; continue; }
                if (Math.Abs(difference) > 5 && Math.Sign(difference) != Math.Sign(original))
                    Observe("OVERTAKING_UNDER_FCY", original < 0 ? [a, b] : [b, a], policy.OvertakingSeconds, batch, context, period,
                        "Sustained reversal of scoring order under FCY; verify class, lapping, pit and steward-authorized exceptions.", reports, seen);
            }
        }
        foreach (var key in _episodes.Keys.Except(seen).ToArray()) End(key, reports, "condition ended or unavailable");
        foreach (var id in _history.Keys.Except(batch.Cars.Select(c => c.VehicleId)).ToArray())
        { _history.Remove(id); RemovePairs(id); }
        return reports;
    }

    private void RemovePairs(int vehicleId)
    {
        foreach (var key in _pairOrder.Keys.Where(k => k.StartsWith(vehicleId + ":", StringComparison.Ordinal)
            || k.EndsWith(":" + vehicleId, StringComparison.Ordinal)).ToArray()) _pairOrder.Remove(key);
    }

    private static double? Distance(TelemetryCar car) => car.TrackLengthMeters is > 0 && car.LapDistanceMeters is { } distance
        && distance >= 0 && distance <= car.TrackLengthMeters ? car.Lap * car.TrackLengthMeters.Value + distance : null;

    private void Observe(string rule, TelemetryCar[] cars, double minimumSeconds, TelemetryBatch batch,
        TelemetryRuleContext context, string period, string description, List<IncidentReport> output, HashSet<string> seen)
    {
        var key = rule + ":" + string.Join(':', cars.Select(c => c.VehicleId)); seen.Add(key);
        if (_episodes.TryGetValue(key, out var previous)
            && (batch.CapturedAt - previous.LastAt).TotalSeconds > context.Policy.MaximumGapSeconds)
            End(key, output, "condition continuity interrupted");
        if (!_episodes.TryGetValue(key, out var episode))
            _episodes[key] = episode = new(batch.CapturedAt, cars.Select(c => c.VehicleId).ToArray());
        episode.LastAt = batch.CapturedAt;
        episode.PeakSpeedKmh = Math.Max(episode.PeakSpeedKmh, cars[0].SpeedKmh);
        if ((batch.CapturedAt - episode.Start).TotalSeconds < minimumSeconds) return;
        var car = cars[0]; var id = episode.Report?.Id ?? Guid.NewGuid().ToString("N");
        var revision = (episode.Report?.TelemetryObservation?.Revision ?? 0) + 1;
        var configuration = JsonSerializer.SerializeToElement(context.AdvancedPolicy ?? new(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var report = new IncidentReport
        {
            Id = id, SessionId = context.SessionId, Source = IncidentSource.Auto,
            ReportedDriver = car.DriverName, ReportedCarNumber = car.CarNumber ?? "", Lap = car.Lap, Sector = car.Sector,
            TrackPositionNormalized = car.Progress, WorldPosition = car.Position, SessionTimeSeconds = car.SampleElapsedSeconds,
            CreatedAtUtc = episode.Start.UtcDateTime, DetectedAtUtc = episode.Start.UtcDateTime, UpdatedAtUtc = batch.CapturedAt.UtcDateTime,
            IncidentType = rule switch {
                "TRACK_LIMITS" => IncidentType.TrackLimits,
                "OVERTAKING_UNDER_FCY" or "JUMP_START" => IncidentType.IgnoringFlags,
                "SUSPICIOUS_TELEMETRY" or "CAR_STOPPED_ON_TRACK" => IncidentType.Other,
                _ => IncidentType.DangerousDriving },
            Description = string.Create(CultureInfo.InvariantCulture, $"{rule}: {description} Observed {(batch.CapturedAt - episode.Start).TotalSeconds:0.0}s; speed {car.SpeedKmh:0.0} km/h; peak {episode.PeakSpeedKmh:0.0} km/h."),
            TelemetryEvidenceId = id, SourceReportIds = [id], CorrelationKey = $"{rule}:{batch.GameEpoch}:{batch.SourceEpoch}:{period}:{id}",
            TelemetryObservation = new(id, rule, batch.GameEpoch, batch.SourceEpoch, period, episode.VehicleIds,
                revision, batch.CapturedAt, "active", $"advanced:{context.AdvancedPolicyRevision}", configuration),
            IncidentParticipants = cars.Select(c => new IncidentParticipant { VehicleId = c.VehicleId, DriverName = c.DriverName,
                CarNumber = c.CarNumber, RaceClass = c.RaceClass, Position = c.Position, Velocity = c.WorldVelocity, SpeedKmh = c.SpeedKmh }).ToList(),
            Evidence = [new() { Kind = "TelemetryRule", Detail = rule, TimestampUtc = batch.CapturedAt.UtcDateTime }]
        };
        episode.Report = report; output.Add(report);
    }

    private void End(string key, List<IncidentReport> reports, string reason)
    {
        if (_episodes.Remove(key, out var episode) && episode.Report is { } report)
        {
            var closed = JsonSerializer.Deserialize<IncidentReport>(JsonSerializer.Serialize(report))!;
            closed.TelemetryObservation = report.TelemetryObservation! with { State = reason, Revision = report.TelemetryObservation!.Revision + 1 };
            reports.Add(closed);
        }
    }
    public IReadOnlyList<IncidentReport> Reset(DateTimeOffset time, string reason)
    {
        var reports = new List<IncidentReport>();
        foreach (var key in _episodes.Keys.ToArray()) End(key, reports, reason);
        reports.AddRange(_sc.Reset(time, reason).Select(r => TelemetryRuleIncidentFactory.Create(r with { RuleId = "SC_SPEEDING" })));
        _history.Clear(); _pairOrder.Clear(); return reports;
    }
    private sealed class CarHistory(TelemetryCar car, DateTimeOffset at, DateTimeOffset firstAt)
    {
        public TelemetryCar Car = car;
        public DateTimeOffset At = at;
        public DateTimeOffset FirstAt = firstAt;
    }
    private sealed class Episode(DateTimeOffset start, int[] vehicles)
    {
        public DateTimeOffset Start = start;
        public DateTimeOffset LastAt = start;
        public double PeakSpeedKmh;
        public int[] VehicleIds = vehicles;
        public IncidentReport? Report;
    }
}
