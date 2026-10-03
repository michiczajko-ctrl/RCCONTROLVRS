namespace VRS.RaceControl.Shared.Models;

public enum LeagueConfigurationSaveState
{
    Saved,
    Unsaved,
    Saving,
    Synced,
    SyncError
}

/// <summary>
/// Versioned, additive configuration envelope. LeagueProfile messages remain supported
/// for older clients; Beta 6.2 peers prefer this complete snapshot.
/// </summary>
public sealed record LeagueConfigurationSnapshot(
    LeagueProfile Profile,
    LeagueEconomySettings Economy,
    long Revision,
    DateTime UpdatedAtUtc,
    string UpdatedBy);

public sealed record LeagueConfigurationAck(string LeagueId, long Revision, bool Accepted, string? Error = null);

public sealed class LeagueConfigurationConflictException : InvalidOperationException
{
    public LeagueConfigurationConflictException(long expectedRevision, long actualRevision)
        : base($"Konflikt konfiguracji: oczekiwano rewizji {expectedRevision}, aktualna rewizja to {actualRevision}.")
    {
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }

    public long ExpectedRevision { get; }
    public long ActualRevision { get; }
}
