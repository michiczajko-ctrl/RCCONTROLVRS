namespace VRS.RaceControl.Shared.Models;

public static class IncidentRaceClass
{
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var label = value.Trim().ToUpperInvariant();
        return label is "HYPERCAR" or "HY" ? "HY" : label[..Math.Min(label.Length, 40)];
    }
}
