using System.Collections.Concurrent;
using System.Threading.Channels;
using VRS.RaceControl.Shared.Models;
using VRS.RaceControl.Shared.Protocol;
using VRS.RaceControl.Shared.Services;

public sealed partial class RelaySession
{
    private readonly Channel<(TelemetryBatch Batch, SessionSnapshot State)> _ruleBatches =
        Channel.CreateBounded<(TelemetryBatch, SessionSnapshot)>(new BoundedChannelOptions(8)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly ConcurrentDictionary<string, IncidentReport> _pendingRuleReports = new();
    private long _ruleDroppedBatches;
    private volatile string _rulesState = "suspended: telemetry report storage unavailable";
    private volatile bool _ruleStorageDegraded;
    private int _incidentRefreshRequested;
    private volatile bool _incidentRefreshStorageDegraded;
    private TrackDefinitionReference? _ruleTrackDefinition;
    public string RulesState => _ruleStorageDegraded || _incidentRefreshStorageDegraded ? "degraded: pending incident persistence/resync" : _rulesState;
    public long RuleDroppedBatches => Interlocked.Read(ref _ruleDroppedBatches);
    public int PendingRuleReports => _pendingRuleReports.Count;

    private void EnqueueRuleTelemetry(TelemetryBatch batch)
    {
        if (_authority == null || !_durableIncidents.SupportsRuleReports) return;
        if (!_ruleBatches.Writer.TryWrite((batch, _authority.Snapshot))) Interlocked.Increment(ref _ruleDroppedBatches);
    }

    public async Task RunTelemetryRulesAsync(CancellationToken token)
    {
        var engine = new TelemetryReportEngine();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                if (_authority == null || !_durableIncidents.SupportsRuleReports) return;
                while (_ruleBatches.Reader.TryRead(out var item))
                {
                    var (batch, state) = item;
                    if (!batch.FreshnessVerified || !_authority.IsWriterActive || _pendingRuleReports.Count >= 2048 || _pendingEvidence.Count >= 512
                        || (_authority.Now - batch.CapturedAt).TotalSeconds > state.Policy.MaximumAgeSeconds)
                    {
                        _rulesState = "suspended: clock, source freshness or storage unavailable";
                        RetainRuleReports(engine.Reset(_authority.Now, "observation unavailable"));
                        RetainEvidence(_evidenceBuffer.Interrupt("observation unavailable"));
                        continue;
                    }
                    _rulesState = "active";
                    _ruleEvidenceSource = batch;
                    _ruleTrackDefinition = state.TrackDefinition is { } track && track.IsValid
                        && TrackLayoutBindingPolicy.Matches(track, state.TrackLayoutBinding, batch) ? track : null;
                    RetainEvidence(_evidenceBuffer.Add(batch));
                    RetainRuleReports(engine.Process(batch, new(Code, state.FcyPeriodId, state.FcyActiveAt,
                        state.PolicyRevision, state.Policy, batch.ReceivedAt ?? _authority.Now,
                        state.ImpactPolicy, state.ImpactPolicyRevision)));
                }
                RetainRuleReports(engine.CheckStale(_authority.Now));
                if (LatestFleet?.ReceivedAt is not { } last || _authority.Now - last > TimeSpan.FromMilliseconds(750))
                {
                    _rulesState = "suspended: telemetry unavailable";
                    RetainEvidence(_evidenceBuffer.Interrupt("telemetry unavailable"));
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void RetainRuleReports(IEnumerable<IncidentReport> reports)
    {
        foreach (var report in reports)
        {
            var observation = report.TelemetryObservation!;
            if (observation.Revision == 1 && _ruleEvidenceSource is { } source && _deliveryStore?.SupportsTelemetryEvidence == true)
                RetainEvidence([_evidenceBuffer.Begin(report.Id, source, observation.ObservedAt, _ruleTrackDefinition, observation.VehicleIds.ToArray())]);
            _pendingRuleReports.AddOrUpdate(report.Id, report, (_, old) => observation.Revision > old.TelemetryObservation!.Revision ? report : old);
        }
    }

    public async Task RunTelemetryReportWriterAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                if (_authority == null || !_durableIncidents.SupportsRuleReports) return;
                // Database refresh belongs to persistence, never the telemetry consumer.
                if (Interlocked.Exchange(ref _incidentRefreshRequested, 0) != 0)
                {
                    try
                    {
                        var state = await _durableIncidents.LoadAsync(Code, token)
                            ?? throw new InvalidDataException("Incident state unavailable.");
                        var snapshot = ProtocolMessage.Create(MessageType.IncidentSnapshot,
                            new IncidentSnapshotPayload(state.Revision, state.Reports, state.Generation));
                        StampRelay(snapshot, "all"); await RouteHostMessageAsync(snapshot, token);
                        _incidentRefreshStorageDegraded = false;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
                    { _incidentRefreshStorageDegraded = true; Interlocked.Exchange(ref _incidentRefreshRequested, 1); }
                }
                foreach (var pending in _pendingRuleReports.Values.OrderBy(r => r.CreatedAtUtc).Take(104))
                {
                    try
                    {
                        var result = await _durableIncidents.ApplyRuleReportAsync(Code, _authority.ClockEpoch,
                            pending, token);
                        if (result.Applied && result.Report != null)
                        {
                            var update = ProtocolMessage.Create(MessageType.IncidentStateUpdate,
                                new IncidentStateUpdatePayload(result.Revision, result.Report, result.Generation));
                            StampRelay(update, "all");
                            await RouteHostMessageAsync(update, token);
                        }
                        if (result.Applied || result.Duplicate)
                        {
                            _ruleStorageDegraded = false;
                            ((ICollection<KeyValuePair<string, IncidentReport>>)_pendingRuleReports)
                                .Remove(new(pending.Id, pending)); // Newer observations stay queued.
                        }
                        else _ruleStorageDegraded = true;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
                    { _ruleStorageDegraded = true; }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
