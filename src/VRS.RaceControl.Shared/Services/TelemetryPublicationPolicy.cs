using System.Text;
using VRS.RaceControl.Shared.Enums;
using VRS.RaceControl.Shared.Models;
using VRS.RaceControl.Shared.Protocol;

namespace VRS.RaceControl.Shared.Services;

public static class TelemetryPublicationPolicy
{
    public static (ProtocolMessage Message, int DroppedSamples) Create(TelemetryBatch batch, int maximumBytes = 240 * 1024)
    {
        if (batch.Validate() is { } error) throw new InvalidDataException(error);
        var initialCount = batch.DetailedSamples?.Count ?? 0;
        while (true)
        {
            var message = ProtocolMessage.Create(MessageType.TelemetryBatch, batch, MessagePriority.Low);
            if (Encoding.UTF8.GetByteCount(message.ToJson()) <= maximumBytes)
                return (message, initialCount - (batch.DetailedSamples?.Count ?? 0));
            if (batch.DetailedSamples is not { Count: > 0 } samples)
                throw new InvalidDataException("Fleet sample exceeds the telemetry transport budget.");
            batch = batch with { DetailedSamples = samples.Skip(1).ToArray(), DetailedSamplesHaveGap = true };
        }
    }
}
