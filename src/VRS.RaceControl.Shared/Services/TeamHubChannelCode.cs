namespace VRS.RaceControl.Shared.Services;

/// <summary>
/// Derives the stable, deterministic session code used by the always-on Team Hub channel
/// (see MainViewModel's _teamHubServer/_teamHubRelayHost) — unlike a real race session's
/// code (random, regenerated on every Start Session click), this one is always the same
/// for a given league, so Host and Client never need to exchange it manually. Both sides
/// call this identical algorithm so they can never drift apart.
/// </summary>
public static class TeamHubChannelCode
{
    public static string Derive(string? leagueId)
    {
        var sanitized = new string((leagueId ?? string.Empty)
            .ToUpperInvariant()
            .Where(char.IsAsciiLetterOrDigit)
            .Take(20)
            .ToArray());
        if (sanitized.Length < 2)
        {
            sanitized = "DEFAULT";
        }
        return $"TH-{sanitized}";
    }
}
