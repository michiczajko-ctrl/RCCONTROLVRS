using System.Text.Json;
using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

public sealed class IncidentReportStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _sync = new();
    private readonly string _filePath;
    private readonly SyncOutboxStore _outbox;

    public IncidentReportStore(string? filePath = null, SyncOutboxStore? outbox = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VRSRaceControl",
            "data",
            "incidents.json");
        _outbox = outbox ?? new SyncOutboxStore(filePath == null
            ? null
            : Path.Combine(Path.GetDirectoryName(_filePath) ?? string.Empty, "sync-outbox.json"));
    }

    public string FilePath => _filePath;

    public IReadOnlyList<IncidentReport> Load()
    {
        lock (_sync)
        {
            return LoadUnsafe();
        }
    }

    /// <param name="enqueueForSync">
    /// True for a genuine local-origin change. The cloud-sync pull loop passes
    /// false when applying a cloud-origin record locally, so applying a pull
    /// doesn't re-queue the very record it just pulled.
    /// </param>
    public void Upsert(IncidentReport report, bool enqueueForSync = true)
    {
        var validationError = IncidentReport.Validate(report);
        if (validationError != null)
        {
            throw new ArgumentException(validationError, nameof(report));
        }

        lock (_sync)
        {
            var reports = LoadUnsafe().ToList();
            var index = reports.FindIndex(item =>
                string.Equals(item.Id, report.Id, StringComparison.OrdinalIgnoreCase));
            report.UpdatedAtUtc = DateTime.UtcNow;
            if (index >= 0)
            {
                reports[index] = report;
            }
            else
            {
                reports.Add(report);
            }
            SaveUnsafe(reports);
            if (enqueueForSync)
            {
                _outbox.Enqueue(SyncEntityType.IncidentReport, report.Id, SyncChangeKind.Upsert);
            }
        }
    }

    public bool Remove(string reportId)
    {
        lock (_sync)
        {
            var reports = LoadUnsafe().ToList();
            var removed = reports.RemoveAll(item =>
                string.Equals(item.Id, reportId, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
            {
                SaveUnsafe(reports);
                _outbox.Enqueue(SyncEntityType.IncidentReport, reportId, SyncChangeKind.Delete);
            }
            return removed;
        }
    }

    public static string GetOutboxPath(string driverId)
        => GetDriverScopedPath(driverId, "outbox", "incident-reports");

    public static string GetHistoryPath(string driverId)
        => GetDriverScopedPath(driverId, "data", "incident-history");

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

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VRSRaceControl",
            directory,
            $"{filePrefix}-{safeDriverId}.json");
    }

    private List<IncidentReport> LoadUnsafe()
    {
        if (!File.Exists(_filePath))
        {
            return new List<IncidentReport>();
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<IncidentReport>>(json, JsonOptions)
                ?? new List<IncidentReport>();
        }
        catch (JsonException)
        {
            var invalidPath = _filePath + $".invalid-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Move(_filePath, invalidPath, overwrite: false);
            return new List<IncidentReport>();
        }
    }

    private void SaveUnsafe(IReadOnlyCollection<IncidentReport> reports)
    {
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("Incident store path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(reports, JsonOptions));
        File.Move(temporaryPath, _filePath, overwrite: true);
    }
}
