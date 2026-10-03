using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

public sealed record TelemetryEvidenceExport(int FormatVersion, DateTimeOffset ExportedAt,
    IncidentReport Incident, TelemetryEvidenceClip Evidence, TrackDefinition? TrackDefinition);

public static class TelemetryEvidenceExportCodec
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static byte[] Serialize(IncidentReport incident, TelemetryEvidenceClip clip, TrackDefinition? track)
    {
        Validate(incident, clip, track);
        return JsonSerializer.SerializeToUtf8Bytes(new TelemetryEvidenceExport(1, DateTimeOffset.UtcNow, incident, clip, track), Json);
    }

    public static TelemetryEvidenceExport Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("Evidence export exceeds 4 MiB.");
        var export = JsonSerializer.Deserialize<TelemetryEvidenceExport>(bytes, Json)
            ?? throw new InvalidDataException("Missing evidence export.");
        if (export.FormatVersion != 1) throw new InvalidDataException("Unsupported evidence export version.");
        Validate(export.Incident, export.Evidence, export.TrackDefinition);
        return export;
    }

    public static void Validate(IncidentReport incident, TelemetryEvidenceClip clip, TrackDefinition? track = null)
    {
        if (incident == null || clip?.Manifest == null || clip.Samples == null)
            throw new InvalidDataException("Missing case or evidence.");
        var manifest = clip.Manifest;
        if (incident.Id != manifest.IncidentId || incident.SessionId != manifest.SessionId
            || incident.TelemetryEvidenceId != manifest.Id || manifest.State is not ("complete" or "partial" or "collecting")
            || manifest.SampleCount != clip.Samples.Count || clip.Samples.Count > 160
            || manifest.WindowStart > manifest.IncidentAt || manifest.WindowEnd < manifest.IncidentAt
            || manifest.WindowEnd - manifest.WindowStart > TimeSpan.FromSeconds(7)
            || manifest.QualityReasons == null || manifest.TrackDefinition is { IsValid: false }
            || clip.Samples.Any(s => s == null || s.Sequence < 1 || s.Cars == null || s.Cars.Count > 8
                || s.CapturedAt < manifest.WindowStart || s.CapturedAt > manifest.WindowEnd
                || s.Cars.Any(c => c == null || !c.IsValid)
                || s.Cars.Select(c => c.VehicleId).Distinct().Count() != s.Cars.Count)
            || clip.Samples.Zip(clip.Samples.Skip(1)).Any(pair => pair.Second.CapturedAt <= pair.First.CapturedAt
                || pair.Second.Sequence <= pair.First.Sequence))
            throw new InvalidDataException("Evidence identity or sample count mismatch.");
        var checksum = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(clip.Samples,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        if (checksum != manifest.Checksum) throw new InvalidDataException("Evidence checksum mismatch.");
        if (track != null && (track.Validate() != null || !track.Verified
            || manifest.TrackDefinition != TrackDefinitionCodec.Reference(track)))
            throw new InvalidDataException("Historical track definition mismatch.");
    }

    public static async Task SaveAtomicallyAsync(string path, byte[] bytes, CancellationToken token = default)
    {
        _ = Deserialize(bytes);
        var target = Path.GetFullPath(path);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
