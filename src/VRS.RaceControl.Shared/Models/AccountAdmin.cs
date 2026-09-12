namespace VRS.RaceControl.Shared.Models;

/// <summary>HOST-admin view of an online login account (narrow — never carries credentials).</summary>
public sealed record AccountRosterRow(string Login, string Status, string? LegacyAccountId, string? DriverName, string? DriverNumber);
public sealed record AccountRosterList(AccountRosterRow[] Accounts);

/// <summary>One entry in the actor-attributed change history for a league's online accounts.</summary>
public sealed record AccountActionRow(string Login, string Action, string? Actor, string Reason, DateTime CreatedAt);
public sealed record AccountActionList(AccountActionRow[] Actions);

public sealed record AccountCreateRequest(string LeagueId, string Login, string DriverName, string DriverNumber, string? LegacyAccountId);
public sealed record AccountSetStatusRequest(string LeagueId, string Login, string Status, string Reason);
public sealed record AccountResetRequest(string LeagueId, string Login, string Reason);
public sealed record AccountReconcileRequest(string RequestId, string Reason);
