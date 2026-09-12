using VRS.RaceControl.Shared.AIEngineer.Models;
using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.AIEngineer.Services;

public sealed class TyreAnalysisService
{
    private static readonly string[] TyreNamesEn = { "front-left", "front-right", "rear-left", "rear-right" };

    public IReadOnlyList<EngineerMessage> Analyze(TelemetrySnapshotPayload snapshot, EngineerSettings settings)
    {
        var messages = new List<EngineerMessage>();
        if (!settings.TyreMessagesEnabled)
        {
            return messages;
        }

        var temps = snapshot.TyreCoreTemperature;
        if (temps is { Length: >= 4 })
        {
            for (var i = 0; i < 4; i++)
            {
                if (temps[i] >= settings.TyreHotThresholdC)
                {
                    messages.Add(Create(EngineerEventType.TyreOverheating,
                        $"The {TyreNamesEn[i]} tyre is overheating.",
                        $"tyre-hot-{i}"));
                }
                else if (temps[i] > 0 && temps[i] <= settings.TyreColdThresholdC)
                {
                    messages.Add(Create(EngineerEventType.TyreTooCold,
                        $"The {TyreNamesEn[i]} tyre is still too cold.",
                        $"tyre-cold-{i}"));
                }
            }
        }

        var pressure = snapshot.TyrePressure;
        if (pressure is { Length: >= 4 })
        {
            var avg = pressure.Where(v => v > 0).DefaultIfEmpty(0).Average();
            for (var i = 0; i < 4; i++)
            {
                if (avg > 0 && Math.Abs(pressure[i] - avg) >= settings.TyrePressureDeltaThreshold)
                {
                    messages.Add(Create(EngineerEventType.TyrePressureIssue,
                        $"The {TyreNamesEn[i]} tyre pressure is noticeably different.",
                        $"tyre-pressure-{i}"));
                }
            }
        }

        return messages;
    }

    private static EngineerMessage Create(EngineerEventType type, string text, string key)
    {
        return new EngineerMessage
        {
            EventType = type,
            Priority = EngineerPriority.Normal,
            TextEn = text,
            Cooldown = TimeSpan.FromMinutes(3),
            DeduplicationKey = key
        };
    }
}
