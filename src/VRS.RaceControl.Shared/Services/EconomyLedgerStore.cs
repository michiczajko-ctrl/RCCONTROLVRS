using System.Text.Json;
using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

/// <summary>
/// Append-only financial ledger — the sole source of truth for every team's balance (see
/// EconomyModels.TeamEconomy). Deliberately has no Update or Delete: a correction is
/// always a new Reversal entry (EconomyService.ReverseEntry), never an edit.
///
/// Corruption handling is the one deliberate departure from every other *Store in this
/// codebase (UserAccountStore/LeagueProfileStore/IncidentReportStore all quarantine a
/// corrupt file and silently resume with a fresh empty collection). For a financial ledger
/// that "same self-healing pattern" would turn a corrupted file into a silent wipe of every
/// team's balance in the league. Instead, once a corrupt file is quarantined, this store
/// BLOCKS all further writes (AppendBatch returns null) until an operator explicitly calls
/// <see cref="ClearQuarantine"/> — the quarantine marker is a separate file on disk, so it
/// survives a process restart instead of silently "healing" on the next launch.
/// </summary>
public sealed class EconomyLedgerStore
{
    private static readonly object s_gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _filePath;
    private readonly SyncOutboxStore _outbox;
    private bool _quarantined;

    public EconomyLedgerStore(string? filePath = null, SyncOutboxStore? outbox = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VRSRaceControl",
            "data",
            "economy-ledger.json");
        _outbox = outbox ?? new SyncOutboxStore(filePath == null
            ? null
            : Path.Combine(Path.GetDirectoryName(_filePath) ?? string.Empty, "sync-outbox.json"));
    }

    public string FilePath => _filePath;

    private string QuarantineMarkerPath => _filePath + ".quarantined";

    /// <summary>True if this ledger is blocked for writes — checks the on-disk marker
    /// directly, so a freshly-constructed instance reports the correct state even before
    /// <see cref="Load"/> has ever been called.</summary>
    public bool IsQuarantined => _quarantined || File.Exists(QuarantineMarkerPath);

    public IReadOnlyList<EconomyLedgerEntry> Load()
    {
        lock (s_gate)
        {
            return (IReadOnlyList<EconomyLedgerEntry>?)LoadUnsafe() ?? Array.Empty<EconomyLedgerEntry>();
        }
    }

    public IReadOnlyList<EconomyLedgerEntry> LoadForTeam(string leagueId, string seasonId, string teamId) =>
        Load()
            .Where(e => e.LeagueId == leagueId && e.SeasonId == seasonId && e.TeamId == teamId)
            .OrderBy(e => e.CreatedAtUtc)
            .ToList();

    public IReadOnlyList<EconomyLedgerEntry> Query(Func<EconomyLedgerEntry, bool> predicate) =>
        Load().Where(predicate).ToList();

    /// <summary>
    /// Appends every entry in <paramref name="newEntries"/> whose IdempotencyKey isn't
    /// already present, chaining each one to the previous entry's hash as it goes, in a
    /// single atomic write. Returns the entries that were actually appended (an empty list
    /// if every key was a duplicate — not an error), or null if the ledger is quarantined
    /// (nothing was written; the caller must treat this as "operation blocked", never as
    /// "ledger is empty").
    /// </summary>
    /// <param name="enqueueForSync">True for genuine local-origin entries. The cloud-sync
    /// pull loop passes false when applying cloud-origin entries locally — pushing them
    /// straight back would be harmless (ON CONFLICT DO NOTHING makes it a no-op) but is a
    /// wasted round trip on every single pulled entry, so it's suppressed the same way
    /// UserAccountStore.SyncAccount/IncidentReportStore.Upsert already do.</param>
    public IReadOnlyList<EconomyLedgerEntry>? AppendBatch(IEnumerable<EconomyLedgerEntry> newEntries, bool enqueueForSync = true)
    {
        lock (s_gate)
        {
            var entries = LoadUnsafe();
            if (entries == null)
            {
                return null;
            }

            var existingKeys = new HashSet<string>(entries.Select(e => e.IdempotencyKey), StringComparer.Ordinal);
            var lastHash = entries.Count > 0 ? entries[^1].EntryHash : string.Empty;
            // Per-team running balance, seeded lazily from existing entries — lets
            // BalanceAfter be a correct audit-display snapshot without a second full pass.
            // Not authoritative (see EconomyLedgerEntry.BalanceAfter's doc comment): a
            // cross-Host pull that inserts an entry earlier than ones already recorded here
            // won't retroactively fix later entries' snapshots, which is an accepted,
            // cosmetic-only imprecision for a field nothing else derives from.
            var runningTeamBalances = new Dictionary<string, long>(StringComparer.Ordinal);
            var appended = new List<EconomyLedgerEntry>();

            foreach (var entry in newEntries)
            {
                if (!existingKeys.Add(entry.IdempotencyKey))
                {
                    continue;
                }

                if (!runningTeamBalances.TryGetValue(entry.TeamId, out var runningBalance))
                {
                    runningBalance = entries.Where(e => e.TeamId == entry.TeamId).Sum(e => e.Amount);
                }
                runningBalance += entry.Amount;
                runningTeamBalances[entry.TeamId] = runningBalance;
                entry.BalanceAfter = runningBalance;

                entry.PreviousEntryHash = lastHash;
                entry.EntryHash = EconomyAmountRules.ComputeEntryHash(entry);
                lastHash = entry.EntryHash;
                entries.Add(entry);
                appended.Add(entry);
            }

            if (appended.Count > 0)
            {
                SaveUnsafe(entries);
                if (enqueueForSync)
                {
                    foreach (var entry in appended)
                    {
                        _outbox.Enqueue(SyncEntityType.EconomyLedgerEntry, entry.Id, SyncChangeKind.Upsert);
                    }
                }
            }
            return appended;
        }
    }

    public bool Append(EconomyLedgerEntry entry, bool enqueueForSync = true) =>
        AppendBatch(new[] { entry }, enqueueForSync)?.Count == 1;

    /// <summary>
    /// Walks the ledger in stored (= append) order and re-verifies every hash. Returns the
    /// Id of the first entry whose chain link or hash doesn't check out, or null if the
    /// whole ledger verifies cleanly.
    /// </summary>
    public string? VerifyChain()
    {
        var expectedPrevious = string.Empty;
        foreach (var entry in Load())
        {
            if (!string.Equals(entry.PreviousEntryHash, expectedPrevious, StringComparison.Ordinal))
            {
                return entry.Id;
            }
            if (!string.Equals(EconomyAmountRules.ComputeEntryHash(entry), entry.EntryHash, StringComparison.Ordinal))
            {
                return entry.Id;
            }
            expectedPrevious = entry.EntryHash;
        }
        return null;
    }

    /// <summary>Explicit operator action to lift a quarantine (e.g. after restoring the
    /// file from the Supabase copy) — never automatic.</summary>
    public void ClearQuarantine()
    {
        lock (s_gate)
        {
            if (File.Exists(QuarantineMarkerPath))
            {
                File.Delete(QuarantineMarkerPath);
            }
            _quarantined = false;
        }
    }

    private List<EconomyLedgerEntry>? LoadUnsafe()
    {
        if (File.Exists(QuarantineMarkerPath))
        {
            _quarantined = true;
            return null;
        }
        if (!File.Exists(_filePath))
        {
            return new List<EconomyLedgerEntry>();
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<EconomyLedgerEntry>>(json, JsonOptions)
                ?? new List<EconomyLedgerEntry>();
        }
        catch (JsonException)
        {
            var invalidPath = _filePath + $".invalid-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Move(_filePath, invalidPath, overwrite: false);
            File.WriteAllText(QuarantineMarkerPath,
                $"Uszkodzony plik księgi ekonomii przeniesiony do {invalidPath} o {DateTime.UtcNow:O}. " +
                "Zapisy są zablokowane do ręcznej interwencji operatora (patrz EconomyLedgerStore.ClearQuarantine).");
            _quarantined = true;
            return null;
        }
    }

    private void SaveUnsafe(IReadOnlyCollection<EconomyLedgerEntry> entries)
    {
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("Economy ledger path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(entries, JsonOptions));
        File.Move(temporaryPath, _filePath, overwrite: true);
    }
}
