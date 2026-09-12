namespace VRS.RaceControl.Shared.AIEngineer.Models;

public sealed class EngineerMessage
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public EngineerEventType EventType { get; init; }
    public EngineerPriority Priority { get; init; }
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public string TextEn { get; init; } = string.Empty;
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromSeconds(12);
    public bool CanInterruptLowerPriority { get; init; }
    public TimeSpan? ExpiresAfter { get; init; } = TimeSpan.FromSeconds(45);
    public string DeduplicationKey { get; init; } = string.Empty;
    public bool IsRaceControl { get; init; }
    public string? RecordedAudioCueKey { get; init; }
    public string? SourceMessageId { get; init; }
    public string? SupersessionKey { get; init; }

    public string GetText(EngineerLanguage language = EngineerLanguage.English) => TextEn;

    public bool IsExpired(DateTime nowUtc) =>
        ExpiresAfter.HasValue && nowUtc - TimestampUtc > ExpiresAfter.Value;
}
