using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

public static class AuthorityAdmissionPolicy
{
    public static string[] MissingCapabilities(string role, IEnumerable<string>? capabilities)
    {
        var required = role == "driver"
            ? new[] { ProtocolCapabilities.SessionAuthority, ProtocolCapabilities.ScheduledPanelAudio }
            : new[] { ProtocolCapabilities.SessionAuthority, ProtocolCapabilities.ScheduledPanelAudio,
                ProtocolCapabilities.IncidentEvidence, ProtocolCapabilities.SharedTrackDefinitions, ProtocolCapabilities.AtomicIncidentPenalty };
        var supported = new HashSet<string>(capabilities ?? [], StringComparer.Ordinal);
        return required.Where(capability => !supported.Contains(capability)).ToArray();
    }
}
