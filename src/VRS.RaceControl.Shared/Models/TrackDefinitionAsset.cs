namespace VRS.RaceControl.Shared.Models;

public sealed record TrackLayoutBinding(string Checksum, string GameEpoch, string SourceEpoch, double TrackLengthMeters)
{
    public bool IsValid => Checksum is { Length: 64 } && Checksum.All(Uri.IsHexDigit)
        && !string.IsNullOrWhiteSpace(GameEpoch) && GameEpoch.Length <= 128
        && !string.IsNullOrWhiteSpace(SourceEpoch) && SourceEpoch.Length <= 128
        && double.IsFinite(TrackLengthMeters) && TrackLengthMeters is >= 200 and <= 50000;
}

public sealed record TrackDefinitionReference(string Checksum, string Simulator, string TrackId, string LayoutId, int Revision)
{
    public bool IsValid => Checksum?.Length == 64 && Checksum.All(Uri.IsHexDigit)
        && !string.IsNullOrWhiteSpace(Simulator) && Simulator.Length <= 100
        && !string.IsNullOrWhiteSpace(TrackId) && TrackId.Length <= 200
        && !string.IsNullOrWhiteSpace(LayoutId) && LayoutId.Length <= 100 && Revision > 0;
}
public sealed record TrackDefinitionRequest(Guid RequestId, string Checksum, int Offset = 0);
public sealed record TrackDefinitionPage(Guid RequestId, string Checksum, TrackDefinition? Definition,
    int? NextOffset, string? Error = null);
