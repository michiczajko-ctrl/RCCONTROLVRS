using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using VRS.RaceControl.Shared.Enums;
using VRS.RaceControl.Shared.Models;
using VRS.RaceControl.Shared.Protocol;
using VRS.RaceControl.Shared.Services;

public interface IAuthorityStore
{
    bool SupportsRelayWriterLease => false;
    Task<bool> AcquireRelayWriterAsync(string sessionId, string clockEpoch, CancellationToken token) => Task.FromResult(true);
    Task<bool> RenewRelayWriterAsync(string sessionId, string clockEpoch, CancellationToken token) => Task.FromResult(true);
    bool SupportsOperationHistory => false;
    bool SupportsPenaltyHistory => false;
    bool SupportsDeliveryHistory => false;
    bool SupportsTelemetryEvidence => false;
    bool SupportsTrackDefinitions => false;
    bool SupportsAtomicIncidentPenalty => false;
    Task WriteTrackDefinitionAsync(string sessionId, string clockEpoch, TrackDefinition definition, CancellationToken token)
        => throw new NotSupportedException("Shared track definitions unavailable.");
    Task<TrackDefinitionPage> ReadTrackDefinitionAsync(string sessionId, TrackDefinitionRequest request, CancellationToken token)
        => Task.FromResult(new TrackDefinitionPage(request.RequestId, request.Checksum, null, null, "Shared track definitions unavailable."));
    Task RecoverEvidenceAsync(string sessionId, string clockEpoch, CancellationToken token) => Task.CompletedTask;
    Task WriteEvidenceAsync(string sessionId, string clockEpoch, TelemetryEvidenceClip clip, CancellationToken token) => Task.CompletedTask;
    Task<bool> RecordEvidenceExportAsync(string sessionId, string actorId, string incidentId, string checksum, CancellationToken token)
        => Task.FromResult(false);
    Task<TelemetryEvidencePage> ReadEvidenceAsync(string sessionId, TelemetryEvidenceRequest request, CancellationToken token)
        => Task.FromResult(new TelemetryEvidencePage(request.RequestId, null, [], null, "Evidence storage unavailable."));
    Task WriteDeliveryAsync(string sessionId, AuthorityDelivery delivery, CancellationToken token) => Task.CompletedTask;
    Task<AuthorityPenaltyPage> ReadPenaltyPageAsync(string sessionId, string userId, AuthorityPenaltyCursor? cursor, CancellationToken token)
        => Task.FromResult(new AuthorityPenaltyPage([], null));
    Task<bool> HasOperationAsync(string sessionId, Guid operationId, CancellationToken token) => Task.FromResult(false);
    Task<AuthorityStoredState?> LoadAsync(string sessionId, CancellationToken token);
    // Persist before acknowledging. The store must compare generation AND revision.
    Task<bool> CompareExchangeAsync(AuthorityStoredState? previous, AuthorityStoredState next,
        string actorId, bool system, CancellationToken token);
}

public sealed record AuthorityOutcome(CommandResult Result, SessionSnapshot Snapshot,
    ProtocolMessage? Event = null, IReadOnlyList<ProtocolMessage>? AdditionalEvents = null);

/// <summary>One serialized writer per session. Socket sends never run under this gate.</summary>
public sealed class SessionAuthority
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IAuthorityStore _store;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan> _greenDelay;
    private AuthorityStoredState _stored;
    private bool _initialized;
    public bool StorageAvailable { get; private set; } = true;
    public bool IsWriterActive => _initialized && StorageAvailable && _writerLease.IsHeld;
    private readonly RelayWriterLease _writerLease;
    public Task RenewRelayWriterAsync(CancellationToken token) => _writerLease.RenewAsync(token);
    public string ClockEpoch { get; } = Guid.NewGuid().ToString("N");

    public SessionAuthority(string sessionId, string creatorId, IAuthorityStore store,
        Func<DateTimeOffset>? now = null, Func<TimeSpan>? greenDelay = null)
    {
        _store = store;
        _writerLease = new(store, sessionId, ClockEpoch);
        var started = Stopwatch.GetTimestamp();
        var utc = DateTimeOffset.UtcNow;
        _now = now ?? (() => utc + Stopwatch.GetElapsedTime(started));
        _greenDelay = greenDelay ?? (() => TimeSpan.FromMilliseconds(Random.Shared.Next(2000, 8001)));
        var time = _now();
        _stored = new(new(sessionId, 1, 0, creatorId, time.AddSeconds(8), ClockEpoch, time,
            new() { EpochId = ClockEpoch, HostNow = time, FlashEpochHostTime = time },
            [new("GT3"), new("HYPERCAR")], new()), null, []);
        Stamp(_stored.State.Panel, _stored.State, Guid.NewGuid());
    }

    public DateTimeOffset Now => _now();
    public IReadOnlyList<AuthorityPendingEvent> PendingEvents => Clone(_stored.PendingEvents ?? []);
    public IReadOnlyList<PenaltyPayload> PenaltyHistory(string userId) =>
        Clone((_stored.Penalties ?? []).Where(p => p.Payload.AccountId == userId)
            .Select(p => { var payload = Clone(p.Payload); payload.Note = ""; payload.IncidentCommit = null; return payload; }).ToArray());
    public async Task<AuthorityPenaltyPage> ReadPenaltyHistoryAsync(string userId, AuthorityPenaltyCursor? cursor, CancellationToken token)
    {
        if (cursor?.IsValid == false) throw new ArgumentException("Invalid penalty cursor.", nameof(cursor));
        if (!_store.SupportsPenaltyHistory) return new(PenaltyHistory(userId), null);
        var page = await _store.ReadPenaltyPageAsync(_stored.State.SessionId, userId, cursor, token);
        var result = Clone(page);
        foreach (var penalty in result.Penalties) { penalty.Note = ""; penalty.IncidentCommit = null; }
        return result;
    }
    public SessionSnapshot Snapshot
    {
        get
        {
            var snapshot = Clone(_stored.State with { ServerNow = Now, ClockEpoch = ClockEpoch });
            snapshot.Panel.HostNow = snapshot.ServerNow;
            return snapshot;
        }
    }
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;

    public async Task InitializeAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (_initialized) return;
            await _writerLease.AcquireAsync(token);
            var loaded = await _store.LoadAsync(_stored.State.SessionId, token);
            if (loaded != null)
            {
                _stored = loaded;
                // Restart cannot replay a pending GREEN, nor grant control to an old socket.
                var next = Clone(loaded) with { PendingGreen = null, PendingEvents = [],
                    State = Clone(loaded.State) with { Generation = loaded.State.Generation + 1,
                        Revision = loaded.State.Revision + 1, ControllerId = null, LeaseExpiresAt = null,
                        ClockEpoch = ClockEpoch, ServerNow = Now, GreenArmed = false,
                        StandingStartArmedAt = null, StandingGrid = null } };
                if (next.State.Panel.Transition?.TargetState == FlagType.Green)
                    next.State.Panel.Transition = null;
                else if (next.State.Panel.Transition is { } restoredTransition)
                { restoredTransition.AudioAnnouncement = null; restoredTransition.SuppressAudio = true; }
                Stamp(next.State.Panel, next.State, Guid.NewGuid());
                if (!await _store.CompareExchangeAsync(loaded, next, "", true, token))
                    throw new InvalidOperationException("Authority recovery conflicted with another writer.");
                _stored = next;
            }
            else if (!await _store.CompareExchangeAsync(null, _stored, _stored.State.ControllerId!, false, token))
                throw new InvalidOperationException("Authority initialization conflicted.");
            if (!_writerLease.IsHeld) throw new IOException("Relay writer lease expired during recovery.");
            _initialized = true;
        }
        finally { _gate.Release(); }
    }

    public async Task<AuthorityOutcome> TransferAsync(AuthorityTransfer request, string actorId,
        bool actorCanControl, bool targetCanControl, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (!actorCanControl || !targetCanControl || string.IsNullOrWhiteSpace(request.TargetOperatorId))
                return Reject(request.OperationId, "rejected", "Control permission required.");
            if (!_initialized || !_writerLease.IsHeld) return Reject(request.OperationId, "unavailable", "Relay writer is unavailable.");
            if (!StorageAvailable && !await RefreshDurableStateAsync(token))
                return Reject(request.OperationId, "unavailable", "Durable state is unavailable; retry the same operation id.");
            var duplicate = await DuplicateAsync(request.OperationId, token);
            if (duplicate == null) return Reject(request.OperationId, "unavailable", "Operation history is temporarily unavailable.");
            if (duplicate.Value) return Reject(request.OperationId, "duplicate", null);
            if (request.OperationId == Guid.Empty || request.Generation != _stored.State.Generation
                || request.ExpectedRevision != _stored.State.Revision)
                return Reject(request.OperationId, "conflict", "Generation or revision changed.");
            if (HasLease && actorId != _stored.State.ControllerId)
                return Reject(request.OperationId, "rejected", "Only the controller may transfer a live lease.");
            if (!HasLease && actorId != request.TargetOperatorId)
                return Reject(request.OperationId, "rejected", "Takeover must claim the actor's own seat.");
            if (_stored.PendingGreen is { Committed: true } green && Now < green.TargetAt)
                return Reject(request.OperationId, "conflict", "GREEN is already committed; wait for execution.");
            var next = Clone(_stored) with { PendingGreen = null,
                State = Clone(_stored.State) with { ControllerId = request.TargetOperatorId,
                    LeaseExpiresAt = Now.AddSeconds(8), Generation = _stored.State.Generation + 1,
                    StandingStartArmedAt = null, StandingGrid = null } };
            return await CommitAsync(next, request.OperationId, actorId, false, null, token);
        }
        finally { _gate.Release(); }
    }

    private bool HasLease => _stored.State.ControllerId != null && _stored.State.LeaseExpiresAt > Now;
    private async Task<bool?> DuplicateAsync(Guid id, CancellationToken token)
    {
        if (id == Guid.Empty) return false;
        if (_stored.OperationIds.Contains(id)) return true;
        if (!_store.SupportsOperationHistory) return false;
        try { return await _store.HasOperationAsync(_stored.State.SessionId, id, token); }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException)
        { StorageAvailable = false; return null; }
    }
    private AuthorityOutcome Reject(Guid id, string result, string? error) =>
        new(new(id, result, _stored.State.Generation, _stored.State.Revision, error), Snapshot);

    public async Task<bool> RenewAsync(string actorId, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (!_initialized || !_writerLease.IsHeld || (!StorageAvailable && !await RefreshDurableStateAsync(token))
                || !HasLease || actorId != _stored.State.ControllerId) return false;
            var next = Clone(_stored) with { State = _stored.State with { LeaseExpiresAt = Now.AddSeconds(8) } };
            if (!await _store.CompareExchangeAsync(_stored, next, actorId, false, token))
            { await RefreshDurableStateAsync(token); return false; }
            _stored = next;
            StorageAvailable = true;
            return true;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException)
        { StorageAvailable = false; return false; }
        finally { _gate.Release(); }
    }

    public async Task<AuthorityOutcome> ExecuteAsync(SessionCommand request, string actorId,
        bool canControl, CancellationToken token = default, AuthorityRecipient? recipient = null, string? preconditionError = null)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (!_initialized || !_writerLease.IsHeld) return Reject(request.OperationId, "unavailable", "Relay writer is unavailable.");
            if (!StorageAvailable && !await RefreshDurableStateAsync(token))
                return Reject(request.OperationId, "unavailable", "Durable state is unavailable; retry the same operation id.");
            if (!canControl) return Reject(request.OperationId, "rejected", "Control permission required.");
            if (request.SessionId != _stored.State.SessionId || request.OperationId == Guid.Empty)
                return Reject(request.OperationId, "rejected", "Invalid session or operation id.");
            // A former controller may retrieve a committed retry, but cannot issue a new command.
            var duplicate = await DuplicateAsync(request.OperationId, token);
            if (duplicate == null) return Reject(request.OperationId, "unavailable", "Operation history is temporarily unavailable.");
            if (duplicate.Value) return Reject(request.OperationId, "duplicate", null);
            if (!HasLease || actorId != _stored.State.ControllerId || request.Generation != _stored.State.Generation)
                return Reject(request.OperationId, "rejected", "Controller lease or generation is stale.");
            if (request.ExpectedRevision != _stored.State.Revision)
                return Reject(request.OperationId, "conflict", "State revision changed; resync required.");
            if (preconditionError != null) return Reject(request.OperationId, "rejected", preconditionError);
            var next = Clone(_stored);
            ProtocolMessage? message = null;
            var isPrivate = request.TargetId != "all";
            if (isPrivate && (recipient == null || recipient.ConnectionId != request.TargetId
                || string.IsNullOrWhiteSpace(recipient.UserId)))
                return Reject(request.OperationId, "rejected", "Recipient is not an authenticated driver in this session.");
            if (request.Kind == "green.arm")
            {
                if (isPrivate) return Reject(request.OperationId, "rejected", "GREEN arming is session-wide.");
                if (next.State.Panel.FlagState != FlagType.ReadyForGreen || next.PendingGreen != null)
                    return Reject(request.OperationId, "rejected", "READY is required and GREEN must not already be armed.");
                var delay = _greenDelay();
                if (delay < TimeSpan.FromSeconds(2) || delay > TimeSpan.FromSeconds(8))
                    return Reject(request.OperationId, "rejected", "GREEN delay must be between 2 and 8 seconds.");
                next = next with { PendingGreen = new(request.OperationId, Now + delay) };
            }
            else if (request.Kind == "green.cancel")
            {
                if (isPrivate) return Reject(request.OperationId, "rejected", "GREEN cancellation is session-wide.");
                if (next.PendingGreen?.Committed == true)
                    return Reject(request.OperationId, "rejected", "Use RED to override committed GREEN.");
                next = next with { PendingGreen = null };
            }
            else if (request.Kind == "policy")
            {
                if (isPrivate) return Reject(request.OperationId, "rejected", "Policy is session-wide.");
                var policy = request.Payload.Deserialize<SpeedingPolicy>(Json);
                if (policy?.IsValid != true) return Reject(request.OperationId, "rejected", "Invalid speeding policy.");
                next = next with { State = next.State with { Policy = policy,
                    PolicyRevision = next.State.PolicyRevision + 1 } };
            }
            else if (request.Kind == "track-definition")
            {
                if (isPrivate || !_store.SupportsTrackDefinitions)
                    return Reject(request.OperationId, "rejected", "Shared track definition storage unavailable.");
                var definition = request.Payload.Deserialize<TrackDefinition>(Json);
                if (definition == null || definition.Validate() != null || !definition.Verified || definition.Spline.Count > 1000
                    || request.Payload.GetRawText().Length > 150000)
                    return Reject(request.OperationId, "rejected", "Invalid or unverified measured track definition.");
                // Immutable asset is durable before its reference commits. A failed CAS leaves only an unreferenced asset.
                await _store.WriteTrackDefinitionAsync(next.State.SessionId, next.State.ClockEpoch, definition, token);
                var reference = TrackDefinitionCodec.Reference(definition);
                next = next with { State = next.State with { TrackDefinition = reference,
                    TrackLayoutBinding = next.State.TrackDefinition?.Checksum == reference.Checksum ? next.State.TrackLayoutBinding : null } };
            }
            else if (request.Kind == "track.confirm-layout")
            {
                var binding = request.Payload.Deserialize<TrackLayoutBinding>(Json);
                if (isPrivate || binding?.IsValid != true || next.State.TrackDefinition?.Checksum != binding.Checksum)
                    return Reject(request.OperationId, "rejected", "Layout confirmation requires the current measured track asset.");
                next = next with { State = next.State with { TrackLayoutBinding = binding } };
            }
            else if (request.Kind == "advanced-policy")
            {
                if (isPrivate) return Reject(request.OperationId, "rejected", "Rule policy is session-wide.");
                var policy = request.Payload.Deserialize<AdvancedTelemetryPolicy>(Json);
                if (policy?.IsValid != true) return Reject(request.OperationId, "rejected", "Invalid rule policy.");
                next = next with { State = next.State with { AdvancedPolicy = policy,
                    AdvancedPolicyRevision = next.State.AdvancedPolicyRevision + 1, StandingStartArmedAt = null, StandingGrid = null } };
            }
            else if (request.Kind is "standing-start.arm" or "standing-start.cancel")
            {
                if (isPrivate || (request.Kind == "standing-start.arm"
                    && (next.State.AdvancedPolicy?.JumpStart != true || next.State.Panel.FlagState != FlagType.ReadyForGreen
                        || next.State.StandingStartArmedAt != null || next.PendingGreen?.Committed == true)))
                    return Reject(request.OperationId, "rejected", "Standing start requires enabled jump-start monitoring and READY FOR GREEN.");
                var grid = request.Kind == "standing-start.arm" ? request.Payload.Deserialize<StandingStartGrid>(Json) : null;
                if (request.Kind == "standing-start.arm" && grid?.IsValid != true)
                    return Reject(request.OperationId, "rejected", "Standing start requires a Relay-captured stationary grid.");
                next = next with { State = next.State with { StandingStartArmedAt =
                    request.Kind == "standing-start.arm" ? Now : null, StandingGrid = grid } };
            }
            else if (request.Kind == "impact-policy")
            {
                if (isPrivate) return Reject(request.OperationId, "rejected", "Impact policy is session-wide.");
                var policy = request.Payload.Deserialize<ImpactDetectionPolicy>(Json);
                if (policy?.IsValid != true) return Reject(request.OperationId, "rejected", "Invalid impact detection policy.");
                next = next with { State = next.State with { ImpactPolicy = policy,
                    ImpactPolicyRevision = next.State.ImpactPolicyRevision
                        + (policy == (next.State.ImpactPolicy ?? new ImpactDetectionPolicy()) ? 0 : 1) } };
            }
            else if (request.Kind == nameof(MessageType.Flag))
            {
                var flag = request.Payload.Deserialize<FlagPayload>(Json);
                if (flag == null || !Enum.IsDefined(flag.FlagType) || !Enum.IsDefined(flag.FlashMode) || flag.Message == null || flag.Message.Length > 500
                    || flag.DisplayDurationMs is < 0 or > 120000)
                    return Reject(request.OperationId, "rejected", "Invalid flag.");
                if (isPrivate)
                {
                    if (AuthorityProjection.IsCritical(flag.FlagType))
                        return Reject(request.OperationId, "rejected", "Critical flags must be session-wide.");
                    if (flag.FlagType == FlagType.Green && (AuthorityProjection.IsCritical(next.State.Panel.FlagState)
                        || next.State.Panel.Transition != null))
                        return Reject(request.OperationId, "rejected", "Private GREEN cannot override a session-wide safety condition.");
                    var panels = new Dictionary<string, RaceControlPanelStatePayload>(next.State.PrivatePanels ?? new Dictionary<string, RaceControlPanelStatePayload>());
                    if (flag.FlagType is FlagType.Green or FlagType.None) panels.Remove(recipient!.UserId);
                    else
                    {
                        var instruction = Clone(next.State.Panel);
                        instruction.FlagState = flag.FlagType;
                        instruction.FlashMode = flag.FlashMode;
                        instruction.Transition = null;
                        instruction.FlashEpochHostTime = Now;
                        panels[recipient!.UserId] = instruction;
                    }
                    next = next with { State = next.State with { PrivatePanels = panels } };
                    message = ProtocolMessage.Create(MessageType.Flag, flag);
                }
                else
                {
                var previousFlag = next.State.Panel.FlagState;
                next.State.Panel.FlagState = flag.FlagType;
                next.State.Panel.FlashMode = flag.FlashMode;
                next.State.Panel.FlashEpochHostTime = Now;
                next.State.Panel.Transition = null;
                next = next with { PendingGreen = null };
                next = next with { State = next.State with { PrivatePanels = null,
                    StandingStartArmedAt = null, StandingGrid = null } };
                if (flag.FlagType == FlagType.FullCourseYellow)
                {
                    var effectiveAt = Now.AddMilliseconds(8720);
                    next.State.Panel.FlagState = previousFlag;
                    next.State.Panel.Transition = new() { TransitionId = request.OperationId.ToString("N"),
                        SourceState = previousFlag, TargetState = FlagType.FullCourseYellow,
                        CueStartsAtHostTime = Now.AddSeconds(2), CountdownStartsAtHostTime = effectiveAt.AddSeconds(-5),
                        TargetEffectiveAtHostTime = effectiveAt };
                    next = next with { State = next.State with { FcyPeriodId = request.OperationId.ToString("N"), FcyActiveAt = effectiveAt } };
                }
                else if (flag.FlagType != FlagType.ReadyForGreen)
                    next = next with { State = next.State with { FcyPeriodId = null, FcyActiveAt = null } };
                if (flag.FlagType == FlagType.Red)
                { next.State.Panel.FastLaneActive = false; next.State.Panel.FastLaneState = FastLaneState.Closed; }
                flag.PanelState = next.State.Panel;
                flag.PanelTransitionOwnsAudio = next.State.Panel.Transition != null;
                message = ProtocolMessage.Create(MessageType.Flag, flag);
                }
            }
            else if (request.Kind == nameof(MessageType.TextMessage))
            {
                var text = request.Payload.Deserialize<TextMessagePayload>(Json);
                if (text == null || string.IsNullOrWhiteSpace(text.Text) || text.Text.Length > 2000
                    || text.DisplayDurationMs is < 1 or > 120000)
                    return Reject(request.OperationId, "rejected", "Invalid text announcement.");
                var active = new AuthorityText(text.Text, Now.AddMilliseconds(text.DisplayDurationMs), text.DisplayDurationMs,
                    request.OperationId.ToString("N"));
                if (isPrivate)
                {
                    var texts = new Dictionary<string, AuthorityText>(next.State.PrivateTexts ?? new Dictionary<string, AuthorityText>());
                    texts[recipient!.UserId] = active;
                    next = next with { State = next.State with { PrivateTexts = texts } };
                }
                else next = next with { State = next.State with { ActiveText = active, PrivateTexts = null } };
                message = ProtocolMessage.Create(MessageType.TextMessage, text);
            }
            else if (request.Kind == nameof(MessageType.Penalty))
            {
                var penalty = request.Payload.Deserialize<PenaltyPayload>(Json);
                if (!isPrivate || penalty == null || !Enum.IsDefined(penalty.PenaltyType) || penalty.PenaltyType == PenaltyType.None
                    || penalty.Reason == null || penalty.Reason.Length > 500 || penalty.Note == null || penalty.Note.Length > 2000
                    || penalty.DriverNumber == null || penalty.DriverNumber.Length > 32
                    || penalty.DurationSeconds is < 0 or > 86400 || penalty.DisplayDurationMs is < 0 or > 120000
                    || penalty.IncidentId?.Length > 80 || (penalty.IncidentCommit is { } commit
                        && (!commit.IsValid || penalty.IncidentId != commit.CaseId
                            || penalty.PenaltyType is PenaltyType.Warning or PenaltyType.Investigation or PenaltyType.NoFurtherAction))
                    || (penalty.IncidentId == null) != (penalty.IncidentCommit == null))
                    return Reject(request.OperationId, "rejected", "Penalty requires a valid authenticated driver and payload.");
                if (penalty.IncidentCommit != null && !_store.SupportsAtomicIncidentPenalty)
                    return Reject(request.OperationId, "unavailable", "Atomic incident penalty storage is unavailable.");
                penalty.AccountId = recipient!.UserId;
                penalty.DriverName = recipient.DriverName;
                penalty.Id = request.OperationId.ToString("N");
                penalty.CreatedAtUtc = Now.UtcDateTime;
                penalty.SessionCode = next.State.SessionId;
                next = next with { Penalties = (next.Penalties ?? []).Append(new(actorId, Clone(penalty))).ToArray() };
                // Investigation notes remain in durable storage, never on the driver's wire.
                penalty.Note = "";
                penalty.IncidentCommit = null;
                message = ProtocolMessage.Create(MessageType.Penalty, penalty);
            }
            else if (request.Kind == nameof(MessageType.ClearOverlay))
            {
                if (isPrivate)
                {
                    var panels = new Dictionary<string, RaceControlPanelStatePayload>(next.State.PrivatePanels ?? new Dictionary<string, RaceControlPanelStatePayload>());
                    var texts = new Dictionary<string, AuthorityText>(next.State.PrivateTexts ?? new Dictionary<string, AuthorityText>());
                    panels.Remove(recipient!.UserId); texts.Remove(recipient.UserId);
                    next = next with { State = next.State with { PrivatePanels = panels, PrivateTexts = texts } };
                }
                else
                {
                next.State.Panel.FlagState = FlagType.None;
                next.State.Panel.Transition = null;
                next = next with { PendingGreen = null, State = next.State with {
                    ActiveText = null, PrivatePanels = null, PrivateTexts = null, FcyPeriodId = null, FcyActiveAt = null } };
                }
                message = ProtocolMessage.Create(MessageType.ClearOverlay, new { reason = "Cleared by Race Control" });
            }
            else if (request.Kind == nameof(MessageType.ManualOverrideCleared))
            {
                if (isPrivate) return Reject(request.OperationId, "rejected", "Monitoring mode is session-wide.");
                // Recovery of monitoring must never change the track condition.
                next = next with { State = next.State with { ManualMonitoring = false } };
                message = ProtocolMessage.Create(MessageType.ManualOverrideCleared, new { timestamp = Now });
            }
            else if (request.Kind == nameof(MessageType.ForceSystemReset))
            {
                if (isPrivate) return Reject(request.OperationId, "rejected", "Monitoring mode is session-wide.");
                next = next with { State = next.State with { ManualMonitoring = true } };
                message = ProtocolMessage.Create(MessageType.ForceSystemReset, new { timestamp = Now, reason = "Manual monitoring by Race Control" });
            }
            else if (request.Kind == "fast-lane")
            {
                if (isPrivate || request.Payload.ValueKind != JsonValueKind.Object
                    || !request.Payload.TryGetProperty("state", out var stateValue)
                    || stateValue.ValueKind != JsonValueKind.Number || !stateValue.TryGetInt32(out var stateNumber)
                    || !Enum.IsDefined((FastLaneState)stateNumber))
                    return Reject(request.OperationId, "rejected", "Invalid session-wide Fast Lane state.");
                if (next.State.Panel.FlagState == FlagType.Red && (FastLaneState)stateNumber != FastLaneState.Closed)
                    return Reject(request.OperationId, "rejected", "Fast Lane must remain closed under RED.");
                next.State.Panel.FastLaneState = (FastLaneState)stateNumber;
                next.State.Panel.FastLaneActive = true;
                next.State.Panel.FastLaneTransition = null;
            }
            else if (request.Kind == nameof(MessageType.CustomFlagWithdraw))
            {
                var withdrawal = request.Payload.Deserialize<CustomFlagWithdrawPayload>(Json);
                if (withdrawal == null || string.IsNullOrWhiteSpace(withdrawal.Code) || withdrawal.Code.Length > 12)
                    return Reject(request.OperationId, "rejected", "Invalid custom flag withdrawal.");
                message = ProtocolMessage.Create(MessageType.CustomFlagWithdraw, withdrawal);
            }
            else if (request.Kind == nameof(MessageType.CustomFlag))
            {
                var custom = request.Payload.Deserialize<CustomFlagPayload>(Json);
                if (custom?.Definition?.Validate() != null || custom?.Definition?.Enabled != true)
                    return Reject(request.OperationId, "rejected", "Invalid custom announcement.");
                var code = custom?.Definition?.Code?.ToUpperInvariant();
                if (isPrivate ? !custom!.Definition.CanSendToDriver : !custom!.Definition.CanSendGlobally)
                    return Reject(request.OperationId, "rejected", "Custom flag does not allow this target.");
                if (code is "QSTART-GT3" or "QSTART-HY" or "FLOPEN-GT3" or "FLOPEN-HY" or "QEND-GT3" or "QEND-HY")
                {
                if (isPrivate) return Reject(request.OperationId, "rejected", "Qualifying commands must be session-wide.");
                var raceClass = code.EndsWith("GT3", StringComparison.Ordinal) ? "GT3" : "HYPERCAR";
                var classes = next.State.Qualifying.ToArray();
                var index = Array.FindIndex(classes, item => item.RaceClass == raceClass);
                var quali = classes[index];
                if (code.StartsWith("QSTART", StringComparison.Ordinal))
                    classes[index] = quali with { Phase = "Running", EndsAt = quali.Phase == "Running" ? quali.EndsAt : Now.AddSeconds(quali.DurationSeconds) };
                else if (code.StartsWith("QEND", StringComparison.Ordinal))
                    classes[index] = quali with { Phase = "Ended", EndsAt = null };
                else if (quali.Phase != "Running") classes[index] = quali with { Phase = "Armed", EndsAt = null };
                next.State.Panel.FastLaneActive = !code.StartsWith("QEND", StringComparison.Ordinal);
                next.State.Panel.FastLaneState = code.StartsWith("FLOPEN", StringComparison.Ordinal) ? FastLaneState.Open : FastLaneState.Closed;
                // Same exclusions as the automatic expiry in TickAsync: ending qualifying must not replace READY FOR GREEN
                // or a transition that is already scheduled.
                if (code.StartsWith("QEND", StringComparison.Ordinal) && next.State.Panel.FlagState
                    is not (FlagType.Red or FlagType.FullCourseYellow or FlagType.SafetyCar or FlagType.VirtualSafetyCar or FlagType.ReadyForGreen)
                    && next.State.Panel.Transition == null)
                    next.State.Panel.FlagState = FlagType.Checkered;
                next = next with { State = next.State with { Qualifying = classes } };
                }
                message = ProtocolMessage.Create(MessageType.CustomFlag, custom!);
            }
            else return Reject(request.OperationId, "rejected", "Unsupported semantic command.");
            if (message != null) message.TargetId = isPrivate ? recipient!.UserId : "all";
            return await CommitAsync(next, request.OperationId, actorId, false, message, token);
        }
        catch (JsonException) { return Reject(request.OperationId, "rejected", "Malformed command payload."); }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException)
        { return Reject(request.OperationId, "unavailable", "Durable command dependency unavailable; retry the same operation id."); }
        finally { _gate.Release(); }
    }

    public async Task<AuthorityOutcome?> TickAsync(bool synchronized, TimeSpan deliveryLead,
        CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (!_initialized || !_writerLease.IsHeld) return null;
            if (!StorageAvailable && !await RefreshDurableStateAsync(token)) return null;
            var next = Clone(_stored);
            var changed = false;
            ProtocolMessage? message = null;
            var additionalEvents = new List<ProtocolMessage>();
            if (next.State.ActiveText is { } activeText && activeText.ExpiresAt <= Now)
            {
                next = next with { State = next.State with { ActiveText = null } };
                changed = true;
            }
            if (next.State.PrivateTexts?.Any(p => p.Value.ExpiresAt <= Now) == true)
            {
                next = next with { State = next.State with { PrivateTexts = next.State.PrivateTexts
                    .Where(p => p.Value.ExpiresAt > Now).ToDictionary(p => p.Key, p => p.Value) } };
                changed = true;
            }
            if (next.State.ControllerId != null && !HasLease)
            {
                next = next with { State = next.State with { ControllerId = null, LeaseExpiresAt = null,
                    Generation = next.State.Generation + 1 },
                    PendingGreen = next.PendingGreen?.Committed == true ? next.PendingGreen : null };
                changed = true;
            }
            if (next.PendingGreen is { Committed: false } pending
                && Now >= pending.TargetAt - deliveryLead)
            {
                if (!synchronized || deliveryLead > TimeSpan.FromMilliseconds(1500)
                    || Now >= pending.TargetAt || next.State.ControllerId == null)
                    next = next with { PendingGreen = null };
                else
                {
                    next.State.Panel.Transition = new() { TransitionId = pending.OperationId.ToString("N"),
                        SourceState = FlagType.ReadyForGreen, TargetState = FlagType.Green,
                        CueStartsAtHostTime = Now, CountdownStartsAtHostTime = Now,
                        TargetEffectiveAtHostTime = pending.TargetAt, HideCountdown = true };
                    next = next with { PendingGreen = pending with { Committed = true } };
                }
                changed = true;
            }
            if (next.State.Panel.Transition is { } transition && Now >= transition.TargetEffectiveAtHostTime)
            {
                next.State.Panel.FlagState = transition.TargetState;
                next.State.Panel.Transition = null;
                next = next with { PendingGreen = null };
                if (transition.TargetState == FlagType.Green)
                    next = next with { State = next.State with { FcyPeriodId = null, FcyActiveAt = null } };
                message = ProtocolMessage.Create(MessageType.Flag, new FlagPayload { FlagType = transition.TargetState,
                    PanelState = next.State.Panel, PanelTransitionOwnsAudio = true });
                changed = true;
            }
            var quali = next.State.Qualifying.ToArray();
            for (var index = 0; index < quali.Length; index++)
                if (quali[index].Phase == "Running" && quali[index].EndsAt <= Now)
                {
                    quali[index] = quali[index] with { Phase = "Ended", EndsAt = null };
                    next.State.Panel.FastLaneActive = false;
                    next.State.Panel.FastLaneState = FastLaneState.Closed;
                    if (next.State.Panel.FlagState is not (FlagType.Red or FlagType.FullCourseYellow
                        or FlagType.SafetyCar or FlagType.VirtualSafetyCar or FlagType.ReadyForGreen)
                        && next.State.Panel.Transition == null)
                        next.State.Panel.FlagState = FlagType.Checkered;
                    var isGt3 = quali[index].RaceClass == "GT3";
                    additionalEvents.Add(ProtocolMessage.Create(MessageType.CustomFlag, new CustomFlagPayload {
                        Definition = new() { Code = isGt3 ? "QEND-GT3" : "QEND-HY",
                            Name = isGt3 ? "QUALI ENDED FOR GT3" : "QUALI ENDED FOR HYPERCAR",
                            DriverMessage = isGt3 ? "QUALI ENDED FOR GT3" : "QUALI ENDED FOR HYPERCAR" } }));
                    changed = true;
                }
            next = next with { State = next.State with { Qualifying = quali } };
            return changed ? await CommitAsync(next, Guid.NewGuid(), "", true, message, token, additionalEvents) : null;
        }
        finally { _gate.Release(); }
    }

    private static void Stamp(RaceControlPanelStatePayload panel, SessionSnapshot state, Guid id)
    {
        panel.EventId = id.ToString("N"); panel.EpochId = state.ClockEpoch;
        panel.ClockEpoch = state.ClockEpoch; panel.AuthorityGeneration = state.Generation;
        panel.Revision = state.Revision; panel.HostNow = state.ServerNow;
        if (panel.Transition is { SuppressAudio: false } transition && transition.AudioAnnouncement == null)
            transition.AudioAnnouncement = new(transition.TransitionId + ":cue", state.Generation, state.Revision, state.ClockEpoch);
    }

    // Called only under _gate. A different clock epoch belongs to another Relay writer.
    private async Task<bool> RefreshDurableStateAsync(CancellationToken token)
    {
        if (!_writerLease.IsHeld) return false;
        try
        {
            var durable = await _store.LoadAsync(_stored.State.SessionId, token);
            if (durable == null || durable.State.ClockEpoch != ClockEpoch
                || durable.State.Generation < _stored.State.Generation || durable.State.Revision < _stored.State.Revision)
            {
                _initialized = false;
                StorageAvailable = false;
                return false;
            }
            _stored = Clone(durable);
            StorageAvailable = true;
            return true;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException)
        { StorageAvailable = false; return false; }
    }

    /// <summary>Transport dispatch only; this is never a confirmation of driver playback/application.</summary>
    public async Task<AuthorityOutcome?> MarkDispatchedAsync(IReadOnlySet<string> messageIds, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (!_initialized || (!StorageAvailable && !await RefreshDurableStateAsync(token))) return null;
            var pending = _stored.PendingEvents ?? [];
            var retained = pending.Where(e => !messageIds.Contains(e.Message.Id) && e.ExpiresAt > Now).ToArray();
            if (retained.Length == pending.Count) return null;
            return await CommitAsync(Clone(_stored) with { PendingEvents = retained }, Guid.NewGuid(), "", true, null, token);
        }
        finally { _gate.Release(); }
    }

    private async Task<AuthorityOutcome> CommitAsync(AuthorityStoredState next, Guid id,
        string actorId, bool system, ProtocolMessage? message, CancellationToken token,
        IReadOnlyList<ProtocolMessage>? additionalEvents = null)
    {
        if (!_writerLease.IsHeld) return Reject(id, "unavailable", "Relay writer lease expired; restart and resync required.");
        if (next.State.Panel.FlagState != FlagType.ReadyForGreen || next.State.ControllerId == null)
            next = next with { State = next.State with { StandingStartArmedAt = null, StandingGrid = null } };
        next = next with { State = next.State with { Revision = _stored.State.Revision + 1,
            ServerNow = Now, ClockEpoch = ClockEpoch, GreenArmed = next.PendingGreen != null },
            OperationIds = (_store.SupportsOperationHistory ? _stored.OperationIds.TakeLast(255) : _stored.OperationIds).Append(id).ToArray() };
        Stamp(next.State.Panel, next.State, id);
        if (_store.SupportsPenaltyHistory) next = next with { Penalties = (next.Penalties ?? []).TakeLast(16).ToArray() };
        if (message != null)
        {
            message.Id = id.ToString("N");
            var target = message.TargetId;
            if (message.Type == MessageType.Flag)
            {
                var payload = message.GetPayload<FlagPayload>()!;
                payload.PanelState = target == "all" ? Clone(next.State.Panel)
                    : AuthorityProjection.ForDriver(next.State, target).Panel;
                message = ProtocolMessage.Create(MessageType.Flag, payload);
                message.Id = id.ToString("N"); message.TargetId = target;
            }
        }
        var events = new List<ProtocolMessage>();
        if (message != null) events.Add(message);
        if (additionalEvents != null)
            for (var index = 0; index < additionalEvents.Count; index++)
            {
                additionalEvents[index].Id = $"{id:N}:{index}";
                events.Add(additionalEvents[index]);
            }
        foreach (var e in events)
        {
            e.Sequence = next.State.Revision;
            e.Timestamp = next.State.ServerNow.UtcDateTime;
            e.SessionId = next.State.SessionId;
            e.AuthorityGeneration = next.State.Generation;
            e.ClockEpoch = next.State.ClockEpoch;
            e.AnnouncementPriority = RaceControlAnnouncementPolicy.Priority(e);
            e.ExpiresAt = e.Type == MessageType.TextMessage
                ? Now.AddMilliseconds(e.GetPayload<TextMessagePayload>()!.DisplayDurationMs)
                : Now.AddSeconds(30);
        }
        // The event and the state change share the same durable transaction.
        var dispatches = (next.PendingEvents ?? []).Where(e => e.ExpiresAt > Now
                && !events.Any(newEvent => (newEvent.TargetId == "all" || newEvent.TargetId == e.Message.TargetId)
                    && (newEvent.Type == MessageType.ClearOverlay && e.Message.Type != MessageType.Penalty
                    || newEvent.Type == MessageType.Flag && e.Message.Type == MessageType.Flag
                    || newEvent.Type == MessageType.TextMessage && e.Message.Type == MessageType.TextMessage)))
            .Concat(events.Select(e => new AuthorityPendingEvent(e, e.Type == MessageType.TextMessage
                ? Now.AddMilliseconds(e.GetPayload<TextMessagePayload>()!.DisplayDurationMs)
                : Now.AddSeconds(30)))).ToArray();
        if (dispatches.Length > 128)
            return Reject(id, "unavailable", "Announcement dispatch backlog is full; state has not changed.");
        next = next with { PendingEvents = dispatches };
        try
        {
            if (!await _store.CompareExchangeAsync(_stored, next, actorId, system, token))
            {
                await RefreshDurableStateAsync(token);
                return Reject(id, "conflict", "Durable state changed; resync required.");
            }
            _stored = next;
            StorageAvailable = true;
            return new(new(id, "committed", next.State.Generation, next.State.Revision), Snapshot, message, additionalEvents);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException)
        { StorageAvailable = false; return Reject(id, "unavailable", "Durable outcome unavailable; retry the same operation id."); }
    }
}
