using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

public interface ITelemetryRule
{
    string Id { get; }
    IReadOnlyList<TelemetryRuleReport> Process(TelemetryBatch batch, TelemetryRuleContext context);
    IReadOnlyList<TelemetryRuleReport> Reset(DateTimeOffset time, string reason);
}

/// <summary>Single consumer; callers persist reports before publishing them to stewards.</summary>
public sealed class TelemetryRuleEngine
{
    private readonly IReadOnlyList<ITelemetryRule> _rules;
    private string? _epoch;
    private long _sequence;
    private DateTimeOffset? _lastFreshReceived;
    private double _maximumAge = .75;
    public TelemetryRuleEngine(IEnumerable<ITelemetryRule>? rules = null) =>
        _rules = (rules ?? [new SpeedingTelemetryRule(false), new SpeedingTelemetryRule(true)]).ToArray();

    public IReadOnlyList<TelemetryRuleReport> Process(TelemetryBatch batch, TelemetryRuleContext context)
    {
        if (batch.Validate() != null || batch.ControlSessionId != context.SessionId || !context.Policy.IsValid) return [];
        var output = new List<TelemetryRuleReport>();
        var epoch = $"{batch.ControlSessionId}:{batch.GameEpoch}:{batch.SourceId}:{batch.SourceEpoch}";
        if (_epoch == epoch && batch.Sequence <= _sequence) return [];
        var age = (context.ReceivedAt - batch.CapturedAt).TotalSeconds;
        var changedSource = _epoch != epoch;
        if (changedSource || batch.HasGap || (!changedSource && batch.Sequence != _sequence + 1)
            || age > context.Policy.MaximumAgeSeconds || age < -.1)
            output.AddRange(Reset(context.ReceivedAt, "telemetry gap or source change"));
        _epoch = epoch; _sequence = batch.Sequence;
        if (age > context.Policy.MaximumAgeSeconds || age < -.1) return output;
        _lastFreshReceived = context.ReceivedAt;
        _maximumAge = context.Policy.MaximumAgeSeconds;
        foreach (var rule in _rules) output.AddRange(rule.Process(batch, context));
        return output;
    }

    public IReadOnlyList<TelemetryRuleReport> Reset(DateTimeOffset time, string reason) =>
        _rules.SelectMany(rule => rule.Reset(time, reason)).ToArray();

    public IReadOnlyList<TelemetryRuleReport> CheckStale(DateTimeOffset time)
    {
        if (_lastFreshReceived == null || (time - _lastFreshReceived.Value).TotalSeconds <= _maximumAge) return [];
        _lastFreshReceived = null;
        return Reset(time, "telemetry unavailable");
    }
}

/// <summary>FCY and pit lane share an episode algorithm with separate applicability and limits.</summary>
public sealed class SpeedingTelemetryRule(bool pitLane) : ITelemetryRule
{
    private readonly Dictionary<int, Observation> _cars = [];
    private string? _period;
    private long _policyRevision = -1;
    public string Id => pitLane ? "PIT_SPEEDING" : "FCY_SPEEDING";

    public IReadOnlyList<TelemetryRuleReport> Process(TelemetryBatch batch, TelemetryRuleContext context)
    {
        var period = pitLane ? batch.GameEpoch : context.FcyPeriodId;
        var output = new List<TelemetryRuleReport>();
        if (_period != period || _policyRevision != context.PolicyRevision)
        {
            output.AddRange(Reset(context.ReceivedAt, "period or policy changed"));
            _period = period; _policyRevision = context.PolicyRevision;
        }
        if (period == null || (!pitLane && context.FcyActiveAt == null)) return output;
        var policy = context.Policy;
        var limit = pitLane ? policy.PitLimitKmh : policy.FcyLimitKmh;
        foreach (var car in batch.Cars)
        {
            if (!_cars.TryGetValue(car.VehicleId, out var observation))
                _cars[car.VehicleId] = observation = new(context.ReceivedAt.AddSeconds(policy.GraceSeconds));
            if (observation.Identity != null && observation.Identity != (car.DriverName, car.CarModel))
            {
                End(observation, output, "vehicle identity changed");
                observation = new(context.ReceivedAt.AddSeconds(policy.GraceSeconds));
                _cars[car.VehicleId] = observation;
            }
            observation.Identity = (car.DriverName, car.CarModel);
            if (car.SampleElapsedSeconds <= observation.LastElapsed)
            {
                if ((context.ReceivedAt - observation.LastNewAt).TotalSeconds > policy.MaximumAgeSeconds)
                { End(observation, output, "vehicle telemetry stale"); observation.GraceUntil = context.ReceivedAt.AddSeconds(policy.GraceSeconds); }
                continue;
            }
            var gap = car.SampleElapsedSeconds - observation.LastElapsed;
            var validPit = car.InPitLane.HasValue && car.InPitLane == pitLane;
            if (observation.LastElapsed >= 0 && gap > policy.MaximumGapSeconds)
            {
                End(observation, output, "observation interrupted");
                observation.GraceUntil = context.ReceivedAt.AddSeconds(policy.GraceSeconds);
            }
            observation.LastElapsed = car.SampleElapsedSeconds;
            observation.LastNewAt = context.ReceivedAt;
            if (!validPit || context.ReceivedAt < observation.GraceUntil
                || (!pitLane && context.ReceivedAt < context.FcyActiveAt!.Value.AddSeconds(policy.GraceSeconds)))
            { End(observation, output, validPit ? "grace" : "pit status excluded or unknown"); continue; }

            if (car.SpeedKmh > limit + policy.ToleranceKmh)
            {
                observation.ReleaseElapsed = null;
                observation.StartElapsed ??= car.SampleElapsedSeconds;
                observation.StartedAt ??= batch.CapturedAt;
                observation.Peak = Math.Max(observation.Peak, car.SpeedKmh);
                observation.Duration = observation.Report == null ? car.SampleElapsedSeconds - observation.StartElapsed.Value
                    : observation.Duration + (observation.LastWasAbove ? gap : 0);
                observation.LastWasAbove = true;
                if (observation.Duration >= policy.MinimumSeconds)
                {
                    var report = observation.Report == null
                        ? new TelemetryRuleReport(Guid.NewGuid().ToString("N"), Id, context.SessionId,
                            batch.GameEpoch, batch.SourceEpoch, period, car.VehicleId, car,
                            observation.StartedAt.Value, batch.CapturedAt, null, car.SpeedKmh, limit,
                            observation.Peak, observation.Duration, 1, "active", policy)
                        : observation.Report with { Car = car, LastObservedAt = batch.CapturedAt, PeakSpeedKmh = observation.Peak,
                            ConfirmedSeconds = observation.Duration, Revision = observation.Report.Revision + 1 };
                    observation.Report = report;
                    output.Add(report);
                }
            }
            else if (observation.Report == null)
            {
                // Confirmation requires uninterrupted time strictly above tolerance.
                observation.ClearCandidate();
            }
            else if (car.SpeedKmh < limit + policy.ReleaseToleranceKmh)
            {
                observation.LastWasAbove = false;
                observation.ReleaseElapsed ??= car.SampleElapsedSeconds;
                if (car.SampleElapsedSeconds - observation.ReleaseElapsed >= policy.ReleaseSeconds)
                    End(observation, output, "complete");
            }
            else { observation.LastWasAbove = false; observation.ReleaseElapsed = null; }
        }
        foreach (var missing in _cars.Where(pair => !batch.Cars.Any(car => car.VehicleId == pair.Key)).ToArray())
        { End(missing.Value, output, "vehicle unavailable"); _cars.Remove(missing.Key); }
        return output;
    }

    public IReadOnlyList<TelemetryRuleReport> Reset(DateTimeOffset time, string reason)
    {
        var reports = new List<TelemetryRuleReport>();
        foreach (var observation in _cars.Values) End(observation, reports, reason);
        _cars.Clear();
        return reports;
    }
    private static void End(Observation observation, List<TelemetryRuleReport> reports, string reason)
    {
        if (observation.Report is { } report)
            reports.Add(report with { EndedAt = report.LastObservedAt, ObservationState = reason, Revision = report.Revision + 1 });
        observation.ClearCandidate();
    }
    private sealed class Observation(DateTimeOffset graceUntil)
    {
        public DateTimeOffset GraceUntil = graceUntil;
        public double LastElapsed = -1;
        public DateTimeOffset LastNewAt;
        public (string Name, string? Model)? Identity;
        public bool LastWasAbove;
        public double? StartElapsed, ReleaseElapsed;
        public DateTimeOffset? StartedAt;
        public double Peak, Duration;
        public TelemetryRuleReport? Report;
        public void ClearCandidate() { StartElapsed = null; ReleaseElapsed = null; StartedAt = null; Peak = 0; Duration = 0; Report = null; LastWasAbove = false; }
    }
}
