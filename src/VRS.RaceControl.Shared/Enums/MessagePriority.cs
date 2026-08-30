namespace VRS.RaceControl.Shared.Enums;

/// <summary>
/// Message priority levels. Higher values = higher priority.
/// Messages with higher priority override lower-priority messages on the overlay.
/// </summary>
public enum MessagePriority
{
    Low = 1,       // Green flag
    Normal = 2,    // Black & White flag
    Medium = 3,    // Warning, Blue flag
    High = 4,      // Yellow flag, Investigation
    Critical = 5,  // Double Yellow, Drive Through, Stop & Go
    Urgent = 6,    // Black flag, Stop and Go
    Emergency = 7, // VSC
    Maximum = 8,   // FCY, Disqualification
    SafetyCar = 9, // Safety Car
    RedFlag = 10   // Red Flag - highest priority
}

