using System.Text;

namespace VRS.RaceControl.Shared.Protocol;

/// <summary>Only relay-owned operations depend on advertised server features.
/// End-to-end application messages use the stable compatibility envelope.</summary>
public static class RelayWirePolicy
{
    public const int MaximumMessageBytes = 256 * 1024;

    public static ProtocolMessage Prepare(ProtocolMessage message) =>
        LegacyProtocolTunnel.WrapIfRequired(message, !IsRelayOperation(message.Type));

    public static bool IsRelayOperation(MessageType type) => type is
        MessageType.SessionCommand or MessageType.CommandResult or MessageType.SessionSnapshot
        or MessageType.AuthorityTransfer or MessageType.TelemetryBatch or MessageType.TelemetryHealth
        or MessageType.TelemetryPublisher or MessageType.Announcement
        or MessageType.EvidenceRequest or MessageType.EvidencePage or
        MessageType.OperatorSnapshot or MessageType.OperatorConnectionRequest
        or MessageType.OperatorHeartbeat or MessageType.OperatorApproval
        or MessageType.OperatorPriorityRequest or MessageType.OperatorPriorityDecision
        or MessageType.OperatorPriorityTransfer or MessageType.OperatorCommand
        or MessageType.IncidentSnapshot or MessageType.IncidentStateUpdate
        or MessageType.IncidentStatusCommand or MessageType.IncidentStatusResult
        or MessageType.IncidentSnapshotRequest
        or MessageType.IncidentSnapshotPage
        or MessageType.SessionSnapshotV2
        or MessageType.SessionOperationV2 or MessageType.SessionOperationResultV2
        or MessageType.ResourceLeaseV2 or MessageType.SessionOwnershipV2;

    public static byte[] Encode(ProtocolMessage message)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJson());
        if (bytes.Length > MaximumMessageBytes)
            throw new ArgumentException($"Relay message exceeds {MaximumMessageBytes} bytes ({bytes.Length}); not sent.");
        return bytes;
    }
}
