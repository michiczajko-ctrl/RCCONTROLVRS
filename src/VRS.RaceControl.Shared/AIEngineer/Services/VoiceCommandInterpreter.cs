using System.Globalization;
using System.Text;
using VRS.RaceControl.Shared.AIEngineer.Models;

namespace VRS.RaceControl.Shared.AIEngineer.Services;

public sealed class VoiceCommandInterpreter
{
    public VoiceCommandIntent Recognize(string input)
    {
        var text = Normalize(input);
        if (string.IsNullOrWhiteSpace(text)) return VoiceCommandIntent.Unknown;

        if (ContainsAny(text, "how much fuel")) return VoiceCommandIntent.FuelAmount;
        if (ContainsAny(text, "how many laps of fuel")) return VoiceCommandIntent.FuelLapsLeft;
        if (ContainsAny(text, "can i make it")) return VoiceCommandIntent.CanFinish;
        if (ContainsAny(text, "how much fuel do i need")) return VoiceCommandIntent.FuelNeeded;
        if (ContainsAny(text, "when should i pit")) return VoiceCommandIntent.PitWindow;
        if (ContainsAny(text, "last lap")) return VoiceCommandIntent.LastLap;
        if (ContainsAny(text, "best lap")) return VoiceCommandIntent.BestLap;
        if (ContainsAny(text, "pace")) return VoiceCommandIntent.Pace;
        if (ContainsAny(text, "tyres", "tires")) return VoiceCommandIntent.Tyres;
        if (ContainsAny(text, "current flag")) return VoiceCommandIntent.CurrentFlag;
        if (ContainsAny(text, "repeat")) return VoiceCommandIntent.RepeatLastMessage;
        if (ContainsAny(text, "mute")) return VoiceCommandIntent.MuteEngineer;
        if (ContainsAny(text, "enable engineer")) return VoiceCommandIntent.EnableEngineer;

        return VoiceCommandIntent.Unknown;
    }

    private static bool ContainsAny(string text, params string[] patterns) =>
        patterns.Any(text.Contains);

    private static string Normalize(string value)
    {
        var normalized = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(ch);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
