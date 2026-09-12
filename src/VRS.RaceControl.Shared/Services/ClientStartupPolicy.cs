namespace VRS.RaceControl.Shared.Services;
public sealed record StartupCheckCache(DateTime LastFullCheckUtc, string Fingerprint, bool Success);
public static class ClientStartupPolicy
{
    public static bool IsQuick(StartupCheckCache? cache, string fingerprint, DateTime now) =>
        cache is { Success: true } && cache.Fingerprint == fingerprint && now >= cache.LastFullCheckUtc
        && now - cache.LastFullCheckUtc < TimeSpan.FromHours(24);
    public static TimeSpan ScreenBudget(bool quick) => TimeSpan.FromSeconds(quick ? 3 : 10);
}
