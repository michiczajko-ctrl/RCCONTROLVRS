using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

public interface ITelemetryReportRule
{
    string Id { get; }
    IReadOnlyList<IncidentReport> Process(TelemetryBatch batch, TelemetryRuleContext context);
    IReadOnlyList<IncidentReport> Reset(DateTimeOffset time, string reason);
}

/// <summary>One Relay consumer, common source fencing, rule-neutral reports and steward-owned decisions.</summary>
public sealed class TelemetryReportEngine
{
    private readonly IReadOnlyList<ITelemetryReportRule> _rules;
    private string? _epoch;
    private long _sequence;
    private DateTimeOffset? _lastReceived;
    private double _maximumAge = .75;
    public TelemetryReportEngine(IEnumerable<ITelemetryReportRule>? rules = null) =>
        _rules = (rules ?? [new SpeedingReportRule(false), new SpeedingReportRule(true), new ImpactReportRule()]).ToArray();
    public IReadOnlyList<IncidentReport> Process(TelemetryBatch batch, TelemetryRuleContext context)
    {
        if (batch.Validate() != null || batch.ControlSessionId != context.SessionId || !context.Policy.IsValid) return [];
        var epoch = $"{context.SessionId}:{batch.GameEpoch}:{batch.SourceId}:{batch.SourceEpoch}";
        if (_epoch == epoch && batch.Sequence <= _sequence) return [];
        var reports = new List<IncidentReport>();
        var age = (context.ReceivedAt - batch.CapturedAt).TotalSeconds;
        if (_epoch != epoch || batch.HasGap || batch.Sequence != _sequence + 1
            || !batch.FreshnessVerified || age > context.Policy.MaximumAgeSeconds || age < -.1)
            reports.AddRange(Reset(context.ReceivedAt, "telemetry gap or source change"));
        _epoch = epoch; _sequence = batch.Sequence;
        if (!batch.FreshnessVerified || age > context.Policy.MaximumAgeSeconds || age < -.1) return reports;
        _lastReceived = context.ReceivedAt; _maximumAge = context.Policy.MaximumAgeSeconds;
        foreach (var rule in _rules) reports.AddRange(rule.Process(batch, context));
        return reports;
    }
    public IReadOnlyList<IncidentReport> Reset(DateTimeOffset time, string reason) =>
        _rules.SelectMany(rule => rule.Reset(time, reason)).ToArray();
    public IReadOnlyList<IncidentReport> CheckStale(DateTimeOffset time)
    {
        if (_lastReceived == null || (time - _lastReceived.Value).TotalSeconds <= _maximumAge) return [];
        _lastReceived = null; return Reset(time, "telemetry unavailable");
    }
}

public sealed class SpeedingReportRule(bool pitLane) : ITelemetryReportRule
{
    private readonly SpeedingTelemetryRule _rule = new(pitLane);
    public string Id => _rule.Id;
    public IReadOnlyList<IncidentReport> Process(TelemetryBatch batch, TelemetryRuleContext context) =>
        _rule.Process(batch, context).Select(TelemetryRuleIncidentFactory.Create).ToArray();
    public IReadOnlyList<IncidentReport> Reset(DateTimeOffset time, string reason) =>
        _rule.Reset(time, reason).Select(TelemetryRuleIncidentFactory.Create).ToArray();
}

public sealed class ImpactReportRule : ITelemetryReportRule
{
    private readonly ImpactDetectionService _detector = new();
    private IncidentSettings _settings = new();
    private ImpactDetectionPolicy? _policy;
    private long _policyRevision = -1;
    public string Id => "IMPACT";
    public IReadOnlyList<IncidentReport> Process(TelemetryBatch batch, TelemetryRuleContext context)
    {
        var policy = context.ImpactPolicy ?? new ImpactDetectionPolicy();
        if (!policy.IsValid) { _detector.Reset(); return []; }
        if (_policy != policy || _policyRevision != context.ImpactPolicyRevision)
        {
            _detector.Reset(); _policy = policy; _policyRevision = context.ImpactPolicyRevision;
            _settings = policy.ToSettings();
        }
        var vehicles = batch.Cars.Select(car => new IncidentTelemetryVehicle(car.VehicleId, car.DriverName,
            car.CarNumber, car.Lap, car.Progress, car.Position, car.WorldVelocity ?? new IncidentVector3(),
            car.LastImpactElapsedTime, car.LastImpactMagnitude, car.ImpactPosition ?? car.Position,
            car.Damage ?? [], car.Sector ?? 0, car.RaceClass, car.LapDistanceMeters, car.TrackLengthMeters,
            car.InPitLane, car.Throttle, car.Brake, car.Steering, car.SampleElapsedSeconds, car.CarModel,
            car.SpeedKmh, car.WorldVelocity != null)).ToArray();
        var frame = new IncidentTelemetryFrame(context.SessionId, batch.Simulator, batch.TrackId,
            batch.SessionType, batch.SessionElapsedSeconds, batch.CapturedAt.UtcDateTime, vehicles);
        var reports = _detector.Process(frame, _settings);
        foreach (var report in reports)
        {
            report.TelemetryObservation = new(report.Id, Id, batch.GameEpoch, batch.SourceEpoch,
                batch.GameEpoch, report.IncidentParticipants.Select(car => car.VehicleId).Order().ToArray(),
                1, batch.CapturedAt, "complete", $"impact:{context.ImpactPolicyRevision}",
                System.Text.Json.JsonSerializer.SerializeToElement(policy, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
            report.TelemetryEvidenceId = report.Id;
            report.CorrelationKey = $"IMPACT:{batch.GameEpoch}:{batch.SourceEpoch}:{report.Id}";
        }
        return reports;
    }
    public IReadOnlyList<IncidentReport> Reset(DateTimeOffset time, string reason) { _detector.Reset(); return []; }
}
