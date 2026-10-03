using System.Diagnostics;
using VRS.RaceControl.Shared.Enums;
using VRS.RaceControl.Shared.Models;
using VRS.RaceControl.Shared.Protocol;

public sealed partial class RelaySession
{
    private readonly SemaphoreSlim _fleetGate = new(1, 1);
    private string? _publisherUserId, _publisherSourceId, _publisherEpoch;
    private long _publisherLeaseStarted;
    private long _fleetSequence;
    private string? _fleetSourceEpoch;
    private readonly System.Collections.Concurrent.ConcurrentQueue<long> _fleetArrivalTimes = new();
    public TelemetryBatch? LatestFleet { get; private set; }
    public long FleetSequenceGaps { get; private set; }

    private bool PublisherLeaseActive => _publisherUserId != null
        && Stopwatch.GetElapsedTime(_publisherLeaseStarted) < TimeSpan.FromSeconds(8);
    private bool CanPublishFleet(RelayClient client) => client.SupportsFleetTelemetry
        && (client.Role == "host" || (RelayRoles.IsSecondaryOperator(client.Role)
            && _operatorPriority?.HasPermission(client.UserId, OperatorPermissions.Observe, DateTime.UtcNow) == true));

    public async Task<bool> HandleFleetMessageAsync(RelayClient client, ProtocolMessage message, CancellationToken token)
    {
        if (message.Type is not (MessageType.TelemetryPublisher or MessageType.TelemetryBatch)) return false;
        if (!CanPublishFleet(client)) return true;
        ProtocolMessage? outbound = null;
        var broadcast = false;
        TelemetryBatch? acceptedFleet = null;
        await _fleetGate.WaitAsync(token);
        try
        {
            if (message.Type == MessageType.TelemetryPublisher)
            {
                var request = message.GetPayload<TelemetryPublisherRequest>();
                if (request == null || request.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(request.SourceId)
                    || request.SourceId.Length > 128) return true;
                var mine = _publisherUserId == client.UserId && _publisherSourceId == request.SourceId;
                var acquired = !request.Release && (!PublisherLeaseActive || mine);
                if (request.Release && mine) { _publisherUserId = null; _publisherSourceId = null; }
                if (acquired)
                {
                    _publisherUserId = client.UserId; _publisherSourceId = request.SourceId;
                    _publisherLeaseStarted = Stopwatch.GetTimestamp();
                }
                outbound = ProtocolMessage.Create(MessageType.TelemetryPublisher,
                    new TelemetryPublisherResult(request.OperationId, acquired, _publisherUserId,
                        PublisherLeaseActive ? DateTimeOffset.UtcNow.AddSeconds(8 - Stopwatch.GetElapsedTime(_publisherLeaseStarted).TotalSeconds) : null,
                        acquired || request.Release ? null : "Another telemetry source holds the lease."));
            }
            else
            {
                var batch = message.GetPayload<TelemetryBatch>();
                if (batch == null || batch.Validate() != null || batch.ControlSessionId != Code
                    || !PublisherLeaseActive || _publisherUserId != client.UserId || _publisherSourceId != batch.SourceId) return true;
                var epoch = $"{client.UserId}:{batch.GameEpoch}:{batch.SourceEpoch}";
                if (_fleetSourceEpoch == epoch && batch.Sequence <= _fleetSequence) return true;
                var gap = _fleetSourceEpoch != null && (_fleetSourceEpoch != epoch || batch.Sequence != _fleetSequence + 1);
                if (gap) FleetSequenceGaps++;
                _fleetSourceEpoch = epoch; _fleetSequence = batch.Sequence; _publisherEpoch = batch.GameEpoch;
                var receivedAt = _authority?.Now ?? DateTimeOffset.UtcNow;
                var arrival = Stopwatch.GetTimestamp(); _fleetArrivalTimes.Enqueue(arrival);
                while (_fleetArrivalTimes.Count > 100) _fleetArrivalTimes.TryDequeue(out _);
                while (_fleetArrivalTimes.TryPeek(out var old) && Stopwatch.GetElapsedTime(old, arrival).TotalSeconds > 5)
                    _fleetArrivalTimes.TryDequeue(out _);
                var age = (receivedAt - batch.CapturedAt).TotalSeconds;
                var verified = _authority != null && batch.ClockEpoch == _authority.ClockEpoch
                    && batch.ClockUncertaintyMs is { } uncertainty && double.IsFinite(uncertainty) && uncertainty is >= 0 and <= 50
                    && _authorityClockReports.TryGetValue(client.Id, out var clock) && clock.Epoch == batch.ClockEpoch
                    && receivedAt - clock.ReceivedAt < TimeSpan.FromSeconds(15) && clock.UncertaintyMs <= 50
                    && age is >= -.1 and <= .75;
                LatestFleet = batch with { HasGap = gap || batch.HasGap, PublisherUserId = client.UserId,
                    ReceivedAt = receivedAt, FreshnessVerified = verified };
                // Detailed source samples are retained once in Relay; map consumers need the 5 Hz projection.
                outbound = ProtocolMessage.Create(MessageType.TelemetryBatch, LatestFleet with { DetailedSamples = null }, MessagePriority.Low);
                acceptedFleet = LatestFleet;
                broadcast = true;
            }
        }
        finally { _fleetGate.Release(); }
        if (outbound != null)
        {
            StampRelay(outbound, broadcast ? "all" : client.Id);
            if (broadcast)
            {
                EnqueueRuleTelemetry(acceptedFleet!);
                await Task.WhenAll(_clients.Values.Where(receiver => receiver.SupportsFleetTelemetry
                    && (receiver.Role == "host" || (RelayRoles.IsSecondaryOperator(receiver.Role) && CanReceiveHostState(receiver))))
                    .Select(receiver => TrySendAsync(receiver, outbound, token)));
            }
            else await TrySendAsync(client, outbound, token);
        }
        return true;
    }

    public TelemetryHealth GetFleetHealth()
    {
        var now = _authority?.Now ?? DateTimeOffset.UtcNow;
        var fleet = LatestFleet;
        var stamps = _fleetArrivalTimes.Where(s => Stopwatch.GetElapsedTime(s).TotalSeconds <= 5).ToArray();
        var rate = stamps.Length < 2 ? 0 : (stamps.Length - 1) / Math.Max(.001, Stopwatch.GetElapsedTime(stamps[0], stamps[^1]).TotalSeconds);
        var fresh = fleet?.ReceivedAt is { } last && now - last < TimeSpan.FromMilliseconds(750);
        return new("Relay ingest", !fresh ? "stale" : fleet!.FreshnessVerified ? "connected" : "unverified",
            $"Pending reports: {PendingRuleReports}; evidence: {PendingEvidenceClips}; evidence storage degraded: {EvidenceStorageDegraded}; ingress data drops: {IngressDataDrops}",
            fleet?.CapturedAt, rate, rate, fleet?.ScoredCars ?? 0, fresh ? fleet!.Cars.Count : 0,
            RuleDroppedBatches, FleetSequenceGaps, fleet?.SourceId, fleet?.GameEpoch, _publisherUserId,
            PublisherLeaseActive ? now.AddSeconds(8 - Stopwatch.GetElapsedTime(_publisherLeaseStarted).TotalSeconds) : null,
            fleet?.FreshnessVerified == true ? Math.Max(0, ((fleet.ReceivedAt ?? now) - fleet.CapturedAt).TotalMilliseconds) : null, RulesState);
    }
}
