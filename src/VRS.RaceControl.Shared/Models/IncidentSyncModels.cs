namespace VRS.RaceControl.Shared.Models;

/// <summary>Authoritative incident state sent after operator approval or reconnect.</summary>
public sealed record IncidentSnapshotPayload(long Revision, IReadOnlyList<IncidentReport> Incidents,
    long Generation = 0);

public sealed record IncidentSnapshotPagePayload(long Generation, long Revision,
    int PageIndex, int PageCount, IReadOnlyList<IncidentReport> Incidents);

/// <summary>One numbered incident replacement within the current HOST session.</summary>
public sealed record IncidentStateUpdatePayload(long Revision, IncidentReport Incident,
    long Generation = 0);

/// <summary>
/// Permission-gated steward command. Actor identity is derived from the authenticated
/// relay connection and is deliberately absent from the payload.
/// </summary>
public sealed record IncidentStatusCommandPayload(Guid CommandId, long KnownRevision,
    string IncidentId, IncidentStatus Status, string Note,
    IncidentCaseStatus? CaseStatus = null, IncidentDecision? Decision = null,
    string? AssignedSteward = null, string? ResponseToDriver = null,
    long KnownGeneration = 0);

public sealed record IncidentStatusResultPayload(Guid CommandId, bool Applied,
    long Revision, string Reason);
