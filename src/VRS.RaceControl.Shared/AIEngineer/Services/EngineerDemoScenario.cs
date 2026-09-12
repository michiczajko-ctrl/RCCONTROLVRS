using VRS.RaceControl.Shared.Enums;
using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.AIEngineer.Services;

public static class EngineerDemoScenario
{
    public static IReadOnlyList<TelemetrySnapshotPayload> CreateFuelDeficitScenario()
    {
        var start = DateTime.UtcNow;
        return Enumerable.Range(0, 6)
            .Select(i => new TelemetrySnapshotPayload
            {
                TimestampUtc = start.AddSeconds(i * 75),
                IsAcConnected = true,
                SessionType = "AC_RACE",
                CompletedLaps = i,
                CurrentLap = i + 1,
                TotalLaps = 15,
                FuelLiters = 40 - (i * 3.0f),
                FuelPerLap = 3.0f,
                SpeedKmh = i == 0 ? 0 : 155,
                LastLapTimeMs = i == 0 ? null : 102_300 + i * 120,
                BestLapTimeMs = 102_300,
                Flag = i == 4 ? FlagType.Yellow : FlagType.Green,
                TyreCoreTemperature = new[] { 84f, 85f, 81f, 82f },
                TyrePressure = new[] { 27.2f, 27.4f, 27.0f, 27.1f },
                TrackName = "demo_track",
                CarModel = "rss_gtm_protech_p92_f6"
            })
            .ToArray();
    }
}
