namespace VRS.RaceControl.Shared.Services;

/// <summary>
/// Driver numbers assigned in the app, looked up by driver name. LMU does not provide a usable car number, so the map
/// takes it from the account roster. A name is trusted only when it maps to exactly one number: if two accounts share a
/// name with different numbers the answer is "unknown", never a guess.
/// </summary>
public sealed class DriverNumberDirectory
{
    public static DriverNumberDirectory Empty { get; } = new(new Dictionary<string, string>());
    private readonly IReadOnlyDictionary<string, string> _numbers;
    private DriverNumberDirectory(IReadOnlyDictionary<string, string> numbers) => _numbers = numbers;
    public int Count => _numbers.Count;

    /// <summary>Entries are (driver name, driver number, status). Accounts being deleted are ignored.</summary>
    public static DriverNumberDirectory Build(IEnumerable<(string? Name, string? Number, string? Status)> entries)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, number, status) in entries)
        {
            var key = Normalize(name); var value = number?.Trim();
            if (key.Length == 0 || string.IsNullOrEmpty(value)) continue;
            if (status != null && status.Contains("delete", StringComparison.OrdinalIgnoreCase)) continue;
            if (found.TryGetValue(key, out var existing) && !string.Equals(existing, value, StringComparison.OrdinalIgnoreCase))
                ambiguous.Add(key);
            else found[key] = value;
        }
        foreach (var key in ambiguous) found.Remove(key);
        return new(found);
    }

    public string? Find(string? driverName) => _numbers.TryGetValue(Normalize(driverName), out var number) ? number : null;

    /// <summary>Case-insensitive, ignoring surrounding and repeated spaces.</summary>
    private static string Normalize(string? name) =>
        string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
