using System.Text.Json;

namespace VRS.RaceControl.Shared.Services;

public enum SyncEntityType
{
    Account,
    LeagueProfile,
    IncidentReport,
    /// <summary>Immutable — a queued entry never needs to be "re-read for latest state" the
    /// way the other kinds do, since a ledger row never changes after being appended. The
    /// drain loop still looks it up by id from EconomyLedgerStore to push it, but coalescing
    /// duplicate queue entries (SyncOutboxStore.Enqueue's usual behavior) is harmless here
    /// either way — pushing the same immutable row twice is a no-op via ON CONFLICT DO NOTHING.</summary>
    EconomyLedgerEntry,
    TeamContract,
    EconomySettings,
    /// <summary>A league profile and its economy settings, committed to cloud storage as one transaction.</summary>
    LeagueConfiguration
}

public enum SyncChangeKind
{
    Upsert,
    Delete
}

/// <summary>
/// A lightweight marker, not a payload snapshot: the drain loop re-reads the
/// entity's current state from the relevant local store by id, so repeated
/// edits to the same entity naturally coalesce into one push of the latest
/// state instead of needing ordered replay.
/// </summary>
public sealed record SyncOutboxEntry(
    SyncEntityType EntityType,
    string EntityId,
    SyncChangeKind ChangeKind,
    DateTime QueuedAtUtc,
    int AttemptCount,
    string? CorrelationId = null,
    long? Revision = null,
    long? ExpectedRevision = null,
    string? PayloadJson = null,
    DateTime? NextAttemptUtc = null);

/// <summary>
/// Shared by UserAccountStore/LeagueProfileStore/IncidentReportStore, so the
/// lock is static (like UserAccountStore's own s_gate) rather than per-instance:
/// each of those stores holds its own SyncOutboxStore instance pointed at the
/// same default file, and writes from different instances must still serialize.
/// </summary>
public sealed class SyncOutboxStore
{
    private static readonly object s_gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _filePath;

    public SyncOutboxStore(string? filePath = null)
    {
        var configuredRoot = Environment.GetEnvironmentVariable("VRS_RACE_CONTROL_DATA_ROOT");
        _filePath = filePath ?? (!string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(configuredRoot, "data", "sync-outbox.json")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VRSRaceControl", "data", "sync-outbox.json"));
    }

    public string FilePath => _filePath;

    public IReadOnlyList<SyncOutboxEntry> LoadPending()
    {
        lock (s_gate)
        {
            return LoadUnsafe();
        }
    }

    public void Enqueue(SyncEntityType entityType, string entityId, SyncChangeKind changeKind,
        string? correlationId = null, long? revision = null, long? expectedRevision = null, string? payloadJson = null)
    {
        if (string.IsNullOrWhiteSpace(entityId))
        {
            return;
        }

        lock (s_gate)
        {
            var entries = LoadUnsafe().ToList();
            var index = entries.FindIndex(entry =>
                entry.EntityType == entityType
                && string.Equals(entry.EntityId, entityId, StringComparison.OrdinalIgnoreCase));

            var entry = new SyncOutboxEntry(entityType, entityId, changeKind, DateTime.UtcNow, 0,
                correlationId ?? Guid.NewGuid().ToString("N"), revision,
                index >= 0 ? entries[index].ExpectedRevision ?? expectedRevision : expectedRevision, payloadJson);
            if (index >= 0)
            {
                entries[index] = entry;
            }
            else
            {
                entries.Add(entry);
            }
            SaveUnsafe(entries);
        }
    }

    public bool IsPending(SyncEntityType entityType, string entityId) => LoadPending().Any(entry =>
        entry.EntityType == entityType
        && string.Equals(entry.EntityId, entityId, StringComparison.OrdinalIgnoreCase));

    public void Remove(SyncEntityType entityType, string entityId)
    {
        lock (s_gate)
        {
            var entries = LoadUnsafe().ToList();
            var removed = entries.RemoveAll(entry =>
                entry.EntityType == entityType
                && string.Equals(entry.EntityId, entityId, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
            {
                SaveUnsafe(entries);
            }
        }
    }

    public void MarkAttempt(SyncEntityType entityType, string entityId, string? correlationId = null)
    {
        lock (s_gate)
        {
            var entries = LoadUnsafe().ToList();
            var index = entries.FindIndex(entry =>
                entry.EntityType == entityType
                && string.Equals(entry.EntityId, entityId, StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && (correlationId == null || entries[index].CorrelationId == correlationId))
            {
                var attempts = entries[index].AttemptCount + 1;
                entries[index] = entries[index] with { AttemptCount = attempts,
                    NextAttemptUtc = DateTime.UtcNow.AddSeconds(Math.Min(60, Math.Pow(2, Math.Min(attempts, 6)))) };
                SaveUnsafe(entries);
            }
        }
    }

    public SyncOutboxEntry Freeze(SyncOutboxEntry entry, string payloadJson)
    {
        lock (s_gate)
        {
            var entries = LoadUnsafe();
            var index = entries.FindIndex(e => e.EntityType == entry.EntityType && e.EntityId == entry.EntityId
                && e.CorrelationId == entry.CorrelationId);
            var frozen = entry with { PayloadJson = entry.PayloadJson ?? payloadJson,
                CorrelationId = entry.CorrelationId ?? Guid.NewGuid().ToString("N") };
            if (index >= 0) { entries[index] = frozen; SaveUnsafe(entries); }
            return frozen;
        }
    }

    public void Acknowledge(SyncOutboxEntry sent, long acceptedRevision)
    {
        lock (s_gate)
        {
            var entries = LoadUnsafe();
            var index = entries.FindIndex(e => e.EntityType == sent.EntityType && e.EntityId == sent.EntityId);
            if (index < 0) return;
            if (entries[index].CorrelationId == sent.CorrelationId) entries.RemoveAt(index);
            else if (sent.EntityType == SyncEntityType.LeagueConfiguration)
                entries[index] = entries[index] with { ExpectedRevision = acceptedRevision };
            SaveUnsafe(entries);
        }
    }

    public SyncOutboxEntry ReplaceFrozenPayload(SyncOutboxEntry sent, string payloadJson)
    {
        lock(s_gate)
        {
            var entries=LoadUnsafe();
            var index=entries.FindIndex(e=>e.EntityType==sent.EntityType&&e.EntityId==sent.EntityId&&e.CorrelationId==sent.CorrelationId);
            var normalized=sent with { PayloadJson=payloadJson };
            if(index>=0){entries[index]=normalized;SaveUnsafe(entries);}
            return normalized;
        }
    }

    private List<SyncOutboxEntry> LoadUnsafe()
    {
        if (!File.Exists(_filePath))
        {
            return new List<SyncOutboxEntry>();
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<SyncOutboxEntry>>(json, JsonOptions)
                ?? new List<SyncOutboxEntry>();
        }
        catch (JsonException)
        {
            var invalidPath = _filePath + $".invalid-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Move(_filePath, invalidPath, overwrite: false);
            return new List<SyncOutboxEntry>();
        }
    }

    private void SaveUnsafe(IReadOnlyCollection<SyncOutboxEntry> entries)
    {
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("Sync outbox path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(entries, JsonOptions));
        File.Move(temporaryPath, _filePath, overwrite: true);
    }
}
