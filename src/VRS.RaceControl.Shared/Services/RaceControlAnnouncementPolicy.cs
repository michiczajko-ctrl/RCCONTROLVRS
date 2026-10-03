using VRS.RaceControl.Shared.AIEngineer.Models;
using VRS.RaceControl.Shared.Enums;
using VRS.RaceControl.Shared.Models;
using VRS.RaceControl.Shared.Protocol;

namespace VRS.RaceControl.Shared.Services;

public static class RaceControlAnnouncementPolicy
{
    public static AnnouncementPriority Priority(ProtocolMessage message)
    {
        if (message.Type == MessageType.Flag && message.GetPayload<FlagPayload>()?.FlagType is FlagType.Red or FlagType.Black)
            return AnnouncementPriority.P0Emergency;
        if (message.Type == MessageType.CustomFlag && message.GetPayload<CustomFlagPayload>()?.Definition?.Code?.Trim().ToUpperInvariant() is
            "QSTART-GT3" or "FLOPEN-GT3" or "QEND-GT3" or "QSTART-HY" or "FLOPEN-HY" or "QEND-HY")
            return AnnouncementPriority.P2SessionInformation;
        return AnnouncementPriority.P1RaceControl;
    }
    public static EngineerPriority QueuePriority(AnnouncementPriority priority) => priority switch
    {
        AnnouncementPriority.P0Emergency => EngineerPriority.Critical,
        AnnouncementPriority.P1RaceControl => EngineerPriority.High,
        AnnouncementPriority.P2SessionInformation => EngineerPriority.Normal,
        _ => EngineerPriority.Informational
    };
}
