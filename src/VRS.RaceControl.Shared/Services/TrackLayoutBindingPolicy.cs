using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

/// <summary>Explicit controller confirmation is scoped to a measured asset and current game/source epochs.</summary>
public static class TrackLayoutBindingPolicy
{
    public static bool Matches(TrackDefinitionReference track, TrackLayoutBinding? binding, TelemetryBatch batch)
    {
        if (!track.IsValid || !track.Simulator.Equals(batch.Simulator, StringComparison.OrdinalIgnoreCase)
            || !track.TrackId.Equals(batch.TrackId, StringComparison.OrdinalIgnoreCase)) return false;
        if (batch.LayoutId != null) return track.LayoutId.Equals(batch.LayoutId, StringComparison.OrdinalIgnoreCase);
        if (binding?.IsValid != true || binding.Checksum != track.Checksum
            || binding.GameEpoch != batch.GameEpoch || binding.SourceEpoch != batch.SourceEpoch) return false;
        var lengths = batch.Cars.Where(car => car.TrackLengthMeters is > 0).Select(car => car.TrackLengthMeters!.Value).ToArray();
        return lengths.Length > 0 && lengths.All(length => Math.Abs(length - binding.TrackLengthMeters)
            <= Math.Max(20, binding.TrackLengthMeters * .01));
    }
}
