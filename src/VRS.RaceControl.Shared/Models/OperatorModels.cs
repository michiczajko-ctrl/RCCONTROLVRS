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
public sealed record OperatorCommandPayload(Guid CommandId, long Generation, MessageType CommandType,
    JsonElement? CommandPayload, string TargetId = "all");

public static class ProtocolCapabilities
{
    public const string MultiHostOperators = "multi-host-v1";
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
