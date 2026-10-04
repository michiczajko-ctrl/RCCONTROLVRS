using System.Text.Json;

namespace VRS.RaceControl.Shared.Services;

/// <summary>
/// Track names LMU has reported on this computer. The track and layout lists use them to fill in layouts whose in-game name was
/// not known, so a layout only needs to be driven once. The optional <c>directory</c> argument exists for tests.
/// </summary>
public static class LearnedTrackNames
{
    private const int MaxNames = 300, MaxLength = 200;
    private static string FilePath(string? directory) => Path.Combine(directory ?? LocalEnvironmentPaths.DataPath(""), "learned-track-names.json");

    public static IReadOnlyList<string> Load(string? directory = null)
    {
        try
        {
            var path = FilePath(directory);
            return File.Exists(path) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path))?
                .Where(name => !string.IsNullOrWhiteSpace(name) && name.Length <= MaxLength).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxNames).ToArray() ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    /// <summary>Stores a name the game reported. False when it was already stored, was empty or too long, or could not be written.</summary>
    public static bool Remember(string? name, string? directory = null)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MaxLength) return false;
        var names = Load(directory).ToList();
        if (names.Count >= MaxNames || names.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        names.Add(name);
        try
        {
            var path = FilePath(directory);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(names));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
