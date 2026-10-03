namespace VRS.RaceControl.Shared.Models;

/// <summary>HOST-admin view of an online login account (narrow — never carries credentials).</summary>
public sealed record AccountRosterRow(string Login, string Status, string? LegacyAccountId, string? DriverName,
    string? DriverNumber, string? UserId = null, bool IsLocalOnly = false,
    DateTime? RestrictedUntil = null, DateTime? PurgeAfter = null);
public sealed record AccountRosterList(AccountRosterRow[] Accounts);

public sealed class OnlineLeagueProfile
{
    public string UserId { get; set; } = string.Empty;
    public string? LegacyAccountId { get; set; }
    public string LeagueId { get; set; } = string.Empty;
    public string? Login { get; set; }
    public string? Status { get; set; }
    public string SeriesCode { get; set; } = LeagueProfile.DefaultId;
    public string DriverName { get; set; } = string.Empty;
    public string DriverNumber { get; set; } = string.Empty;
    public string SeasonId { get; set; } = string.Empty;
    public string TeamId { get; set; } = string.Empty;
    public List<string> CeoTeamIds { get; set; } = new();
    public List<TeamJoinRequestEntry>? CeoPendingJoinRequests { get; set; }
    public bool CanAnnounce { get; set; }
    public bool CanManageEconomy { get; set; }
    public bool CanAddMembers { get; set; }
    public string Affiliation { get; set; } = string.Empty;
    public string LicenseCategory { get; set; } = string.Empty;
    public string RaceClass { get; set; } = string.Empty;
    public List<string> RaceClasses { get; set; } = new();
    public double SafetyRating { get; set; }
    public int SeasonRaceCount { get; set; }
    public int SeasonRaceLimit { get; set; }
    public int PenaltyPoints { get; set; }
    public int PenaltyPointsLimit { get; set; } = 12;
    public int? ChampionshipPosition { get; set; }
    public string AdministrativeNote { get; set; } = string.Empty;
    public DateTime? RestrictedUntil { get; set; }
    public DateTime? PurgeAfter { get; set; }
    public long RowVersion { get; set; }
}

public sealed record OnlineLeagueProfileResponse(OnlineLeagueProfile Profile, bool CanEdit);
public sealed record OnlineLeagueProfileSaveResult(long RowVersion, OnlineLeagueProfile? Profile = null);

public sealed record DriverGreetingResponse(string MessageKey, string Text, DateTimeOffset GeneratedAt);

/// <summary>The one shared race calendar's raw wire shape — see <c>OnlineDriverSession.FetchCalendarAsync</c>
/// for why <see cref="RacesJson"/> stays an opaque already-serialized blob instead of a typed list here.</summary>
public sealed record CalendarResponse(string RacesJson, DateTimeOffset? UpdatedAt);

public sealed record AccountCatalogRace(string Id, string Name, string Track);
public sealed record AccountCatalogSeason(string Id, string Name, AccountCatalogRace[] Races);
public sealed record AccountCatalogTeam(string Id, string Name);
public sealed record AccountCatalogSeries(string Id, string Name, AccountCatalogSeason[] Seasons,
    AccountCatalogTeam[] Teams, double SafetyRatingMinimum, double SafetyRatingMaximum, int PenaltyPointsLimit);
public sealed record AccountCatalogResponse(AccountCatalogSeries[] Series);

/// <summary>One entry in the actor-attributed change history for a league's online accounts.</summary>
public sealed record AccountActionRow(string Login, string Action, string? Actor, string Reason, DateTime CreatedAt);
public sealed record AccountActionList(AccountActionRow[] Actions);

public sealed record AccountCreateRequest(
    string LeagueId,
    string FirstName,
    string Surname,
    string DriverNumber,
    string? LegacyAccountId,
    string SeriesCode = "vrs",
    string SeasonId = "default",
    string TeamId = "");
public sealed record AccountSetStatusRequest(string LeagueId, string Login, string Status, string Reason,
    DateTime? RestrictedUntil = null);
public sealed record AccountPasswordChangeRequest(string LeagueId, string CurrentPassword, string NewPassword,
    string AccountType);
public sealed record AccountResetRequest(string LeagueId, string Login, string Reason);
public sealed record AccountReconcileRequest(string RequestId, string Reason);
public sealed record OperatorAccountRow(string Login, string UserId, string DisplayName, string Status, DateTime CreatedAt);
public sealed record OperatorAccountList(OperatorAccountRow[] Operators);
public sealed record OperatorCreateRequest(string LeagueId, string Login);
public sealed record OperatorSetStatusRequest(string LeagueId, string Login, string Status, string Reason);
public sealed record OperatorActivationReceipt(string Id, string Login, string Status, string ActivationCode);
