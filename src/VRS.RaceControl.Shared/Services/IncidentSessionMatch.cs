namespace VRS.RaceControl.Shared.Services;

/// <summary>
/// Decides whether a stored incident belongs to the session on screen. A Relay-controlled session stores its incidents under the
/// session's durable id (a GUID), while the HOST shows the short session code (VRS-ABCD). Comparing only with the code hid every
/// incident of such a session from the RaceGuard queue.
/// </summary>
public static class IncidentSessionMatch
{
    public static bool IsCurrent(string? reportSessionId, string? sessionCode, string? durableSessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionCode)) return true;
        return string.Equals(reportSessionId, sessionCode, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(durableSessionId) && string.Equals(reportSessionId, durableSessionId, StringComparison.OrdinalIgnoreCase));
    }
}
