using System.Collections.Concurrent;
using VRS.RaceControl.Shared.Models;
using VRS.RaceControl.Shared.Protocol;

/// <summary>Mode comes from the durable session, never from a joining client's capabilities.</summary>
public sealed partial class RelaySession
{
    private readonly SemaphoreSlim _authorityBootstrapGate = new(1, 1);
    private CancellationTokenSource? _authorityRuntimeStop;
    private Task? _authorityRuntime;
    private Task? _relayWriterRenewal;
    private Task? _authorityDeliveryWriter;
    private Task? _telemetryRulesWorker;
    private Task? _telemetryReportWriter;
    private Task? _telemetryEvidenceWriter;
    private SupabaseAuthorityStore? _configuredAuthorityStore;
    private readonly ConcurrentDictionary<string, AuthorityOperatorApproval> _authorityApprovals = new();
    public bool RequiresAuthority { get; private set; }

    public async Task BootstrapConfiguredAuthorityAsync(SupabaseAuthorityStore store, string authenticatedUserId,
        CancellationToken applicationStopping, CancellationToken token)
    {
        if (!RequiresAuthority) return;
        await _authorityBootstrapGate.WaitAsync(token);
        try
        {
            if (_authority != null) return;
            var creator = _mainUserId ?? throw new InvalidDataException("Session creator is missing.");
            if (await store.LoadAsync(Code, token) == null)
            {
                if (authenticatedUserId != creator)
                    throw new InvalidOperationException("The creator must start a new authority session first.");
                await store.PrepareSessionAsync(Code, authenticatedUserId, token);
            }
            var approvals = await store.LoadApprovalsAsync(Code, token);
            _authorityApprovals.Clear();
            foreach (var approval in approvals)
            {
                if (!Guid.TryParse(approval.UserId, out _) || !RelayRoles.IsSecondaryOperator(approval.Role)
                    || approval.Permissions is < 1 or > 7)
                    throw new InvalidDataException("Invalid durable operator approval.");
                _authorityApprovals[approval.UserId] = approval;
            }
            await AttachAuthorityAsync(creator, store, token);
            _configuredAuthorityStore = store;
            _authorityRuntimeStop = CancellationTokenSource.CreateLinkedTokenSource(applicationStopping);
            _relayWriterRenewal = RunRelayWriterRenewalAsync(_authorityRuntimeStop.Token);
            _authorityRuntime = RunAuthoritySchedulerAsync(_authorityRuntimeStop.Token);
            _authorityDeliveryWriter = RunAuthorityDeliveryWriterAsync(_authorityRuntimeStop.Token);
            _telemetryRulesWorker = RunTelemetryRulesAsync(_authorityRuntimeStop.Token);
            _telemetryReportWriter = RunTelemetryReportWriterAsync(_authorityRuntimeStop.Token);
            _telemetryEvidenceWriter = RunTelemetryEvidenceWriterAsync(_authorityRuntimeStop.Token);
        }
        finally { _authorityBootstrapGate.Release(); }
    }

    private void RestoreAuthorityApproval(RelayClient client)
    {
        if (!_authorityApprovals.TryGetValue(client.UserId, out var approval) || approval.Role != client.Role) return;
        var granted = RelayRoles.ToPermissions(approval.Permissions) & RelayRoles.ToPermissions(client.Permissions);
        _operatorPriority?.RestoreTrustedSeat(client.UserId, client.Name, RelayRoles.ToOperatorRole(client.Role), granted, DateTime.UtcNow);
    }

    private async Task<bool> HandleConfiguredAuthorityApprovalAsync(RelayClient actor, ProtocolMessage message, CancellationToken token)
    {
        if (!RequiresAuthority || message.Type != MessageType.OperatorApproval) return false;
        var snapshot = _authority?.Snapshot;
        if (_configuredAuthorityStore == null || snapshot == null || (actor.UserId != _mainUserId
            && !(snapshot.ControllerId == actor.UserId && snapshot.LeaseExpiresAt > _authority!.Now && HasAuthorityControl(actor)))) return true;
        var request = message.GetPayload<OperatorApprovalPayload>();
        var target = _clients.Values.FirstOrDefault(c => RelayRoles.IsSecondaryOperator(c.Role) && c.UserId == request?.OperatorId);
        if (request == null || target == null) return true;
        var maximum = RelayRoles.ToPermissions(target.Permissions);
        var permissions = request.Approved ? request.Permissions & maximum : maximum;
        if ((int)permissions is < 1 or > 7) return true;
        try
        {
            await _configuredAuthorityStore.SaveApprovalAsync(Code, actor.UserId, target.UserId, target.Role,
                (int)permissions, request.Approved, token);
            if (request.Approved)
            {
                _authorityApprovals[target.UserId] = new(target.UserId, target.Role, (int)permissions);
                RestoreAuthorityApproval(target);
                await SendAuthoritySnapshotAsync(target, token);
                await SendRetainedIncidentStateAsync(target, token);
            }
            else
            {
                _authorityApprovals.TryRemove(target.UserId, out _);
                _operatorPriority?.RestoreTrustedSeat(target.UserId, target.Name, RelayRoles.ToOperatorRole(target.Role),
                    OperatorPermissions.None, DateTime.UtcNow);
            }
            await BroadcastOperatorSnapshotAsync(token);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            var rejected = ProtocolMessage.Create(MessageType.CommandResult,
                new CommandResult(Guid.Empty, "unavailable", snapshot.Generation, snapshot.Revision,
                    "Operator approval outcome is unknown; resync before retrying."));
            StampRelay(rejected, actor.Id);
            await TrySendAsync(actor, rejected, token);
        }
        return true;
    }

    private async Task StopAuthorityRuntimeAsync()
    {
        if (_authorityRuntimeStop == null) return;
        _authorityRuntimeStop.Cancel();
        if (_relayWriterRenewal != null) await _relayWriterRenewal;
        if (_telemetryEvidenceWriter != null) await _telemetryEvidenceWriter;
        if (_telemetryRulesWorker != null) await _telemetryRulesWorker;
        if (_telemetryReportWriter != null) await _telemetryReportWriter;
        if (_authorityDeliveryWriter != null) await _authorityDeliveryWriter;
        if (_authorityRuntime != null) await _authorityRuntime;
    }
}
