using System.Text.Json;
using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

public enum IncidentDetectionSensitivity { Low, Balanced, High }

public sealed class IncidentSettings
{
    public int SchemaVersion { get; set; } = 1;
    public bool DetectionEnabled { get; set; } = true;
    public bool DriverReportsEnabled { get; set; } = true;
    public bool ShowMinorIncidents { get; set; }
    public bool PopupNotificationsEnabled { get; set; } = true;
    public bool SoundAlertsEnabled { get; set; } = true;
    public IncidentDetectionSensitivity Sensitivity { get; set; } = IncidentDetectionSensitivity.Balanced;
    public double TimestampToleranceSeconds { get; set; } = 0.75;
    public double DistanceToleranceMeters { get; set; } = 8;
    public double DeduplicationCooldownSeconds { get; set; } = 5;
    public double MinimumImpactMagnitude { get; set; } = 1.5;
    public double MediumMagnitude { get; set; } = 4;
    public double HeavyMagnitude { get; set; } = 8;
    public double SevereMagnitude { get; set; } = 14;
    public int DriverReportCooldownSeconds { get; set; } = 15;

    public void Normalize()
    {
        TimestampToleranceSeconds = Math.Clamp(TimestampToleranceSeconds, 0.1, 5);
        DistanceToleranceMeters = Math.Clamp(DistanceToleranceMeters, 1, 50);
        DeduplicationCooldownSeconds = Math.Clamp(DeduplicationCooldownSeconds, 1, 30);
        MinimumImpactMagnitude = Math.Clamp(MinimumImpactMagnitude, 0.01, 1000);
        MediumMagnitude = Math.Max(MinimumImpactMagnitude, MediumMagnitude);
        HeavyMagnitude = Math.Max(MediumMagnitude, HeavyMagnitude);
        SevereMagnitude = Math.Max(HeavyMagnitude, SevereMagnitude);
        DriverReportCooldownSeconds = Math.Clamp(DriverReportCooldownSeconds, 10, 20);
    }
}

public sealed class IncidentSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public IncidentSettingsStore(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VRSRaceControl", "incident-settings.json");

    public IncidentSettings Load()
    {
        try
        {
            var value = File.Exists(_path)
                ? JsonSerializer.Deserialize<IncidentSettings>(File.ReadAllText(_path), JsonOptions)
                : null;
            value ??= new IncidentSettings();
            value.Normalize();
            return value;
        }
        catch
        {
            return new IncidentSettings();
        }
    }

    public void Save(IncidentSettings settings)
    {
        settings.Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporary, _path, true);
    }
}
