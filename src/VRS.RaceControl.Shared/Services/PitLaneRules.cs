using VRS.RaceControl.Shared.Models;
namespace VRS.RaceControl.Shared.Services;

public static class PitLaneRules
{
    public const int MaximumKmh = 60;
    public static int Normalize(int value) => value <= 0 ? MaximumKmh : Math.Min(value, MaximumKmh);
    public static bool IsSpeeding(TelemetrySnapshotPayload snapshot, int limit, DateTime now) =>
        snapshot.TelemetryAvailable && snapshot.IsInPitLane == true && snapshot.SpeedKmh is { } speed
        && float.IsFinite(speed) && now >= snapshot.TimestampUtc
        && now - snapshot.TimestampUtc <= TimeSpan.FromSeconds(5) && speed > Normalize(limit);
}
