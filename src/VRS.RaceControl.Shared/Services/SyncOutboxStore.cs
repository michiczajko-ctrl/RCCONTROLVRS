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
    EconomySettings
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
    int AttemptCount);

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
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VRSRaceControl",
            "data",
            "sync-outbox.json");
    }

    public string FilePath => _filePath;

    public IReadOnlyList<SyncOutboxEntry> LoadPending()
    {
        lock (s_gate)
        {
            return LoadUnsafe();
        }
    }

    public void Enqueue(SyncEntityType entityType, string entityId, SyncChangeKind changeKind)
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

            var entry = new SyncOutboxEntry(entityType, entityId, changeKind, DateTime.UtcNow, 0);
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

    public void MarkAttempt(SyncEntityType entityType, string entityId)
    {
        lock (s_gate)
        {
            var entries = LoadUnsafe().ToList();
            var index = entries.FindIndex(entry =>
                entry.EntityType == entityType
                && string.Equals(entry.EntityId, entityId, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                entries[index] = entries[index] with { AttemptCount = entries[index].AttemptCount + 1 };
                SaveUnsafe(entries);
            }
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
