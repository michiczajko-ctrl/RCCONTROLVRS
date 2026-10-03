using System.Text.Json;
using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

public sealed record SessionStateDocument(
    int SchemaVersion,
    string ControlSessionId,
    long Generation,
    long Revision,
    string OwnerOperatorId,
    string RaceState,
    string PitLaneState,
    IReadOnlyList<SessionOperation> OperationJournal,
    DateTime SavedAtUtc);

public sealed class SessionStateStore
{
    public const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _filePath;

    public SessionStateStore(string? filePath = null) => _filePath = filePath ?? LocalEnvironmentPaths.DataPath("data", "active-session-v2.json");

    public void Save(SessionStateAggregate aggregate)
    {
        var document = new SessionStateDocument(CurrentSchemaVersion, aggregate.ControlSessionId,
            aggregate.Generation, aggregate.Revision, aggregate.OwnerOperatorId,
            aggregate.RaceState, aggregate.PitLaneState, aggregate.OperationJournal.ToArray(), DateTime.UtcNow);
        var directory = Path.GetDirectoryName(_filePath) ?? throw new InvalidOperationException("Session-state path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions));
        File.Move(temporaryPath, _filePath, true);
    }

    public SessionStateAggregate? Load()
    {
        if (!File.Exists(_filePath)) return null;
        try
        {
            var document = JsonSerializer.Deserialize<SessionStateDocument>(File.ReadAllText(_filePath), JsonOptions)
                ?? throw new InvalidDataException("Session-state document is empty.");
            if (document.SchemaVersion > CurrentSchemaVersion)
                throw new InvalidDataException($"Session-state schema {document.SchemaVersion} is newer than supported schema {CurrentSchemaVersion}.");
            var aggregate = new SessionStateAggregate
            {
                ControlSessionId = document.ControlSessionId,
                RaceState = document.RaceState,
                PitLaneState = document.PitLaneState
            };
            aggregate.RestoreAuthority(document.Generation, document.Revision, document.OwnerOperatorId,
                document.OperationJournal.Select(operation => operation.OperationId));
            aggregate.OperationJournal.AddRange(document.OperationJournal);
            return aggregate;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            var diagnosticPath = _filePath + $".invalid-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Copy(_filePath, diagnosticPath, false);
            throw new InvalidDataException(
                $"Active session state is invalid. The original was preserved and copied to '{diagnosticPath}'.", exception);
        }
    }
}
