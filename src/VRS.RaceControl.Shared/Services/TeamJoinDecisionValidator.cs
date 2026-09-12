using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

public enum TeamJoinDecisionValidationOutcome
{
    Success,
    RequesterNotFound,
    NotCeoOfTeam,
    EntryNotFound
}

/// <summary>
/// Pure validation for a team CEO's accept/reject decision on a pending join application —
/// mirrors <see cref="TeamAddMemberValidator"/>'s shape, free of any dependency on live
/// sockets/dispatchers so it's unit-testable on its own. The requester must be the
/// designated CEO of the exact team the pending entry belongs to; CEO status and the
/// pending entry are always resolved from the Host's own data, never from anything the
/// client asserts.
/// </summary>
public static class TeamJoinDecisionValidator
{
    public static (TeamJoinDecisionValidationOutcome Outcome, TeamJoinRequestEntry? Entry) Validate(
        UserAccount? requester,
        LeagueProfile? leagueProfile,
        string targetLogin,
        string teamName)
    {
        if (requester == null)
        {
            return (TeamJoinDecisionValidationOutcome.RequesterNotFound, null);
        }

        var permission = leagueProfile?.FindMemberPermission(teamName, requester.Login);
        if (permission == null || !permission.IsCeo)
        {
            return (TeamJoinDecisionValidationOutcome.NotCeoOfTeam, null);
        }

        var entry = leagueProfile!.PendingJoinRequests.FirstOrDefault(r =>
            r.Login.Equals(targetLogin, StringComparison.OrdinalIgnoreCase)
            && r.TeamName.Equals(teamName, StringComparison.OrdinalIgnoreCase));
        if (entry == null)
        {
            return (TeamJoinDecisionValidationOutcome.EntryNotFound, null);
        }

        return (TeamJoinDecisionValidationOutcome.Success, entry);
    }
}
