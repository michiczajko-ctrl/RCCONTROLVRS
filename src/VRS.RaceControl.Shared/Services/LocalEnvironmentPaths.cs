namespace VRS.RaceControl.Shared.Services;

/// <summary>Opt-in per-process test roots; default installed locations are preserved.</summary>
public static class LocalEnvironmentPaths
{
    public static bool IsolatedData => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VRS_RACE_CONTROL_DATA_ROOT"));
    public static string DataRoot => Resolve("VRS_RACE_CONTROL_DATA_ROOT", Environment.SpecialFolder.LocalApplicationData, false);
    public static string SettingsRoot => Resolve("VRS_RACE_CONTROL_SETTINGS_ROOT", Environment.SpecialFolder.ApplicationData, true);
    public static string DataPath(params string[] parts) => Path.Combine([DataRoot, .. parts]);
    private static string Resolve(string variable, Environment.SpecialFolder fallback, bool appendApplication)
    {
        var configured = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(configured)) return Path.Combine(Environment.GetFolderPath(fallback), "VRSRaceControl");
        var root = Path.GetFullPath(configured);
        return appendApplication ? Path.Combine(root,"VRSRaceControl") : root;
    }
}
