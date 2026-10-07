using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

/// <summary>
/// Local library of measured track profiles, one file per simulator/track/layout. Shared by HOST and CLIENT.
/// The optional <c>directory</c> arguments exist for tests; production code uses the default location.
/// </summary>
public static class TrackDefinitionStore
{
    public static string DirectoryPath => LocalEnvironmentPaths.DataPath("track-definitions");
    /// <summary>
    /// Maps built by a driver's CLIENT. Kept apart from <see cref="DirectoryPath"/> so a map a driver measured on a PC that also runs HOST can
    /// never be auto-loaded, published to a session or shared to the track library as the HOST's own map ("only the HOST sets the map").
    /// </summary>
    public static string ClientDirectoryPath => LocalEnvironmentPaths.DataPath("client-track-definitions");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static TrackDefinition Import(string path)
    {
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("Track definition exceeds 2 MiB.");
        var definition = JsonSerializer.Deserialize<TrackDefinition>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("Missing track definition.");
        if (definition.Validate() is { } error) throw new InvalidDataException(error);
        return definition;
    }

    /// <summary>File name stem for a simulator/track/layout, independent of the geometry (not the content checksum).</summary>
    public static string Key(string simulator, string track, string layout) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{simulator}:{track}:{layout}".ToUpperInvariant())));

    public static void Save(TrackDefinition definition, string? directory = null)
    {
        if (definition.Validate() is { } error) throw new InvalidDataException(error);
        directory ??= DirectoryPath;
        Directory.CreateDirectory(directory);
        WriteAtomically(Path.Combine(directory, Key(definition.Simulator, definition.TrackId, definition.LayoutId) + ".json"), definition);
    }

    public static void Export(TrackDefinition definition, string path)
    {
        if (definition.Validate() is { } error) throw new InvalidDataException(error);
        WriteAtomically(path, definition);
    }

    /// <summary>Direct lookup by the hashed file name. Null when missing, unreadable or invalid.</summary>
    public static TrackDefinition? Find(string simulator, string track, string layout, string? directory = null)
    {
        var path = Path.Combine(directory ?? DirectoryPath, Key(simulator, track, layout) + ".json");
        if (!File.Exists(path)) return null;
        try
        {
            var definition = Import(path);
            return definition.Simulator.Equals(simulator, StringComparison.OrdinalIgnoreCase)
                && definition.TrackId.Equals(track, StringComparison.OrdinalIgnoreCase)
                && definition.LayoutId.Equals(layout, StringComparison.OrdinalIgnoreCase) ? definition : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { return null; }
    }

    /// <summary>The CLIENT's lookup: its own folder first, then the shared folder (read only, for maps that were saved there before the folders were split).</summary>
    public static TrackDefinition? FindForClient(string simulator, string track, string layout, string? clientDirectory = null, string? sharedDirectory = null) =>
        Find(simulator, track, layout, clientDirectory ?? ClientDirectoryPath) ?? Find(simulator, track, layout, sharedDirectory);

    /// <summary>The CLIENT saves only into its own folder.</summary>
    public static void SaveForClient(TrackDefinition definition, string? clientDirectory = null) => Save(definition, clientDirectory ?? ClientDirectoryPath);

    /// <summary>Every valid saved map, newest first. Unreadable or invalid files are skipped, so a damaged file never breaks the list.</summary>
    public static IReadOnlyList<TrackDefinition> ListSaved(string? directory = null)
    {
        directory ??= DirectoryPath;
        if (!Directory.Exists(directory)) return [];
        var found = new List<(DateTime Written, TrackDefinition Map)>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            try { found.Add((File.GetLastWriteTimeUtc(path), Import(path))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { }
        }
        return found.OrderByDescending(item => item.Written).Select(item => item.Map).ToArray();
    }

    /// <summary>Removes the saved profile for a simulator/track/layout so it can be mapped again. A missing file is not an error.</summary>
    public static void Delete(string simulator, string track, string layout, string? directory = null)
    {
        var path = Path.Combine(directory ?? DirectoryPath, Key(simulator, track, layout) + ".json");
        if (File.Exists(path)) File.Delete(path);
    }

    private static void WriteAtomically(string path, TrackDefinition definition)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(definition, Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
