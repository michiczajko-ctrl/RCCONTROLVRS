using System.Text.Json;
using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

/// <summary>A snapshot changes clock epoch only together with a newer authority generation.</summary>
public sealed class AuthorityProjection
{
    public static bool IsCritical(Enums.FlagType state) => state is Enums.FlagType.Red
        or Enums.FlagType.FullCourseYellow or Enums.FlagType.SafetyCar or Enums.FlagType.VirtualSafetyCar
        or Enums.FlagType.ReadyForGreen or Enums.FlagType.Checkered;

    /// <summary>Only this authenticated user's private state is placed in their wire projection.</summary>
    public static SessionSnapshot ForDriver(SessionSnapshot snapshot, string userId)
    {
        var result = Copy(snapshot);
        if (result.Panel.Transition == null && !IsCritical(result.Panel.FlagState)
            && result.PrivatePanels?.TryGetValue(userId, out var instruction) == true)
        {
            result.Panel.FlagState = instruction.FlagState;
            result.Panel.FlashMode = instruction.FlashMode;
            result.Panel.FlashEpochHostTime = instruction.FlashEpochHostTime;
        }
        var text = result.PrivateTexts?.TryGetValue(userId, out var privateText) == true
            && privateText.ExpiresAt > result.ServerNow ? privateText : result.ActiveText;
        return result with { PrivatePanels = null, PrivateTexts = null, ActiveText = text };
    }
    private readonly object _gate = new();
    private readonly string _sessionId;
    private SessionSnapshot? _current;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public AuthorityProjection(string sessionId) => _sessionId = sessionId;
    private static SessionSnapshot Copy(SessionSnapshot value) =>
        JsonSerializer.Deserialize<SessionSnapshot>(JsonSerializer.Serialize(value, Json), Json)!;
    public SessionSnapshot? Current { get { lock (_gate) return _current == null ? null : Copy(_current); } }

    public bool TryApply(SessionSnapshot snapshot)
    {
        if (snapshot.SessionId != _sessionId || snapshot.Generation < 1 || snapshot.Revision < 0
            || string.IsNullOrWhiteSpace(snapshot.ClockEpoch) || snapshot.ClockEpoch.Length > 128
            || snapshot.ServerNow == default || snapshot.Panel == null || snapshot.Panel.Validate() != null
            || string.IsNullOrWhiteSpace(snapshot.ControllerId) != !snapshot.LeaseExpiresAt.HasValue
            || snapshot.Panel.AuthorityGeneration != snapshot.Generation || snapshot.Panel.Revision != snapshot.Revision
            || snapshot.Panel.ClockEpoch != snapshot.ClockEpoch || snapshot.Panel.EpochId != snapshot.ClockEpoch
            || snapshot.Policy?.IsValid != true || snapshot.PolicyRevision < 0 || snapshot.Qualifying == null
            || snapshot.ImpactPolicy?.IsValid == false || snapshot.ImpactPolicyRevision < 0
            || snapshot.TrackDefinition?.IsValid == false
            || snapshot.TrackLayoutBinding?.IsValid == false
            || (snapshot.TrackLayoutBinding != null && snapshot.TrackLayoutBinding.Checksum != snapshot.TrackDefinition?.Checksum)
            || snapshot.Qualifying.Count != 2 || snapshot.Qualifying.Any(q => q == null)
            || snapshot.Qualifying.Select(q => q.RaceClass).Distinct().Count() != 2
            || snapshot.Qualifying.Any(q => q.RaceClass is not ("GT3" or "HYPERCAR")
                || q.Phase is not ("Inactive" or "Armed" or "Running" or "Ended")
                || (q.Phase == "Running") != q.EndsAt.HasValue || q.DurationSeconds is < 1 or > 86400))
            return false;
        lock (_gate)
        {
            if (_current != null && (snapshot.Generation < _current.Generation
                || snapshot.Revision < _current.Revision
                || (snapshot.ClockEpoch != _current.ClockEpoch && snapshot.Generation <= _current.Generation)))
                return false;
            // Equal revisions can renew the lease and refresh server time, but cannot rewrite domain state.
            if (_current != null && snapshot.Revision == _current.Revision
                && JsonSerializer.Serialize(snapshot with { LeaseExpiresAt = _current.LeaseExpiresAt,
                    ServerNow = _current.ServerNow }, Json) != JsonSerializer.Serialize(_current, Json))
            {
                // Panel.HostNow is also a presentation timestamp refreshed in snapshots.
                var normalized = Copy(snapshot with { LeaseExpiresAt = _current.LeaseExpiresAt, ServerNow = _current.ServerNow });
                normalized.Panel.HostNow = _current.Panel.HostNow;
                if (JsonSerializer.Serialize(normalized, Json) != JsonSerializer.Serialize(_current, Json)) return false;
            }
            if (_current != null && snapshot.Revision == _current.Revision && snapshot.ServerNow < _current.ServerNow)
                return false;
            _current = Copy(snapshot);
            return true;
        }
    }
}
