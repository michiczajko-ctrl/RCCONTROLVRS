using System.Text.Json;
using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

/// <summary>
/// Durable per-penalty history store. Mirrors <see cref="IncidentReportStore"/>'s file
/// shape but has no outbox — cloud sync is out of scope for penalty history; this is a
/// local-JSON-only record. HOST uses the default (no-arg) path as the single authoritative
/// store for every driver; CLIENT uses <see cref="GetHistoryPath"/> for its own driver-scoped
/// local cache.
/// </summary>
public sealed class PenaltyStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _sync = new();
    private readonly string _filePath;

    public PenaltyStore(string? filePath = null)
    {
        _filePath = filePath ?? LocalEnvironmentPaths.DataPath("data", "penalties.json");
    }

    public string FilePath => _filePath;

    public IReadOnlyList<PenaltyPayload> Load()
    {
        lock (_sync)
        {
            return LoadUnsafe();
        }
    }

    public IReadOnlyList<PenaltyPayload> LoadForAccount(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return Array.Empty<PenaltyPayload>();
        }

        return Load()
            .Where(item => item.AccountId.Equals(accountId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.CreatedAtUtc)
            .ToList();
    }

    public void Upsert(PenaltyPayload record)
    {
        if (string.IsNullOrWhiteSpace(record.Id))
        {
            throw new ArgumentException("Penalty record requires an Id.", nameof(record));
        }

        lock (_sync)
        {
            var records = LoadUnsafe().ToList();
            var index = records.FindIndex(item =>
                string.Equals(item.Id, record.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                records[index] = record;
            }
            else
            {
                records.Add(record);
            }
            SaveUnsafe(records);
        }
    }

    public static string GetHistoryPath(string driverId)
        => GetDriverScopedPath(driverId, "data", "penalty-history");

    private static string GetDriverScopedPath(
        string driverId,
        string directory,
        string filePrefix)
    {
        var safeDriverId = string.Concat(driverId
            .Where(character => char.IsLetterOrDigit(character) || character is '-' or '_'));
        if (string.IsNullOrWhiteSpace(safeDriverId))
        {
            safeDriverId = "unknown";
        }

        return LocalEnvironmentPaths.DataPath(directory, $"{filePrefix}-{safeDriverId}.json");
    }

    private List<PenaltyPayload> LoadUnsafe()
    {
        if (!File.Exists(_filePath))
        {
            return new List<PenaltyPayload>();
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<PenaltyPayload>>(json, JsonOptions)
                ?? new List<PenaltyPayload>();
        }
        catch (JsonException)
        {
            var invalidPath = _filePath + $".invalid-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Move(_filePath, invalidPath, overwrite: false);
            return new List<PenaltyPayload>();
        }
    }

    private void SaveUnsafe(IReadOnlyCollection<PenaltyPayload> records)
    {
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("Penalty store path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(records, JsonOptions));
        File.Move(temporaryPath, _filePath, overwrite: true);
    }
}
