using VRS.RaceControl.Shared.Models;
using VRS.RaceControl.Shared.Protocol;

namespace VRS.RaceControl.Shared.Services;

public static class PanelAudioAnnouncementPolicy
{
    public static ProtocolMessage Source(TimedPanelTransitionPayload transition, RaceControlPanelStatePayload state)
    {
        var audio = transition.AudioAnnouncement;
        return new ProtocolMessage { Type = MessageType.Flag, Id = audio?.EventId ?? transition.TransitionId,
            AuthorityGeneration = audio?.Generation ?? state.AuthorityGeneration, Sequence = audio?.Revision ?? state.Revision,
            ClockEpoch = audio?.ClockEpoch ?? state.ClockEpoch, ExpiresAt = transition.TargetEffectiveAtHostTime.AddSeconds(10),
            AnnouncementPriority = AnnouncementPriority.P1RaceControl };
    }

    // Controller transfer does not revoke a committed transition. A different clock/flag does.
    public static bool IsCurrent(string? eventId, string? clockEpoch, SessionSnapshot state,
        TimedPanelTransitionPayload? scheduled)
    {
        if (string.IsNullOrEmpty(eventId) || clockEpoch != state.ClockEpoch || scheduled?.AudioAnnouncement?.EventId != eventId) return false;
        return state.Panel.Transition?.AudioAnnouncement?.EventId == eventId
            || (state.Panel.Transition == null && state.Panel.FlagState == scheduled.TargetState
                && state.ServerNow >= scheduled.TargetEffectiveAtHostTime
                && state.ServerNow < scheduled.TargetEffectiveAtHostTime.AddSeconds(10));
    }
}
