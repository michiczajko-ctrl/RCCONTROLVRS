using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

public enum TeamJoinRequestValidationOutcome
{
    Success,
    RequesterNotFound,
    TeamNotFound,
    AlreadyOnTeam,
    AlreadyPending
}

/// <summary>
/// Pure validation for a driver's "apply to join a team" request, mirroring
/// <see cref="TeamAddMemberValidator"/>'s shape so both team-membership flows are checked
/// the same way — free of any dependency on live sockets/dispatchers so it's unit-testable
/// on its own. Unlike TeamAddMemberValidator this never checks a permission flag: an
/// application requires no prior team standing, only that the target team exists and the
/// requester isn't already on it or already waiting on the same application.
/// </summary>
public static class TeamJoinRequestValidator
{
    public static (TeamJoinRequestValidationOutcome Outcome, string? MatchedTeamName) Validate(
        UserAccount? requester,
        LeagueProfile? leagueProfile,
        string teamName)
    {
        if (requester == null)
        {
            return (TeamJoinRequestValidationOutcome.RequesterNotFound, null);
        }

        var matchedTeam = leagueProfile?.Teams.FirstOrDefault(t =>
            t.Equals(teamName, StringComparison.OrdinalIgnoreCase));
        if (matchedTeam == null)
        {
            return (TeamJoinRequestValidationOutcome.TeamNotFound, null);
        }

        if (!string.IsNullOrWhiteSpace(requester.Team)
            && requester.Team.Equals(matchedTeam, StringComparison.OrdinalIgnoreCase))
        {
            return (TeamJoinRequestValidationOutcome.AlreadyOnTeam, null);
        }

        if (leagueProfile!.PendingJoinRequests.Any(r =>
                r.Login.Equals(requester.Login, StringComparison.OrdinalIgnoreCase)
                && r.TeamName.Equals(matchedTeam, StringComparison.OrdinalIgnoreCase)))
        {
            return (TeamJoinRequestValidationOutcome.AlreadyPending, null);
        }

        return (TeamJoinRequestValidationOutcome.Success, matchedTeam);
    }
}
