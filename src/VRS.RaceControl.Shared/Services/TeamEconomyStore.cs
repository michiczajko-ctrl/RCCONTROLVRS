using System.Text.Json;
using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

/// <summary>
/// Balances, contracts, and per-league economy settings — one file, one lock, same shape
/// as LeagueProfileStore (a wrapper object holding several related collections that are
/// always read/written together). Unlike EconomyLedgerStore this is a regular, ordinary
/// self-healing store: it holds no financial history of its own, only a materialized view
/// (TeamEconomy.Balance) that EconomyService can always recompute from the ledger, so the
/// usual "quarantine and resume with an empty collection" pattern is safe here.
/// </summary>
public sealed class TeamEconomyStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _sync = new();
    private readonly string _filePath;
    private readonly SyncOutboxStore _outbox;

    public TeamEconomyStore(string? filePath = null, SyncOutboxStore? outbox = null)
    {
        var dataRoot = Environment.GetEnvironmentVariable("VRS_RACE_CONTROL_DATA_ROOT");
        _filePath = filePath ?? Path.Combine(
            string.IsNullOrWhiteSpace(dataRoot)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VRSRaceControl")
                : Path.GetFullPath(dataRoot),
            "data",
            "team-economy.json");
        _outbox = outbox ?? new SyncOutboxStore(filePath == null
            ? null
            : Path.Combine(Path.GetDirectoryName(_filePath) ?? string.Empty, "sync-outbox.json"));
    }

    public string FilePath => _filePath;

    public TeamEconomy GetOrCreateBalance(string leagueId, string seasonId, string teamId)
    {
        lock (_sync)
        {
            var data = LoadUnsafe();
            var balance = data.Balances.FirstOrDefault(b => Matches(b, leagueId, seasonId, teamId));
            if (balance != null)
            {
                return balance;
            }
            balance = new TeamEconomy { LeagueId = leagueId, SeasonId = seasonId, TeamId = teamId };
            data.Balances.Add(balance);
            SaveUnsafe(data);
            return balance;
        }
    }

    public void SaveBalance(TeamEconomy balance)
    {
        lock (_sync)
        {
            var data = LoadUnsafe();
            var index = data.Balances.FindIndex(b => Matches(b, balance.LeagueId, balance.SeasonId, balance.TeamId));
            if (index >= 0)
            {
                data.Balances[index] = balance;
            }
            else
            {
                data.Balances.Add(balance);
            }
            SaveUnsafe(data);
        }
    }

    public IReadOnlyList<TeamContract> LoadContracts(string leagueId, string seasonId)
    {
        lock (_sync)
        {
            return LoadUnsafe().Contracts
                .Where(c => c.LeagueId == leagueId && c.SeasonId == seasonId)
                .ToList();
        }
    }

    public TeamContract? FindContractById(string contractId)
    {
        lock (_sync)
        {
            return LoadUnsafe().Contracts.FirstOrDefault(c => c.Id == contractId);
        }
    }

    /// <param name="enqueueForSync">True for a genuine local-origin change. The cloud-sync
    /// pull loop passes false when applying a cloud-origin contract locally, so applying a
    /// pull doesn't re-queue the very record it just pulled.</param>
    public void UpsertContract(TeamContract contract, bool enqueueForSync = true)
    {
        lock (_sync)
        {
            var data = LoadUnsafe();
            var index = data.Contracts.FindIndex(c => c.Id == contract.Id);
            if (index >= 0)
            {
                data.Contracts[index] = contract;
            }
            else
            {
                data.Contracts.Add(contract);
            }
            SaveUnsafe(data);
            if (enqueueForSync)
            {
                _outbox.Enqueue(SyncEntityType.TeamContract, contract.Id, SyncChangeKind.Upsert);
            }
        }
    }

    public LeagueEconomySettings LoadSettings(string leagueId, string seasonId = "default")
    {
        lock (_sync)
        {
            var data = LoadUnsafe();
            return data.Settings.FirstOrDefault(s => s.LeagueId == leagueId && s.SeasonId == seasonId)
                ?? new LeagueEconomySettings { LeagueId = leagueId, SeasonId = seasonId };
        }
    }

    /// <param name="enqueueForSync">True for a genuine local-origin change — see
    /// <see cref="UpsertContract"/> for why the cloud-sync pull loop passes false.</param>
    public void SaveSettings(LeagueEconomySettings settings, bool enqueueForSync = true)
    {
        lock (_sync)
        {
            var data = LoadUnsafe();
            var index = data.Settings.FindIndex(s => s.LeagueId == settings.LeagueId && s.SeasonId == settings.SeasonId);
            if (index >= 0)
            {
                data.Settings[index] = settings;
            }
            else
            {
                data.Settings.Add(settings);
            }
            SaveUnsafe(data);
            if (enqueueForSync)
            {
                _outbox.Enqueue(SyncEntityType.EconomySettings, $"{settings.LeagueId}:{settings.SeasonId}", SyncChangeKind.Upsert);
            }
        }
    }

    private static bool Matches(TeamEconomy balance, string leagueId, string seasonId, string teamId) =>
        balance.LeagueId == leagueId && balance.SeasonId == seasonId && balance.TeamId == teamId;

    private TeamEconomyData LoadUnsafe()
    {
        if (!File.Exists(_filePath))
        {
            return new TeamEconomyData();
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<TeamEconomyData>(json, JsonOptions) ?? new TeamEconomyData();
        }
        catch (JsonException)
        {
            var invalidPath = _filePath + $".invalid-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Move(_filePath, invalidPath, overwrite: false);
            return new TeamEconomyData();
        }
    }

    private void SaveUnsafe(TeamEconomyData data)
    {
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("Team economy path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(data, JsonOptions));
        File.Move(temporaryPath, _filePath, overwrite: true);
    }

    private sealed class TeamEconomyData
    {
        public List<TeamEconomy> Balances { get; set; } = new();
        public List<TeamContract> Contracts { get; set; } = new();
        public List<LeagueEconomySettings> Settings { get; set; } = new();
    }
}
