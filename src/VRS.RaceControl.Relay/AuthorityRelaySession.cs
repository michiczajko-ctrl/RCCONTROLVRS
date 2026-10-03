using VRS.RaceControl.Shared.Enums;
using VRS.RaceControl.Shared.Models;
using VRS.RaceControl.Shared.Protocol;
using VRS.RaceControl.Shared.Services;

public sealed partial class RelaySession
{
    private SessionAuthority? _authority;
    private long _ingressDataDrops;
    public long IngressDataDrops => Interlocked.Read(ref _ingressDataDrops);
    public void RecordIngressDataDrop() => Interlocked.Increment(ref _ingressDataDrops);
    private readonly SemaphoreSlim _authorityDispatchGate = new(1, 1);
    public SessionSnapshot? AuthoritySnapshot => _authority?.Snapshot;

    public async Task AttachAuthorityAsync(string creatorId, IAuthorityStore store, CancellationToken token = default)
    {
        if (_authority != null) throw new InvalidOperationException("Authority mode is fixed for a session.");
        var authority = new SessionAuthority(Code, creatorId, store);
        await authority.InitializeAsync(token);
        _authority = authority;
        _deliveryStore = store;
        lock (_membershipGate)
        {
            _mainUserId ??= creatorId;
            if (_operatorPriority == null)
            {
                _operatorPriority = new OperatorPriority(creatorId, "Main HOST", DateTime.UtcNow);
                _operatorPriority.RestoreDurableAuthority(authority.Snapshot.Generation, authority.Snapshot.Revision);
            }
        }
        RefreshAuthorityMetadata(authority.Snapshot);
    }

    private void RefreshAuthorityMetadata(SessionSnapshot snapshot)
    {
        lock (_membershipGate)
        {
            _sessionGeneration = snapshot.Generation;
            _sessionRevision = snapshot.Revision;
            _sessionOwnerUserId = snapshot.ControllerId ?? string.Empty;
        }
    }

    private bool HasAuthorityControl(RelayClient client) => client.Role == "host"
        || _operatorPriority?.HasPermission(client.UserId, OperatorPermissions.Control, DateTime.UtcNow) == true;
    public bool HasReservedControlBudget(RelayClient client) => client.Role != "driver" && HasAuthorityControl(client);

    public async Task<bool> HandleAuthorityMessageAsync(RelayClient client, ProtocolMessage message, CancellationToken token)
    {
        var authority = _authority;
        if (authority == null) return false;
        if (message.Type == MessageType.TelemetryHealth)
        {
            if (client.Role == "host" || (client.Role != "driver" && CanReceiveHostState(client)))
            {
                var response = ProtocolMessage.Create(MessageType.TelemetryHealth, GetFleetHealth());
                StampRelay(response, client.Id);
                await TrySendAsync(client, response, token);
            }
            return true;
        }
        if (message.Type == MessageType.EvidenceRequest)
        {
            await HandleEvidenceRequestAsync(client, message, token);
            return true;
        }
        if (message.Type == MessageType.TrackDefinitionRequest)
        { await HandleTrackDefinitionRequestAsync(client, message, token); return true; }
        // Legacy ACK means transport receipt only and is never a v3 execution result.
        if (message.Type == MessageType.Ack) return true;
        if (message.Type == MessageType.AuthorityReceipt)
        {
            if (message.GetPayload<AuthorityReceipt>() is { } receipt) RecordAuthorityReceipt(client, receipt);
            return true;
        }
        if (message.Type == MessageType.AuthorityPlaybackReceipt)
        {
            if (message.GetPayload<AuthorityPlaybackReceipt>() is { } receipt) RecordAuthorityPlayback(client, receipt);
            return true;
        }
        if (message.Type == MessageType.AuthorityDeliveryStatus)
        {
            if (client.Role == "host" || (client.Role != "driver" && CanReceiveHostState(client)))
            {
                var status = ProtocolMessage.Create(MessageType.AuthorityDeliveryStatus, DeliveryStatus);
                StampRelay(status, client.Id);
                await TrySendAsync(client, status, token);
            }
            return true;
        }
        if (message.Type == MessageType.PenaltyHistoryRequest)
        {
            var request = message.GetPayload<AuthorityPenaltyHistoryRequest>();
            if (client.Role == "driver" && request != null && request.Cursor?.IsValid != false)
                await SendAuthorityPenaltyHistoryAsync(client, token, request.Cursor, request.RequestId);
            return true;
        }
        if (message.Type == MessageType.OverlayStateRequest)
        {
            await SendAuthoritySnapshotAsync(client, token);
            await SendAuthorityPenaltyHistoryAsync(client, token);
            return true;
        }
        if (message.Type == MessageType.TimeSyncRequest)
        {
            var request = message.GetPayload<TimeSyncRequestPayload>();
            if (request == null || string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 128) return true;
            var received = authority.Now;
            RecordAuthorityClock(client, request);
            var response = ProtocolMessage.Create(MessageType.TimeSyncResponse, new TimeSyncResponsePayload
            { RequestId = request.RequestId, HostReceivedAt = received, HostSentAt = authority.Now, ClockEpoch = authority.ClockEpoch });
            StampRelay(response, client.Id);
            await TrySendAsync(client, response, token);
            return true;
        }
        // Commit and publication share one order. Time sync/socket heartbeats do not wait here.
        await _authorityDispatchGate.WaitAsync(token);
        try
        {
        AuthorityOutcome? outcome = null;
        if (message.Type == MessageType.SessionCommand)
        {
            var command = message.GetPayload<SessionCommand>();
            if (command == null) return true;
            string? preconditionError = null;
            if (command.Kind == "green.arm" && GetAuthorityReadiness() is { Ready: false } readiness)
                preconditionError = readiness.Reason ?? "Clock readiness is degraded.";
            AuthorityRecipient? recipient = null;
            if (command.Kind == "track.confirm-layout")
            {
                TrackLayoutBinding? binding;
                try { binding = System.Text.Json.JsonSerializer.Deserialize<TrackLayoutBinding>(command.Payload,
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)); }
                catch (System.Text.Json.JsonException) { binding = null; }
                if (LatestFleet is not { FreshnessVerified: true } fleet || authority.Snapshot.TrackDefinition is not { } track
                    || binding?.IsValid != true || binding.Checksum != track.Checksum
                    || binding.GameEpoch != fleet.GameEpoch || binding.SourceEpoch != fleet.SourceEpoch
                    || authority.Now - fleet.CapturedAt > TimeSpan.FromMilliseconds(750)
                    || !TrackLayoutBindingPolicy.Matches(track, binding, fleet))
                    preconditionError = "Layout confirmation requires fresh matching game telemetry and track length.";
            }
            if (command.TargetId != "all" && _clients.TryGetValue(command.TargetId, out var driver)
                && driver.Role == "driver" && !string.IsNullOrWhiteSpace(driver.UserId))
                recipient = new(driver.Id, driver.UserId, driver.Name);
            outcome = await authority.ExecuteAsync(command, client.UserId, HasAuthorityControl(client), token, recipient, preconditionError);
        }
        else if (message.Type == MessageType.AuthorityTransfer)
        {
            var request = message.GetPayload<AuthorityTransfer>();
            if (request == null) return true;
            var target = _clients.Values.FirstOrDefault(item => item.UserId == request.TargetOperatorId);
            outcome = await authority.TransferAsync(request, client.UserId, HasAuthorityControl(client),
                target != null && HasAuthorityControl(target), token);
        }
        else if (AuthorityCommands.IsSemantic(message.Type) || message.Type is MessageType.OperatorCommand
            or MessageType.SessionOperationV2 or MessageType.SessionOwnershipV2 or MessageType.OperatorPriorityTransfer
            or MessageType.OperatorPriorityDecision or MessageType.ForceSystemReset)
        {
            var rejection = ProtocolMessage.Create(MessageType.CommandResult,
                new CommandResult(Guid.Empty, "rejected", authority.Snapshot.Generation,
                    authority.Snapshot.Revision, "Use fenced authority commands in this session."));
            StampRelay(rejection, client.Id);
            await TrySendAsync(client, rejection, token);
            return true;
        }
        else return false;
        if (message.Type == MessageType.SessionCommand && message.GetPayload<SessionCommand>() is { Kind: "Penalty" } penaltyCommand
            && penaltyCommand.Payload.ValueKind == System.Text.Json.JsonValueKind.Object
            && penaltyCommand.Payload.TryGetProperty("incidentCommit", out var linkedCommit) && linkedCommit.ValueKind == System.Text.Json.JsonValueKind.Object)
            Interlocked.Exchange(ref _incidentRefreshRequested, 1);
        if (outcome.Result.Committed) await PublishAuthorityOutcomeAsync(outcome, token);
        else await SendAuthoritySnapshotAsync(client, token);
        var result = ProtocolMessage.Create(MessageType.CommandResult, outcome.Result);
        StampRelay(result, client.Id);
        await TrySendAsync(client, result, token);
        return true;
        }
        finally { _authorityDispatchGate.Release(); }
    }

    public Task<bool> RenewAuthorityAsync(RelayClient client, CancellationToken token = default) =>
        _authority?.RenewAsync(client.UserId, token) ?? Task.FromResult(false);

    public async Task TickAuthorityAsync(bool synchronized, TimeSpan deliveryLead, CancellationToken token = default)
    {
        if (_authority == null) return;
        await _authorityDispatchGate.WaitAsync(token);
        try
        {
        var outcome = await _authority.TickAsync(synchronized, deliveryLead, token);
        if (outcome?.Result.Committed == true) await PublishAuthorityOutcomeAsync(outcome, token);
        else await DispatchAuthorityEventsAsync(token);
        }
        finally { _authorityDispatchGate.Release(); }
    }

    public async Task SendAuthoritySnapshotAsync(RelayClient client, CancellationToken token = default,
        SessionSnapshot? snapshot = null)
    {
        if (_authority == null || !(client.Role == "host" || CanReceiveHostState(client))) return;
        snapshot ??= _authority.Snapshot;
        if (client.Role == "driver") snapshot = AuthorityProjection.ForDriver(snapshot, client.UserId);
        else snapshot = snapshot with { PrivatePanels = null, PrivateTexts = null };
        var message = ProtocolMessage.Create(MessageType.SessionSnapshot, snapshot);
        message.AuthorityGeneration = snapshot.Generation;
        message.ClockEpoch = snapshot.ClockEpoch;
        message.Sequence = snapshot.Revision;
        StampRelay(message, client.Id);
        TrackAuthorityDelivery(client, message);
        TrackPanelAudioDelivery(client, snapshot);
        await TrySendAsync(client, message, token);
    }

    private async Task PublishAuthorityOutcomeAsync(AuthorityOutcome outcome, CancellationToken token)
    {
        RefreshAuthorityMetadata(outcome.Snapshot);
        // Every station, including the historical main HOST, uses the same projection.
        await Task.WhenAll(_clients.Values.Select(client => SendAuthoritySnapshotAsync(client, token, outcome.Snapshot)));
        await DispatchAuthorityEventsAsync(token);
    }

    public async Task SendAuthorityPenaltyHistoryAsync(RelayClient client, CancellationToken token = default,
        AuthorityPenaltyCursor? cursor = null, Guid? requestId = null)
    {
        if (_authority == null || client.Role != "driver") return;
        PenaltyHistorySnapshotPayload payload;
        try
        {
            var page = await _authority.ReadPenaltyHistoryAsync(client.UserId, cursor, token);
            payload = new() { Penalties = page.Penalties.ToList(), NextCursor = page.NextCursor, HistoryComplete = page.NextCursor == null };
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException)
        {
            // Session presentation stays available while the separate history query fails.
            payload = new() { Penalties = _authority.PenaltyHistory(client.UserId).TakeLast(16).ToList(),
                HistoryComplete = false, Error = "Penalty history is temporarily unavailable. Reconnect or retry." };
        }
        payload.RequestId = requestId;
        var history = ProtocolMessage.Create(MessageType.PenaltyHistorySnapshot, payload);
        StampRelay(history, client.Id);
        await TrySendAsync(client, history, token);
    }

    private async Task DispatchAuthorityEventsAsync(CancellationToken token)
    {
        if (_authority?.IsWriterActive != true) return;
        var dispatched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pending in _authority.PendingEvents)
        {
            if (pending.ExpiresAt <= _authority.Now) { dispatched.Add(pending.Message.Id); continue; }
            var message = pending.Message;
            var results = await Task.WhenAll(_clients.Values.Where(client =>
                    (client.Role == "host" || CanReceiveHostState(client))
                    && (message.TargetId == "all" || client.Role != "driver" || client.UserId == message.TargetId))
                .Select(client => SendAuthorityEventAsync(client, message, token)));
            if (results.Length > 0 && results.All(sent => sent)) dispatched.Add(message.Id);
        }
        var marked = await _authority.MarkDispatchedAsync(dispatched, token);
        if (marked?.Result.Committed == true)
        {
            RefreshAuthorityMetadata(marked.Snapshot);
            await Task.WhenAll(_clients.Values.Select(client => SendAuthoritySnapshotAsync(client, token, marked.Snapshot)));
        }
    }

    private Task<bool> SendAuthorityEventAsync(RelayClient client, ProtocolMessage stored, CancellationToken token)
    {
        var message = ProtocolMessage.FromJson(stored.ToJson())!;
        StampRelay(message, client.Id);
        TrackAuthorityDelivery(client, message);
        return TrySendAsync(client, message, token);
    }
}
