using System.Globalization;
using System.Text;

namespace VRS.RaceControl.Shared.Services;

/// <summary>One layout of a venue. <see cref="GameName"/> is the exact track name LMU reports for it, or null until it has been seen.</summary>
public sealed record CatalogLayout(string Venue, string Label, string? GameName)
{
    public bool IsKnown => GameName != null;
    /// <summary>Text for the layout list: layouts whose in-game name is not known yet say so.</summary>
    public string DisplayLabel => IsKnown ? Label : Label + " · drive it once";
}

public sealed record CatalogVenue(string Name, IReadOnlyList<CatalogLayout> Layouts);

/// <summary>
/// Every venue and layout installed with Le Mans Ultimate (read from <c>Installed\Locations\*\layout*.mas</c> file names, nothing unpacked),
/// with the exact name LMU reports for each layout where it has been seen in LMU's own result files. LMU names are irregular
/// ("Monza Curva Grande Circuit", "COTA National Circuit"), so an unseen name is never guessed: it stays unknown until the game
/// reports it, and <see cref="Build"/> then fills it in from the learned names.
/// </summary>
public static class LmuTrackCatalog
{
    private sealed record VenueDefinition(string Name, string[] Keys, (string Label, string? GameName)[] Layouts);

    private static readonly VenueDefinition[] Definitions =
    [
        new("Bahrain International Circuit", ["bahrain"], [("Default (Grand Prix)", "Bahrain International Circuit"), ("Endurance", null), ("Outer", "Bahrain Outer Circuit"), ("Paddock", null)]),
        new("Barcelona-Catalunya", ["barcelona", "catalunya"], [("Default (ELMS)", null)]),
        new("Circuit of the Americas", ["cota", "americas"], [("Default (Grand Prix)", "Circuit of the Americas"), ("National", "COTA National Circuit")]),
        new("Daytona International Speedway", ["daytona"], [("Default (Road Course)", "Daytona International Speedway Road Course")]),
        new("Fuji Speedway", ["fuji"], [("Default", "Fuji Speedway"), ("Classic", "Fuji Speedway Classic")]),
        new("Imola", ["imola", "enzoedino"], [("Default", "Autodromo Enzo e Dino Ferrari"), ("ELMS", null)]),
        new("Interlagos", ["interlagos", "carlospace"], [("Default", "Autódromo José Carlos Pace")]),
        new("Laguna Seca", ["laguna"], [("Default", "WeatherTech Raceway Laguna Seca")]),
        new("Le Mans", ["sarthe", "lemans", "mulsanne"], [("Default (24h circuit)", "Circuit de la Sarthe"), ("Mulsanne", null)]),
        new("Long Beach", ["longbeach"], [("Default", "Grand Prix of Long Beach")]),
        new("Monza", ["monza"], [("Default (Grand Prix)", "Autodromo Nazionale Monza"), ("Curva Grande", "Monza Curva Grande Circuit")]),
        new("Paul Ricard", ["ricard"], [("Default (1A)", "Paul Ricard - 1A"), ("1A V2", null), ("1A V2 Short", null), ("3A", null), ("ELMS", null)]),
        new("Portimão (Algarve)", ["algarve", "portimao"], [("Default", "Algarve International Circuit"), ("ELMS", null)]),
        new("Lusail (Qatar)", ["qatar", "lusail"], [("Default", null), ("Short", null)]),
        new("Road Atlanta", ["roadatlanta"], [("Default", "Michelin Raceway Road Atlanta")]),
        new("Sebring", ["sebring"], [("Default", "Sebring International Raceway"), ("School", "Sebring School Circuit")]),
        new("Silverstone", ["silverstone"], [("International", null), ("National", null), ("WEC", "Silverstone Grand Prix Circuit - WEC"), ("ELMS", "Silverstone Grand Prix Circuit - ELMS")]),
        new("Spa-Francorchamps", ["spa"], [("Default", "Circuit de Spa-Francorchamps"), ("ELMS", null), ("Endurance", null)]),
    ];

    /// <summary>Lower case letters and digits only, accents removed: the form used to compare names and match learned ones.</summary>
    public static string Normalize(string? text)
    {
        var builder = new StringBuilder();
        foreach (var character in (text ?? "").Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(character))
                builder.Append(char.ToLowerInvariant(character));
        return builder.ToString();
    }

    /// <summary>The catalog with every learned game name placed on its layout. A name that fits no layout is added to its venue under its own name.</summary>
    public static IReadOnlyList<CatalogVenue> Build(IEnumerable<string>? learnedGameNames = null)
    {
        var venues = Definitions.Select(definition => (Definition: definition,
            Layouts: definition.Layouts.Select(layout => (Label: layout.Label, GameName: layout.GameName)).ToList())).ToList();
        var others = new List<(string Label, string? GameName)>();
        foreach (var learned in (learnedGameNames ?? []).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var normalized = Normalize(learned);
            if (venues.Any(venue => venue.Layouts.Any(layout => layout.GameName != null && Normalize(layout.GameName) == normalized))) continue;
            var venue = venues.FirstOrDefault(item => item.Definition.Keys.Any(key => normalized.Contains(key, StringComparison.Ordinal)));
            if (venue.Definition == null) { others.Add((learned.Trim(), learned.Trim())); continue; }
            var unknown = venue.Layouts.Select((layout, index) => (layout, index)).Where(item => item.layout.GameName == null).ToList();
            // The unseen layout whose label appears in the name wins; the longest label wins ("1A V2 Short" over "1A V2").
            var named = unknown.Where(item => !item.layout.Label.StartsWith("Default", StringComparison.Ordinal)
                    && normalized.Contains(Normalize(item.layout.Label), StringComparison.Ordinal))
                .OrderByDescending(item => Normalize(item.layout.Label).Length).Select(item => (Index: item.index, Found: true)).FirstOrDefault();
            if (!named.Found)
            {
                // No label fits: a lone unseen "Default" layout is the only place the name can belong.
                var defaults = unknown.Where(item => item.layout.Label.StartsWith("Default", StringComparison.Ordinal)).ToList();
                if (defaults.Count == 1 && unknown.All(item => defaults.Contains(item) || !normalized.Contains(Normalize(item.layout.Label), StringComparison.Ordinal)))
                    named = (defaults[0].index, true);
            }
            if (named.Found) venue.Layouts[named.Index] = (venue.Layouts[named.Index].Label, learned.Trim());
            else venue.Layouts.Add((learned.Trim(), learned.Trim()));
        }
        var result = venues.Select(venue => new CatalogVenue(venue.Definition.Name,
            venue.Layouts.Select(layout => new CatalogLayout(venue.Definition.Name, layout.Label, layout.GameName)).ToArray())).ToList();
        if (others.Count > 0)
            result.Add(new CatalogVenue("Other (seen in LMU)", others.Select(item => new CatalogLayout("Other (seen in LMU)", item.Label, item.GameName)).ToArray()));
        return result;
    }

    /// <summary>The layout whose in-game name is <paramref name="gameName"/>, or null.</summary>
    public static CatalogLayout? Find(IEnumerable<CatalogVenue> catalog, string? gameName) =>
        string.IsNullOrWhiteSpace(gameName) ? null : catalog.SelectMany(venue => venue.Layouts)
            .FirstOrDefault(layout => layout.GameName != null && Normalize(layout.GameName) == Normalize(gameName));
}
