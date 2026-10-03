using VRS.RaceControl.Shared.Services;

namespace VRS.RaceControl.Shared.Models;

public sealed record ImpactDetectionPolicy(bool Enabled = true, double TimestampToleranceSeconds = .75,
    double DistanceToleranceMeters = 8, double DeduplicationCooldownSeconds = 5,
    double MinimumImpactMagnitude = 1.5, double MediumMagnitude = 4,
    double HeavyMagnitude = 8, double SevereMagnitude = 14)
{
    public bool IsValid => TimestampToleranceSeconds is >= .1 and <= 5 && DistanceToleranceMeters is >= 1 and <= 50
        && DeduplicationCooldownSeconds is >= 1 and <= 30 && MinimumImpactMagnitude is >= .01 and <= 1000
        && MediumMagnitude >= MinimumImpactMagnitude && MediumMagnitude <= 1000
        && HeavyMagnitude >= MediumMagnitude && HeavyMagnitude <= 1000
        && SevereMagnitude >= HeavyMagnitude && SevereMagnitude <= 1000;
    public static ImpactDetectionPolicy From(IncidentSettings settings) => new(settings.DetectionEnabled,
        settings.TimestampToleranceSeconds, settings.DistanceToleranceMeters, settings.DeduplicationCooldownSeconds,
        settings.MinimumImpactMagnitude, settings.MediumMagnitude, settings.HeavyMagnitude, settings.SevereMagnitude);
    public IncidentSettings ToSettings() => new() { DetectionEnabled = Enabled,
        TimestampToleranceSeconds = TimestampToleranceSeconds, DistanceToleranceMeters = DistanceToleranceMeters,
        DeduplicationCooldownSeconds = DeduplicationCooldownSeconds, MinimumImpactMagnitude = MinimumImpactMagnitude,
        MediumMagnitude = MediumMagnitude, HeavyMagnitude = HeavyMagnitude, SevereMagnitude = SevereMagnitude };
    public void ApplyTo(IncidentSettings settings)
    {
        settings.DetectionEnabled = Enabled; settings.TimestampToleranceSeconds = TimestampToleranceSeconds;
        settings.DistanceToleranceMeters = DistanceToleranceMeters; settings.DeduplicationCooldownSeconds = DeduplicationCooldownSeconds;
        settings.MinimumImpactMagnitude = MinimumImpactMagnitude; settings.MediumMagnitude = MediumMagnitude;
        settings.HeavyMagnitude = HeavyMagnitude; settings.SevereMagnitude = SevereMagnitude;
    }
}
