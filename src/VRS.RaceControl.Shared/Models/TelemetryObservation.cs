namespace VRS.RaceControl.Shared.Models;

/// <summary>Rule-neutral identity and evidence context; rule-specific measurements remain separate.</summary>
public sealed record TelemetryObservation(string Id, string RuleId, string GameEpoch,
    string SourceEpoch, string PeriodId, IReadOnlyList<int> VehicleIds, long Revision,
    DateTimeOffset ObservedAt, string State, string ConfigurationVersion,
    System.Text.Json.JsonElement? Configuration = null);
