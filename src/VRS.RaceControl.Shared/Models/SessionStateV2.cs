using System.Text.Json;

namespace VRS.RaceControl.Shared.Models;

public sealed record SessionOperation(
    Guid OperationId,
    string ControlSessionId,
    long Generation,
    long ExpectedRevision,
    string OperatorId,
    DateTime TimestampUtc,
    string Kind,
    string PayloadJson);

public sealed record SessionOperationEnvelope(SessionOperation Operation, JsonElement Snapshot);
public sealed record SessionOperationResultPayload(Guid OperationId, SessionOperationResult Result);
public sealed record SessionSnapshotV2Payload(long Generation, long Revision, string OwnerOperatorId, JsonElement Snapshot);

public sealed class SessionStateAggregate
{
    private readonly HashSet<Guid> _appliedOperations = new();
    public string ControlSessionId { get; init; } = string.Empty;
    public long Generation { get; private set; }
    public long Revision { get; private set; }
    public string OwnerOperatorId { get; private set; } = string.Empty;
    public string RaceState { get; set; } = "GREEN";
    public string PitLaneState { get; set; } = "CLOSED";
    public List<SessionOperation> OperationJournal { get; } = new();

    public SessionOperationResult Apply(SessionOperation operation, Action<SessionStateAggregate> mutation)
    {
        if (!string.Equals(operation.ControlSessionId, ControlSessionId, StringComparison.Ordinal))
            return SessionOperationResult.Rejected("Session mismatch", Revision);
        if (operation.Generation != Generation)
            return SessionOperationResult.Rejected("Ownership generation mismatch", Revision);
        if (_appliedOperations.Contains(operation.OperationId))
            return SessionOperationResult.Duplicate(Revision);
        if (operation.ExpectedRevision != Revision)
            return SessionOperationResult.Conflict(Revision);

        mutation(this);
        _appliedOperations.Add(operation.OperationId);
        OperationJournal.Add(operation);
        Revision++;
        return SessionOperationResult.Applied(Revision);
    }

    public void RestoreAuthority(long generation, long revision, string ownerOperatorId, IEnumerable<Guid>? appliedOperations = null)
    {
        if (generation < Generation || revision < Revision) throw new InvalidOperationException("Session authority cannot move backwards.");
        Generation = generation;
        Revision = revision;
        OwnerOperatorId = ownerOperatorId;
        if (appliedOperations != null) _appliedOperations.UnionWith(appliedOperations);
    }
}

public sealed record SessionOperationResult(bool Success, bool IsDuplicate, bool IsConflict, string? Error, long Revision)
{
    public static SessionOperationResult Applied(long revision) => new(true, false, false, null, revision);
    public static SessionOperationResult Duplicate(long revision) => new(true, true, false, null, revision);
    public static SessionOperationResult Conflict(long revision) => new(false, false, true, "Revision conflict", revision);
    public static SessionOperationResult Rejected(string error, long revision) => new(false, false, false, error, revision);
}
