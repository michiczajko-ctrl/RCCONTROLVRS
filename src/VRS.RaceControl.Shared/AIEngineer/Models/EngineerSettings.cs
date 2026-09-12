namespace VRS.RaceControl.Shared.AIEngineer.Models;

public sealed class EngineerSettings
{
    public bool Enabled { get; set; } = true;
    public EngineerLanguage Language { get; set; } = EngineerLanguage.English;
    public bool VoiceEnabled { get; set; } = true;
    public int VoiceVolume { get; set; } = 85;
    public int VoiceRate { get; set; } = 0;
    public string? VoiceName { get; set; }
    public EngineerPriority MinimumVoicePriority { get; set; } = EngineerPriority.Informational;
    public bool MuteLowPriority { get; set; }
    public bool FuelMessagesEnabled { get; set; } = true;
    public bool StrategyMessagesEnabled { get; set; } = true;
    public bool LapMessagesEnabled { get; set; } = true;
    public bool TyreMessagesEnabled { get; set; } = true;
    public bool FlagMessagesEnabled { get; set; } = true;
    public bool RaceControlVoiceEnabled { get; set; } = true;
    public bool ReadEveryLap { get; set; }
    public double FuelReserveLiters { get; set; } = 2.0;
    public double FuelReserveLaps { get; set; } = 1.0;
    public double LowFuelLapsThreshold { get; set; } = 3.0;
    public double CriticalFuelLapsThreshold { get; set; } = 1.2;
    public double TyreColdThresholdC { get; set; } = 65.0;
    public double TyreHotThresholdC { get; set; } = 105.0;
    public double TyrePressureDeltaThreshold { get; set; } = 3.0;
    public int MaxQueueLength { get; set; } = 16;
    public bool DemoMode { get; set; }

    public EngineerSettings Clone() => (EngineerSettings)MemberwiseClone();
}
