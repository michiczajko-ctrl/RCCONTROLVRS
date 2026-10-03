namespace VRS.RaceControl.Shared.Models;

public sealed record TelemetryEvidenceSample(DateTimeOffset CapturedAt, long Sequence,
    IReadOnlyList<TelemetryCar> Cars)
{
    [System.Text.Json.Serialization.JsonIgnore] public long CaptureMonotonicTimestamp { get; init; }
}
public sealed record TelemetryEvidenceManifest(string Id, string SessionId, string IncidentId,
    string GameEpoch, string SourceEpoch, DateTimeOffset IncidentAt, DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd, string State, int SampleCount, string Checksum,
    IReadOnlyList<string> QualityReasons, TrackDefinitionReference? TrackDefinition = null);
public sealed record TelemetryEvidenceClip(TelemetryEvidenceManifest Manifest, IReadOnlyList<TelemetryEvidenceSample> Samples);
public sealed record TelemetryEvidenceRequest(string IncidentId, int Offset = 0, Guid RequestId = default, string? ExportChecksum = null);
public sealed record TelemetryEvidencePage(Guid RequestId, TelemetryEvidenceManifest? Manifest,
    IReadOnlyList<TelemetryEvidenceSample> Samples, int? NextOffset, string? Error = null, bool ExportRecorded = false);
