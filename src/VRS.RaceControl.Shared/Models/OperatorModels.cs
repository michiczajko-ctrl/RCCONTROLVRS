using System.Text.Json;
using VRS.RaceControl.Shared.Protocol;

namespace VRS.RaceControl.Shared.Models;

[Flags]
public enum OperatorPermissions
{
    None = 0,
    Observe = 1,
    Incidents = 2,
    Control = 4
}

public enum OperatorRole
{
    MainHost,
    Operator,
    Observer,
    Steward
}

public sealed record OperatorSeat(string Id, string Name, OperatorRole Role,
    OperatorPermissions Permissions, DateTime LastHeartbeatUtc, bool IsConnected);

public sealed record OperatorPriorityState(string MainId, string? ControllerId, long Generation,
    long Revision, string? PendingControllerId, IReadOnlyList<OperatorSeat> Operators)
{
    public bool IsReadOnly => ControllerId == null;
}

public sealed record OperatorSnapshotPayload(OperatorPriorityState State);
public sealed record OperatorConnectionRequestPayload(string OperatorId, string ConnectionId,
    string Name, OperatorRole Role, OperatorPermissions MaximumPermissions);
public sealed record OperatorHeartbeatPayload(long KnownRevision);
public sealed record OperatorApprovalPayload(string OperatorId, string Name, OperatorRole Role,
    OperatorPermissions Permissions, bool Approved);
public sealed record OperatorPriorityRequestPayload(Guid RequestId, long KnownGeneration);
public sealed record OperatorPriorityDecisionPayload(Guid RequestId, string OperatorId,
    bool Approved, long Generation);
public sealed record OperatorPriorityTransferPayload(string OperatorId);
public sealed record SessionOwnershipRequestPayload(Guid RequestId, long ExpectedGeneration, string Reason);
public sealed record SessionOwnershipResultPayload(Guid RequestId, bool Accepted, long Generation, string? Error);
public sealed record OperatorCommandPayload(Guid CommandId, long Generation, MessageType CommandType,
    JsonElement? CommandPayload, string TargetId = "all");

public static class ProtocolCapabilities
{
    public const string SessionAuthority = "session-state-v3";
    public const string FleetTelemetry = "telemetry-v2";
    public const string IncidentEvidence = "raceguard-evidence-v1";
    public const string ScheduledPanelAudio = "scheduled-panel-audio-v1";
    public const string SharedTrackDefinitions = "shared-track-definitions-v1";
    public const string DetailedTelemetry = "telemetry-detail-v1";
    public const string AtomicIncidentPenalty = "atomic-incident-penalty-v1";
    public const string AdvancedTelemetryRules = "advanced-telemetry-rules-v1";
    /// <summary>A driver may send its measured track line to the HOST. Old relays disconnect on unknown message types, so clients only send it when the join reply lists this.</summary>
    public const string DriverTrackUpload = "driver-track-upload-v1";
    public const string MultiHostOperators = "multi-host-v1";
    public const string LiveIncidents = "live-incidents-v1";
    public const string SessionStateV2 = "session-state-v2";
    public const string MultiHostV2 = "multi-host-v2";
    public const string DurableIncidentCases = "raceguard-cases-v1";
}


public static class OperatorCommandRules
{
    public static bool IsAllowed(MessageType type) => type is
        MessageType.Flag
        or MessageType.Penalty
        or MessageType.TextMessage
        or MessageType.ClearOverlay
        or MessageType.CustomFlag
        or MessageType.CustomFlagWithdraw
        or MessageType.SessionState
        or MessageType.ForceSystemReset
        or MessageType.ManualOverrideCleared;
}
