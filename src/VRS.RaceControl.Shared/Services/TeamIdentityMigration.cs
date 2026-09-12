using System.Security.Cryptography;
using System.Text;
using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

/// <summary>
/// "Heal on load, save if changed" — same idiom already used by
/// LeagueProfileStore.LoadForHost/LoadSupportedProfiles. No separate migration tool, no
/// schema-version flag: calling EnsureTeamIdentities on every load is idempotent and cheap.
/// </summary>
public static class TeamIdentityMigration
{
    /// <summary>
    /// Deterministic id derivation for a team that existed before Team.Id was introduced.
    /// NEVER change this function once shipped — two Hosts of the same league must derive
    /// the SAME id for the same legacy team name, or synchronization would produce
    /// duplicate teams with split balances. Teams created after this migration ships use a
    /// normal Guid.NewGuid().ToString("N") instead (see Team.Id's own default).
    /// </summary>
    public static string DeriveLegacyTeamId(string leagueId, string teamName) =>
        DeriveDeterministicId($"{leagueId}|{teamName.Trim().ToUpperInvariant()}");

    /// <summary>
    /// Deterministic id derivation for a season race that existed before LeagueRace.Id was
    /// introduced — same reasoning as <see cref="DeriveLegacyTeamId"/>. Includes the race's
    /// position at migration time so two identically-named races (repeated track) don't
    /// collide. NEVER change this function once shipped.
    /// </summary>
    public static string DeriveLegacyRaceId(string leagueId, int index, string name, string track) =>
        DeriveDeterministicId($"{leagueId}|{index}|{name.Trim().ToUpperInvariant()}|{track.Trim().ToUpperInvariant()}");

    private static string DeriveDeterministicId(string normalized)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }

    /// <summary>
    /// Assigns stable ids to legacy string-only teams and season races, and re-keys
    /// <see cref="LeagueProfile.TeamPermissions"/> from team name to <see cref="Team.Id"/>.
    /// Idempotent — running this twice makes no further changes. Permission entries that
    /// don't match any known team are left untouched rather than deleted (orphaned data is
    /// still data, not garbage) and reported via <paramref name="warnings"/>.
    /// </summary>
    public static bool EnsureTeamIdentities(LeagueProfile profile, out List<string> warnings)
    {
        warnings = new List<string>();
        var changed = false;

        foreach (var name in profile.Teams)
        {
            if (profile.FindTeam(name) != null)
            {
                continue;
            }
            profile.TeamEntries.Add(new Team
            {
                Id = DeriveLegacyTeamId(profile.Id, name),
                Name = name
            });
            changed = true;
        }

        foreach (var key in profile.TeamPermissions.Keys.ToList())
        {
            var team = profile.FindTeam(key);
            if (team == null)
            {
                warnings.Add($"Uprawnienia dla nieznanego zespołu „{key}” pozostawione bez zmian.");
                continue;
            }
            if (string.Equals(key, team.Id, StringComparison.Ordinal))
            {
                continue;
            }

            var entries = profile.TeamPermissions[key];
            profile.TeamPermissions.Remove(key);
            if (profile.TeamPermissions.TryGetValue(team.Id, out var existing))
            {
                existing.AddRange(entries);
            }
            else
            {
                profile.TeamPermissions[team.Id] = entries;
            }
            changed = true;
        }

        for (var i = 0; i < profile.SeasonRaces.Count; i++)
        {
            var race = profile.SeasonRaces[i];
            if (string.IsNullOrWhiteSpace(race.Id))
            {
                race.Id = DeriveLegacyRaceId(profile.Id, i, race.Name, race.Track);
                changed = true;
            }
        }

        return changed;
    }
}
