using System.Reflection;

namespace VRS.RaceControl.Shared.Diagnostics;

public static class BuildInfo
{
    public const string DisplayVersion = "Beta 5.2.F";

    public static string InformationalVersion =>
        typeof(BuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? "5.2.0-beta.F";
}
