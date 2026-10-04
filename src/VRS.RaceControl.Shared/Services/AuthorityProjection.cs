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
        return result with { PrivatePanels = null, PrivateTexts = null, ActiveText = text,
            StandingGrid = null, StandingStartArmedAt = null };
    }
    private readonly object _gate = new();
    private readonly string _sessionId;
    private SessionSnapshot? _current;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public AuthorityProjection(string sessionId) => _sessionId = sessionId;
    private static SessionSnapshot Copy(SessionSnapshot value) =>
        JsonSerializer.Deserialize<SessionSnapshot>(JsonSerializer.Serialize(value, Json), Json)!;
    public SessionSnapshot? Current { get { lock (_gate) return _current == null ? null : Copy(_current); } }

    /// <summary>Why the most recent snapshot was refused (null after an accepted one). Never contains session data.</summary>
    public string? LastRejection { get; private set; }

    /// <summary>The first failed structural check, by name, or null when the snapshot is well formed for this session.</summary>
    private string? Invalid(SessionSnapshot snapshot)
    {
        if (snapshot.SessionId != _sessionId) return "session id differs from the joined session";
        if (snapshot.Generation < 1) return "generation below 1";
        if (snapshot.Revision < 0) return "negative revision";
        if (string.IsNullOrWhiteSpace(snapshot.ClockEpoch) || snapshot.ClockEpoch.Length > 128) return "clock epoch missing or too long";
        if (snapshot.ServerNow == default) return "server time missing";
        if (snapshot.Panel == null) return "panel missing";
        if (snapshot.Panel.Validate() is { } panelError) return "panel invalid: " + panelError;
        if (string.IsNullOrWhiteSpace(snapshot.ControllerId) != !snapshot.LeaseExpiresAt.HasValue) return "controller and lease do not match";
        if (snapshot.Panel.AuthorityGeneration != snapshot.Generation || snapshot.Panel.Revision != snapshot.Revision
            || snapshot.Panel.ClockEpoch != snapshot.ClockEpoch || snapshot.Panel.EpochId != snapshot.ClockEpoch)
            return "panel generation/revision/epoch stamp differs from the snapshot";
        if (snapshot.Policy?.IsValid != true) return "speeding policy missing or invalid";
        if (snapshot.PolicyRevision < 0) return "negative policy revision";
        if (snapshot.Qualifying == null) return "qualifying list missing";
        if (snapshot.ImpactPolicy?.IsValid == false || snapshot.ImpactPolicyRevision < 0) return "impact policy invalid";
        if (snapshot.AdvancedPolicy?.IsValid == false || snapshot.AdvancedPolicyRevision < 0) return "advanced rule policy invalid";
        if (snapshot.StandingGrid?.IsValid == false) return "standing grid invalid";
        if (snapshot.StandingStartArmedAt.HasValue != (snapshot.StandingGrid != null)) return "standing start arm and grid do not match";
        if (snapshot.TrackDefinition?.IsValid == false) return "track definition reference invalid";
        if (snapshot.TrackLayoutBinding?.IsValid == false) return "track layout binding invalid";
        if (snapshot.TrackLayoutBinding != null && snapshot.TrackLayoutBinding.Checksum != snapshot.TrackDefinition?.Checksum)
            return "track layout binding does not match the track definition";
        if (snapshot.Qualifying.Count != 2 || snapshot.Qualifying.Any(q => q == null)) return "qualifying must list exactly two classes";
        if (snapshot.Qualifying.Select(q => q.RaceClass).Distinct().Count() != 2) return "qualifying classes are not distinct";
        if (snapshot.Qualifying.Any(q => q.RaceClass is not ("GT3" or "HYPERCAR")))
            return "qualifying class name not GT3/HYPERCAR: " + string.Join("/", snapshot.Qualifying.Select(q => q.RaceClass));
        if (snapshot.Qualifying.Any(q => q.Phase is not ("Inactive" or "Armed" or "Running" or "Ended")))
            return "qualifying phase not recognised: " + string.Join("/", snapshot.Qualifying.Select(q => q.Phase));
        if (snapshot.Qualifying.Any(q => (q.Phase == "Running") != q.EndsAt.HasValue)) return "qualifying end time does not match its phase";
        if (snapshot.Qualifying.Any(q => q.DurationSeconds is < 1 or > 86400)) return "qualifying duration out of range";
        return null;
    }

    public bool TryApply(SessionSnapshot snapshot)
    {
        if (Invalid(snapshot) is { } invalid) { LastRejection = invalid; return false; }
        lock (_gate)
        {
            if (_current != null && (snapshot.Generation < _current.Generation
                || snapshot.Revision < _current.Revision
                || (snapshot.ClockEpoch != _current.ClockEpoch && snapshot.Generation <= _current.Generation)))
            { LastRejection = "older than the state already applied"; return false; }
            // Equal revisions can renew the lease and refresh server time, but cannot rewrite domain state.
            if (_current != null && snapshot.Revision == _current.Revision
                && JsonSerializer.Serialize(snapshot with { LeaseExpiresAt = _current.LeaseExpiresAt,
                    ServerNow = _current.ServerNow }, Json) != JsonSerializer.Serialize(_current, Json))
            {
                // Panel.HostNow is also a presentation timestamp refreshed in snapshots.
                var normalized = Copy(snapshot with { LeaseExpiresAt = _current.LeaseExpiresAt, ServerNow = _current.ServerNow });
                normalized.Panel.HostNow = _current.Panel.HostNow;
                if (JsonSerializer.Serialize(normalized, Json) != JsonSerializer.Serialize(_current, Json))
                { LastRejection = "same revision but the state differs from the one already applied"; return false; }
            }
            if (_current != null && snapshot.Revision == _current.Revision && snapshot.ServerNow < _current.ServerNow)
            { LastRejection = "same revision with an older server time"; return false; }
            _current = Copy(snapshot);
            LastRejection = null;
            return true;
        }
    }
}
