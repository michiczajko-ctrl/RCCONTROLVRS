namespace VRS.RaceControl.Shared.Services;

/// <summary>Rules that keep every HOST in a Relay session looking at the same map and the same cars.</summary>
public static class MapSharingPolicy
{
    /// <summary>
    /// Which car data a HOST's map uses. Its own game's samples are fresher and do not depend on the Relay, but in a Relay-controlled
    /// session only the HOST that holds the telemetry slot may use them: any other HOST (even one running LMU on its own PC) shows the
    /// shared fleet from the Relay, so all HOSTs see the same cars.
    /// </summary>
    public static bool UseLocalFleet(bool localFleetIsFresh, bool relayControlledSession, bool holdsTelemetrySlot) =>
        localFleetIsFresh && (!relayControlledSession || holdsTelemetrySlot);

    /// <summary>
    /// Whether the controlling HOST should publish its map to the session now: it has a verified map, the session does not already hold
    /// exactly this map, and this map was not already sent. Other HOSTs then load it by themselves.
    /// </summary>
    public static bool ShouldPublish(string? mapChecksum, string? lastPublishedChecksum, string? sessionMapChecksum) =>
        !string.IsNullOrEmpty(mapChecksum)
        && !string.Equals(mapChecksum, lastPublishedChecksum, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(mapChecksum, sessionMapChecksum, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a map arriving from the session should replace the one this HOST has. The controlling HOST sets the session's map, so
    /// it keeps its own verified map; every other HOST takes the shared one.
    /// </summary>
    public static bool AcceptSharedMap(bool isController, bool hasOwnVerifiedMap, bool forced) => forced || !(isController && hasOwnVerifiedMap);
}
