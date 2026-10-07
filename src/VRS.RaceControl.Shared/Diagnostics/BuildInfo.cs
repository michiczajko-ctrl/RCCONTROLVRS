using System.Reflection;

namespace VRS.RaceControl.Shared.Diagnostics;

public static class BuildInfo
{
    public static string DisplayVersion
    {
        get
        {
            var version = typeof(BuildInfo).Assembly.GetName().Version ?? new Version(1, 2, 1, 0);
            return version.ToString(3);
        }
    }

    public static string InformationalVersion =>
        typeof(BuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? "1.2.2";
}
