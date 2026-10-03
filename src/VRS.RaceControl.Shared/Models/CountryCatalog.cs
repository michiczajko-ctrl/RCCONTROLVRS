using System.Globalization;

namespace VRS.RaceControl.Shared.Models;

public sealed record CountryOption(string Code, string Name)
{
    // The themed ComboBox template (VRS.RaceControl.Shared.UI/DesignSystem/Controls.xaml) renders its
    // closed-state selection via the item's ToString() rather than picking up DisplayMemberPath, so the
    // record's auto-generated ToString() (which would print "CountryOption { Code = ..., Name = ... }")
    // must be overridden for the picker to show a plain country name.
    public override string ToString() => Name;
}

/// <summary>ISO 3166-1 alpha-2 country list and emoji-flag rendering, shared by HOST (race-entry
/// picker) and CLIENT (calendar card). Built from .NET's own culture data instead of a hardcoded
/// list so coverage stays complete without manual maintenance.</summary>
public static class CountryCatalog
{
    public static readonly IReadOnlyList<CountryOption> AvailableCountries = CultureInfo
        .GetCultures(CultureTypes.SpecificCultures)
        .Select(culture => { try { return new RegionInfo(culture.Name); } catch { return null; } })
        .Where(region => region is not null)
        .Select(region => new CountryOption(region!.TwoLetterISORegionName, region.EnglishName))
        .GroupBy(option => option.Code, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static readonly Dictionary<string, string> NamesByCode = AvailableCountries
        .ToDictionary(option => option.Code, option => option.Name, StringComparer.OrdinalIgnoreCase);

    public static string? DisplayName(string? code) =>
        !string.IsNullOrWhiteSpace(code) && NamesByCode.TryGetValue(code, out var name) ? name : null;

    /// <summary>Two-letter code to Unicode regional-indicator flag emoji; falls back to a checkered flag.</summary>
    public static string FlagEmoji(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length != 2) return "\U0001F3C1";
        var upper = code.ToUpperInvariant();
        if (upper[0] is < 'A' or > 'Z' || upper[1] is < 'A' or > 'Z') return "\U0001F3C1";
        return string.Concat(upper.Select(c => char.ConvertFromUtf32(0x1F1E6 + (c - 'A'))));
    }
}
