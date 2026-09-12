using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

public enum TeamAddMemberValidationOutcome
{
    Success,
    RequesterNotFound,
    RequesterHasNoTeam,
    PermissionDenied,
    TargetNotFound
}

/// <summary>
/// Pure validation for a driver's "add member to my team" request, deliberately free of any
/// dependency on live sockets/dispatchers so it's unit-testable on its own. The Host is the
/// only caller that acts on the result — it never trusts a client-side-only check, since the
/// requester's team and permission here are always resolved from the Host's own account/profile
/// data, not from anything the requesting client asserts.
/// </summary>
public static class TeamAddMemberValidator
{
    public static (TeamAddMemberValidationOutcome Outcome, UserAccount? Target) Validate(
        UserAccount? requester,
        LeagueProfile? leagueProfile,
        IReadOnlyList<UserAccount> allAccounts,
        string targetLogin)
    {
        if (requester == null)
        {
            return (TeamAddMemberValidationOutcome.RequesterNotFound, null);
        }
        if (string.IsNullOrWhiteSpace(requester.Team))
        {
            return (TeamAddMemberValidationOutcome.RequesterHasNoTeam, null);
        }

        var permission = leagueProfile?.FindMemberPermission(requester.Team, requester.Login);
        if (permission == null || !permission.CanAddMembers)
        {
            return (TeamAddMemberValidationOutcome.PermissionDenied, null);
        }

        var target = allAccounts.FirstOrDefault(a =>
            a.Login.Equals(targetLogin, StringComparison.OrdinalIgnoreCase));
        if (target == null)
        {
            return (TeamAddMemberValidationOutcome.TargetNotFound, null);
        }

        return (TeamAddMemberValidationOutcome.Success, target);
    }
}
