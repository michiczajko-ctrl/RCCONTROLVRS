using System.Security.Cryptography;
using System.Text;
using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

/// <summary>
/// Verifies a TeamHubAuthPayload against the account it claims to belong to. Pure,
/// stateless, independently testable — the Host owns the actual connection→login binding
/// (a ConcurrentDictionary in MainViewModel), this class only answers "is this token
/// correct for this account".
/// </summary>
public static class TeamHubAuthService
{
    public static bool Verify(UserAccount? account, string? providedToken)
    {
        if (account == null
            || string.IsNullOrEmpty(providedToken)
            || string.IsNullOrEmpty(account.TeamHubToken))
        {
            return false;
        }

        var expected = Encoding.UTF8.GetBytes(account.TeamHubToken);
        var actual = Encoding.UTF8.GetBytes(providedToken);
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
