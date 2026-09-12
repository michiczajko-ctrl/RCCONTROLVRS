using System.Text.Json.Serialization;
using VRS.RaceControl.Shared.Enums;

namespace VRS.RaceControl.Shared.Models;

public sealed class CustomFlagDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Color { get; set; } = "#FFFFFF";
    public string Icon { get; set; } = "⚑";
    public string Description { get; set; } = string.Empty;
    public string DriverMessage { get; set; } = string.Empty;
    public MessagePriority Priority { get; set; } = MessagePriority.Normal;
    public int DisplayDurationMs { get; set; } = 8000;
    public string? Sound { get; set; }
    public string EnterAnimation { get; set; } = "Slide";
    public string ExitAnimation { get; set; } = "Fade";
    public bool CanSendGlobally { get; set; } = true;
    public bool CanSendToDriver { get; set; } = true;
    public bool SaveInSessionHistory { get; set; } = true;
    public bool Enabled { get; set; } = true;

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) return "Nazwa flagi jest wymagana.";
        if (string.IsNullOrWhiteSpace(Code) || Code.Trim().Length > 12)
            return "Kod flagi musi mieć od 1 do 12 znaków.";
        if (!System.Text.RegularExpressions.Regex.IsMatch(Color, "^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$"))
            return "Kolor musi mieć format #RRGGBB lub #RRGGBBAA.";
        if (DisplayDurationMs is < 0 or > 120_000)
            return "Czas wyświetlania musi mieścić się w zakresie 0–120 sekund.";
        if (Description?.Length > 300 || DriverMessage?.Length > 300)
            return "Opis i komunikat flagi mogą mieć maksymalnie 300 znaków.";
        if (!Enum.IsDefined(Priority))
            return "Priorytet flagi jest nieprawidłowy.";
        if (!new[] { "None", "Fade", "Slide", "Scale" }.Contains(EnterAnimation)
            || !new[] { "None", "Fade", "Slide", "Scale" }.Contains(ExitAnimation))
            return "Animacja flagi jest nieprawidłowa.";
        if (Sound?.Length > 500)
            return "Ścieżka dźwięku jest zbyt długa.";
        return null;
    }
}

public sealed class CustomFlagPayload
{
    [JsonPropertyName("definition")]
    public CustomFlagDefinition Definition { get; set; } = new();
}

public sealed class CustomFlagWithdrawPayload
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;
}
