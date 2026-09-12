namespace VRS.RaceControl.Shared.AIEngineer.Models;

public enum EngineerEventType
{
    EngineerStarted,
    EngineerStopped,
    TelemetryLost,
    TelemetryRecovered,
    SessionStarted,
    SessionEnded,
    DrivingStarted,
    CarStopped,
    NewLapStarted,
    LapCompleted,
    NewBestLap,
    EnteredPitLane,
    ExitedPitLane,
    PitLimiterOn,
    PitLimiterOff,
    FlagChanged,
    LowFuel,
    CriticalFuel,
    FuelDeficit,
    FuelConsumptionIncreased,
    PaceImproved,
    PaceDropped,
    TyreOverheating,
    TyreTooCold,
    TyrePressureIssue,
    CarDamage,
    RaceControlMessage,
    VoiceCommandResponse
}
