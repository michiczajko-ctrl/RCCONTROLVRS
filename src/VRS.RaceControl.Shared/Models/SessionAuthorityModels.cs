using System.Text.Json;
using VRS.RaceControl.Shared.Enums;
using VRS.RaceControl.Shared.Protocol;

namespace VRS.RaceControl.Shared.Models;

public sealed record SessionCommand(Guid OperationId, string SessionId, long Generation,
    long ExpectedRevision, string Kind, JsonElement Payload, string TargetId = "all");
public sealed record CommandResult(Guid OperationId, string Result, long Generation,
    long Revision, string? Error = null)
{
    public bool Committed => Result is "committed" or "duplicate";
}
public sealed record AuthorityTransfer(Guid OperationId, long Generation, long ExpectedRevision,
    string TargetOperatorId);
public sealed record AuthorityQualifying(string RaceClass, string Phase = "Inactive",
    DateTimeOffset? EndsAt = null, int DurationSeconds = 900);
public sealed record AuthorityText(string Text, DateTimeOffset ExpiresAt, int DisplayDurationMs, string? EventId = null);
/// <summary>Resolved by Relay from an authenticated driver connection, never from a display name.</summary>
public sealed record AuthorityRecipient(string ConnectionId, string UserId, string DriverName);
public sealed record AuthorityPenalty(string IssuedBy, PenaltyPayload Payload);
public sealed record AuthorityPenaltyCursor(DateTimeOffset CreatedAt, Guid PenaltyId)
{
    public bool IsValid => CreatedAt != default && PenaltyId != Guid.Empty;
}
public sealed record AuthorityPenaltyPage(IReadOnlyList<PenaltyPayload> Penalties, AuthorityPenaltyCursor? NextCursor);
public sealed record AuthorityPenaltyHistoryRequest(AuthorityPenaltyCursor? Cursor = null, Guid RequestId = default);
public sealed record ScheduledGreen(Guid OperationId, DateTimeOffset TargetAt, bool Committed = false);
public sealed record SessionSnapshot(string SessionId, long Generation, long Revision,
    string? ControllerId, DateTimeOffset? LeaseExpiresAt, string ClockEpoch,
    DateTimeOffset ServerNow, RaceControlPanelStatePayload Panel,
    IReadOnlyList<AuthorityQualifying> Qualifying, SpeedingPolicy Policy,
    string? FcyPeriodId = null, DateTimeOffset? FcyActiveAt = null,
    IReadOnlyDictionary<string, RaceControlPanelStatePayload>? PrivatePanels = null,
    AuthorityText? ActiveText = null,
    IReadOnlyDictionary<string, AuthorityText>? PrivateTexts = null,
    bool ManualMonitoring = false, bool GreenArmed = false, long PolicyRevision = 0,
    ImpactDetectionPolicy? ImpactPolicy = null, long ImpactPolicyRevision = 0,
    TrackDefinitionReference? TrackDefinition = null, TrackLayoutBinding? TrackLayoutBinding = null);
public sealed record AuthorityStoredState(SessionSnapshot State,
    ScheduledGreen? PendingGreen, IReadOnlyList<Guid> OperationIds,
    IReadOnlyList<AuthorityPendingEvent>? PendingEvents = null,
    IReadOnlyList<AuthorityPenalty>? Penalties = null);
public sealed record AuthorityPendingEvent(ProtocolMessage Message, DateTimeOffset ExpiresAt);
/// <summary>StateApplied confirms the shared projection, not paint completion or audio playback.</summary>
public sealed record AuthorityReceipt(string MessageId, long Generation, long Revision,
    string ClockEpoch, string Result);
public sealed record AuthorityPlaybackReceipt(string MessageId, long Generation, long Revision,
    string ClockEpoch, string Status, string? Reason = null);
public sealed record AuthorityDelivery(string MessageId, string ConnectionId, string UserId,
    long Generation, long Revision, string ClockEpoch, DateTimeOffset SentAt,
    string Result = "pending", DateTimeOffset? ReceivedAt = null, double? RoundTripMs = null,
    string? AudioStatus = null, string? AudioReason = null, bool AudioExpected = false);
public sealed record AuthorityDeliveryStatus(IReadOnlyList<AuthorityDelivery> Deliveries,
    bool PersistenceDegraded, long DroppedRecords);

public enum AnnouncementPriority { P0Emergency, P1RaceControl, P2SessionInformation, P3Informational }
public sealed record Announcement(string EventId, string SessionId, long Generation,
    AnnouncementPriority Priority, MessageType MessageType, JsonElement Payload,
    string TargetId, DateTimeOffset EffectiveAt, DateTimeOffset ExpiresAt);

public static class AuthorityCommands
{
    public static bool IsSemantic(MessageType type) => type is MessageType.Flag
        or MessageType.Penalty or MessageType.TextMessage or MessageType.ClearOverlay
        or MessageType.CustomFlag or MessageType.CustomFlagWithdraw
        or MessageType.ManualOverrideCleared or MessageType.ForceSystemReset;
}
