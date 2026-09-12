namespace VRS.RaceControl.Shared.Models;

public sealed record AccountRequestInput(string RequestId, string LeagueId, string Login,
    string DriverName, string DriverNumber, string Message, string StatusToken, bool Recovery = false);
public sealed record AccountRequestReceipt(string Id, string Status, string? ActivationCode = null);
public sealed record AccountRequestRow(string Id, string LeagueId, string Login, string DriverName,
    string DriverNumber, string Message, string Status, bool Recovery, DateTime CreatedAt);
public sealed record AccountDecision(string RequestId, bool Approve, string Login,
    string DriverNumber, string Reason, string? LegacyAccountId = null);

/// <summary>
/// Narrow shape of an online account as returned by /accounts/login — deliberately not the
/// full local <c>UserAccount</c> model (which carries PasswordHash/PasswordSalt/TeamHubToken
/// that the server never sends and this flow never needs).
/// </summary>
public sealed record OnlineAccountProfile(string Id, string Login, string DriverName, string DriverNumber, string LeagueId);

public static class AccountRequestRules
{
    public static string? Validate(AccountRequestInput input)
    {
        if (!Guid.TryParse(input.RequestId, out _) || !Guid.TryParse(input.LeagueId, out _)) return "Invalid league/request id";
        if (input.Login.Trim().Length is < 2 or > 80 || input.Login.Any(char.IsControl)) return "Invalid login";
        if (input.DriverName.Trim().Length is < 2 or > 80) return "Invalid driver name";
        if (input.DriverNumber.Length > 20 || input.Message.Length > 500) return "Input too long";
        if (input.StatusToken.Length is < 40 or > 128) return "Invalid status token";
        return null;
    }
}
