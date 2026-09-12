using VRS.RaceControl.Shared.Enums;

namespace VRS.RaceControl.Shared.Models;

public sealed class LeagueProfile
{
    private int _pitLaneSpeedLimitKmh = 60;
    public int PitLaneSpeedLimitKmh { get => _pitLaneSpeedLimitKmh; set => _pitLaneSpeedLimitKmh = VRS.RaceControl.Shared.Services.PitLaneRules.Normalize(value); }
    public bool SupportsIncidentReplies { get; set; }
    public const string DefaultId = "vrs";
    public const string IvsId = "ivs";
    public const string VvsEId = "vvs-e";
    public const string DefaultInternetServerUrl = "https://rccontrolvrs.onrender.com";

    public string Id { get; set; } = DefaultId;
    public string FullName { get; set; } = "Vistula Racing Series";
    public string ShortName { get; set; } = "VRS";
    public string RaceControlName { get; set; } = "VRS Race Control";
    public string? LogoPath { get; set; }
    public string PrimaryColor { get; set; } = "#58A6FF";
    public string SecondaryColor { get; set; } = "#A371F7";
    public string AccentColor { get; set; } = "#EC407A";
    public string BackgroundColor { get; set; } = "#0D1117";
    public string RelayUrl { get; set; } = DefaultInternetServerUrl;
    public string ApiUrl { get; set; } = string.Empty;
    public string InterfaceLanguage { get; set; } = "pl-PL";
    public string RulesUrl { get; set; } = string.Empty;
    public string WikiUrl { get; set; } = string.Empty;
    public int PenaltyPointsLimit { get; set; } = 12;
    public double SafetyRatingMinimum { get; set; }
    public double SafetyRatingMaximum { get; set; } = 100;
    public List<string> DriverCategories { get; set; } = new();
    public List<string> Seasons { get; set; } = new();
    public List<string> Teams { get; set; } = new();
    /// <summary>
    /// Stable team identities, introduced alongside the Economy feature so a team's
    /// financial history (and its permissions, below) survive a rename. <see cref="Teams"/>
    /// stays as a display-name mirror — untouched, still the source for combo boxes,
    /// TeamHubCacheStore.AllTeams, and legacy XAML. Use <see cref="ResolveTeamId"/>/
    /// <see cref="FindTeam"/> to translate a name-or-id reference to a stable Team.
    /// </summary>
    public List<Team> TeamEntries { get; set; } = new();
    /// <summary>
    /// Per-team member permissions, keyed by <see cref="Team.Id"/> (not name) — a rename
    /// no longer orphans these, since the key never changes. Legacy name-keyed data is
    /// re-keyed once by TeamIdentityMigration.EnsureTeamIdentities. See
    /// <see cref="PruneOrphanedTeamPermissions"/> for what happens when a team is removed.
    /// </summary>
    public Dictionary<string, List<TeamMemberPermission>> TeamPermissions { get; set; } = new();
    /// <summary>
    /// Drivers' pending applications to join a team, awaiting Host review — see
    /// MainViewModel.AcceptJoinRequest/RejectJoinRequest on the Host side. Purely
    /// additive; pruned when a team is removed (see MainViewModel.RemoveTeam).
    /// </summary>
    public List<TeamJoinRequestEntry> PendingJoinRequests { get; set; } = new();
    public List<string> Licenses { get; set; } = new();
    public List<string> EnabledStandardFlags { get; set; } = new()
    {
        "Green", "Yellow", "DoubleYellow", "Blue", "Red", "BlackAndWhite",
        "Black", "Checkered", "SafetyCar", "FullCourseYellow", "VirtualSafetyCar", "ReadyForGreen"
    };
    public List<CustomFlagDefinition> CustomFlags { get; set; } = new();
    public List<LeagueRace> SeasonRaces { get; set; } = new();
    public string OverlayFooter { get; set; } = "VRS RACE CONTROL";
    public LeagueOverlayConfiguration Overlay { get; set; } = new();

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Id)
            || !System.Text.RegularExpressions.Regex.IsMatch(Id, "^[a-zA-Z0-9_-]{1,40}$"))
            return "Identyfikator ligi może zawierać litery, cyfry, _ i -.";
        if (string.IsNullOrWhiteSpace(FullName) || string.IsNullOrWhiteSpace(ShortName))
            return "Pełna i skrócona nazwa ligi są wymagane.";
        if (!IsColor(PrimaryColor) || !IsColor(SecondaryColor)
            || !IsColor(AccentColor) || !IsColor(BackgroundColor))
            return "Kolory profilu muszą mieć format #RRGGBB.";
        if (!string.IsNullOrWhiteSpace(RelayUrl)
            && !VRS.RaceControl.Shared.Protocol.RelayEndpoint.TryNormalize(RelayUrl, out _))
            return "Adres relay musi używać ws:// lub wss://.";
        if (!IsOptionalUri(ApiUrl, "http", "https"))
            return "Adres API musi używać http:// lub https://.";
        if (PenaltyPointsLimit < 0 || SafetyRatingMinimum > SafetyRatingMaximum)
            return "Limity profilu ligi są nieprawidłowe.";
        var duplicateFlagCode = CustomFlags
            .GroupBy(flag => flag.Code.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateFlagCode != null)
            return $"Kod własnej flagi „{duplicateFlagCode.Key}” jest zduplikowany.";
        if (Overlay == null)
            return "Konfiguracja overlayu jest wymagana.";
        return Overlay.Validate()
               ?? CustomFlags.Select(flag => flag.Validate()).FirstOrDefault(error => error != null);
    }

    public static LeagueProfile CreateDefault() => CreateSupportedProfile(DefaultId);

    public static bool IsSupportedProfileId(string? id) =>
        id is not null && id.Equals(DefaultId, StringComparison.OrdinalIgnoreCase)
        || id is not null && id.Equals(IvsId, StringComparison.OrdinalIgnoreCase)
        || id is not null && id.Equals(VvsEId, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<LeagueProfile> CreateSupportedProfiles() =>
        new[]
        {
            CreateSupportedProfile(IvsId),
            CreateSupportedProfile(DefaultId),
            CreateSupportedProfile(VvsEId)
        };

    public static LeagueProfile CreateSupportedProfile(string id)
    {
        var normalizedId = id.Trim().ToLowerInvariant();
        var profile = new LeagueProfile();
        switch (normalizedId)
        {
            case IvsId:
                profile.Id = IvsId;
                profile.FullName = "IVS";
                profile.ShortName = "IVS";
                profile.RaceControlName = "IVS Race Control";
                profile.PrimaryColor = "#F2B732";
                profile.SecondaryColor = "#F97316";
                profile.AccentColor = "#FFF2CC";
                profile.OverlayFooter = "IVS RACE CONTROL";
                break;
            case VvsEId:
                profile.Id = VvsEId;
                profile.FullName = "VVS-E";
                profile.ShortName = "VVS-E";
                profile.RaceControlName = "VVS-E Race Control";
                profile.PrimaryColor = "#18C9A7";
                profile.SecondaryColor = "#2F81F7";
                profile.AccentColor = "#D7F9F1";
                profile.OverlayFooter = "VVS-E RACE CONTROL";
                break;
            case DefaultId:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(id), "Unsupported league profile.");
        }
        return profile;
    }

    public static void EnsureRequiredRaceControlActions(LeagueProfile profile)
    {
        if (!profile.EnabledStandardFlags.Contains(
                FlagType.ReadyForGreen.ToString(),
                StringComparer.OrdinalIgnoreCase))
        {
            profile.EnabledStandardFlags.Add(FlagType.ReadyForGreen.ToString());
        }
        if (!profile.EnabledStandardFlags.Contains(FlagType.Checkered.ToString(), StringComparer.OrdinalIgnoreCase))
            profile.EnabledStandardFlags.Add(FlagType.Checkered.ToString());
    }

    /// <summary>
    /// Resolves a team reference that may be either a stable <see cref="Team.Id"/> or a
    /// legacy/display name to that team's Id — the seam that lets existing name-based
    /// callers (GetTeamPermissions, validators, UserAccount.Team) keep passing a name
    /// unchanged while TeamPermissions itself is keyed by Id internally. Returns null if
    /// nothing in <see cref="TeamEntries"/> matches (e.g. before migration has run, or a
    /// genuinely unknown team).
    /// </summary>
    public string? ResolveTeamId(string? teamNameOrId)
    {
        if (string.IsNullOrWhiteSpace(teamNameOrId))
        {
            return null;
        }
        var byId = TeamEntries.FirstOrDefault(t => string.Equals(t.Id, teamNameOrId, StringComparison.Ordinal));
        if (byId != null)
        {
            return byId.Id;
        }
        var byName = TeamEntries.FirstOrDefault(t => string.Equals(t.Name, teamNameOrId, StringComparison.OrdinalIgnoreCase));
        return byName?.Id;
    }

    /// <summary>Same resolution as <see cref="ResolveTeamId"/>, returning the full entity.</summary>
    public Team? FindTeam(string? teamNameOrId)
    {
        var id = ResolveTeamId(teamNameOrId);
        return id == null ? null : TeamEntries.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));
    }

    /// <summary>
    /// Returns the permission rows for <paramref name="team"/> (accepts a name or a
    /// Team.Id — resolved via <see cref="ResolveTeamId"/>), creating an empty list in
    /// <see cref="TeamPermissions"/> if none exists yet. Falls back to the raw value as
    /// the dictionary key when the team isn't in <see cref="TeamEntries"/> yet (e.g. not
    /// migrated), matching this method's historical behavior for an unknown team.
    /// </summary>
    public List<TeamMemberPermission> GetTeamPermissions(string team)
    {
        var teamKey = ResolveTeamId(team) ?? team;
        var key = TeamPermissions.Keys.FirstOrDefault(k => string.Equals(k, teamKey, StringComparison.OrdinalIgnoreCase));
        if (key == null)
        {
            key = teamKey;
            TeamPermissions[key] = new List<TeamMemberPermission>();
        }
        return TeamPermissions[key];
    }

    /// <summary>
    /// Finds a specific member's permission row within a team (name or Team.Id, resolved
    /// via <see cref="ResolveTeamId"/>, plus case-insensitive login match), or null if the
    /// team/member has no explicit entry yet — callers should treat a null result as "no
    /// permissions granted" (least-privilege default).
    /// </summary>
    public TeamMemberPermission? FindMemberPermission(string? team, string? login)
    {
        if (string.IsNullOrWhiteSpace(team) || string.IsNullOrWhiteSpace(login))
        {
            return null;
        }
        var teamKey = ResolveTeamId(team) ?? team;
        var key = TeamPermissions.Keys.FirstOrDefault(k => string.Equals(k, teamKey, StringComparison.OrdinalIgnoreCase));
        if (key == null)
        {
            return null;
        }
        return TeamPermissions[key].FirstOrDefault(m => string.Equals(m.Login, login, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Drops permission entries for teams that no longer exist in <see cref="TeamEntries"/>
    /// — note an archived team (see MainViewModel.RemoveTeam) is still a valid entry here,
    /// so its permissions and CEO flag survive removal, only a truly deleted team's
    /// permissions are dropped. Call after any team-membership change.
    /// </summary>
    public void PruneOrphanedTeamPermissions()
    {
        var validTeamKeys = new HashSet<string>(
            TeamEntries.Select(t => t.Id).Concat(Teams),
            StringComparer.OrdinalIgnoreCase);
        foreach (var key in TeamPermissions.Keys.Where(k => !validTeamKeys.Contains(k)).ToList())
        {
            TeamPermissions.Remove(key);
        }
    }

    private static bool IsColor(string value) =>
        System.Text.RegularExpressions.Regex.IsMatch(value ?? string.Empty, "^#[0-9A-Fa-f]{6}$");

    private static bool IsOptionalUri(string value, params string[] schemes) =>
        string.IsNullOrWhiteSpace(value)
        || (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && schemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase));

    public override string ToString() => string.IsNullOrWhiteSpace(FullName) ? ShortName : FullName;
}

public sealed class LeagueRace
{
    /// <summary>
    /// No random default (unlike Team.Id/UserAccount.Id) — deliberately left empty for
    /// legacy records so TeamIdentityMigration can detect and deterministically derive an
    /// id for races that predate this field, instead of every Host independently minting a
    /// different random id for "the same" race on deserialization. Freshly created races
    /// (MainViewModel.AddSeasonRace) assign a real Guid.NewGuid() explicitly.
    /// </summary>
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Track { get; set; } = string.Empty;
}

/// <summary>
/// A team's stable identity — introduced so financial history (and permissions, see
/// <see cref="LeagueProfile.TeamPermissions"/>) survives a rename. <see cref="LeagueProfile.Teams"/>
/// stays as the display-name mirror everything else already reads.
/// </summary>
public sealed class Team
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string ShortName { get; set; } = string.Empty;
    public string ColorHex { get; set; } = "#58A6FF";
    public bool IsArchived { get; set; }
    public List<string> PreviousNames { get; set; } = new();
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            return "Zespół musi mieć identyfikator.";
        }
        if (string.IsNullOrWhiteSpace(Name) || Name.Trim().Length > 60)
        {
            return "Nazwa zespołu musi mieć od 1 do 60 znaków.";
        }
        return null;
    }
}

/// <summary>
/// A single team member's granted permissions, keyed by <see cref="Login"/> within one
/// team's entry in <see cref="LeagueProfile.TeamPermissions"/>. All flags default to
/// false — a driver has no team capabilities until the Host explicitly grants them.
/// </summary>
public sealed class TeamMemberPermission
{
    public string Login { get; set; } = string.Empty;
    public bool CanAnnounce { get; set; }
    /// <summary>
    /// Reserved for the future Economy feature (currently a "coming soon" stub with no
    /// real functionality) — modeled now so it isn't a throwaway/breaking addition later.
    /// </summary>
    public bool CanManageEconomy { get; set; }
    public bool CanAddMembers { get; set; }
    /// <summary>
    /// At most one member per team should carry this — Host-only, set from the "Zespoły"
    /// tab's roster editor. Additive, not implied by any of the other flags: a CEO doesn't
    /// automatically get CanAnnounce/CanAddMembers, and vice versa. Unlocks reviewing
    /// (accepting/rejecting) pending join applications for this specific team, from the
    /// driver's own Team Hub — see TeamJoinDecisionValidator.
    /// </summary>
    public bool IsCeo { get; set; }
}

/// <summary>
/// One driver's pending application to join a team, awaiting Host review in
/// <see cref="LeagueProfile.PendingJoinRequests"/>.
/// </summary>
public sealed class TeamJoinRequestEntry
{
    public string Login { get; set; } = string.Empty;
    public string DriverName { get; set; } = string.Empty;
    public string TeamName { get; set; } = string.Empty;
    public DateTime RequestedAtUtc { get; set; }
}
