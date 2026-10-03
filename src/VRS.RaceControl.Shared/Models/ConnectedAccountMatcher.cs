namespace VRS.RaceControl.Shared.Models;

public static class ConnectedAccountMatcher
{
    public static bool Matches(DriverInfo driver, UserAccount account, bool online)
    {
        if (online)
        {
            if (!Guid.TryParse(driver.AccountUserId, out var connectedUserId)) return false;
            var linkedId = account.OnlineUserId ?? account.Id;
            return Guid.TryParse(linkedId, out var accountUserId)
                && connectedUserId == accountUserId;
        }

        return account.DriverName.Equals(driver.Name, StringComparison.OrdinalIgnoreCase)
            || account.Login.Equals(driver.Name, StringComparison.OrdinalIgnoreCase);
    }
}
