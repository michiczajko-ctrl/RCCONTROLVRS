namespace VRS.RaceControl.Shared.Enums;

/// <summary>
/// Types of penalties that can be issued by race control.
/// </summary>
public enum PenaltyType
{
    None = 0,
    Warning = 1,
    DriveThrough = 2,
    StopAndGo = 3,
    Investigation = 4,
    Disqualification = 5,
    TimePenalty5s = 6,
    TimePenalty10s = 7,
    TimePenalty30s = 8,
    StopAndGo5s = 9,
    StopAndGo10s = 10,
    /// <summary>Investigation closed with no penalty. Appended last — PenaltyStore persists the numeric values.</summary>
    NoFurtherAction = 11
}
