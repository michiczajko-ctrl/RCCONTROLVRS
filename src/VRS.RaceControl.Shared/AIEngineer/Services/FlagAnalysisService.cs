using VRS.RaceControl.Shared.AIEngineer.Models;
using VRS.RaceControl.Shared.Enums;

namespace VRS.RaceControl.Shared.AIEngineer.Services;

public sealed class FlagAnalysisService
{
    public EngineerMessage? CreateFlagMessage(
        FlagType flag,
        bool officialRaceControl,
        string? recordedAudioCueKey = null,
        string? sourceMessageId = null,
        string? supersessionKey = null)
    {
        var (text, priority) = flag switch
        {
            FlagType.Yellow => ("Yellow flag.", EngineerPriority.High),
            FlagType.DoubleYellow => ("Double yellow flag. Use caution.", EngineerPriority.High),
            FlagType.Blue => ("Blue flag, a faster car is approaching.", EngineerPriority.Normal),
            FlagType.BlackAndWhite => ("Black and white flag. Warning for driving standards.", EngineerPriority.Normal),
            FlagType.Black => ("Black flag. Follow Race Control instructions.", EngineerPriority.Critical),
            FlagType.Red => ("Red flag. Follow Race Control instructions.", EngineerPriority.Critical),
            FlagType.SafetyCar => ("Safety Car deployed. No overtaking.", EngineerPriority.High),
            FlagType.VirtualSafetyCar => ("Virtual Safety Car. Slow down and maintain the delta.", EngineerPriority.High),
            FlagType.FullCourseYellow => ("Full Course Yellow. Slow down and do not overtake.", EngineerPriority.High),
            FlagType.ReadyForGreen => ("Ready for green. Prepare to restart.", EngineerPriority.High),
            FlagType.Green => ("Green flag.", EngineerPriority.Informational),
            _ => ("Flag status changed.", EngineerPriority.Normal)
        };

        return new EngineerMessage
        {
            EventType = EngineerEventType.FlagChanged,
            Priority = officialRaceControl && priority < EngineerPriority.High ? EngineerPriority.High : priority,
            TextEn = text,
            Cooldown = officialRaceControl ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(20),
            CanInterruptLowerPriority = priority >= EngineerPriority.High || officialRaceControl,
            DeduplicationKey = officialRaceControl ? $"rc-flag-{flag}" : $"flag-{flag}",
            IsRaceControl = officialRaceControl,
            RecordedAudioCueKey = recordedAudioCueKey,
            SourceMessageId = sourceMessageId,
            SupersessionKey = supersessionKey
        };
    }
}
