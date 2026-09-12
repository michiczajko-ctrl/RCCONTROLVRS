using System.Text.Json.Serialization;
using VRS.RaceControl.Shared.Enums;

namespace VRS.RaceControl.Shared.Models;

/// <summary>
/// Payload for flag messages.
/// </summary>
public class FlagPayload
{
    [JsonPropertyName("flagType")]
    public FlagType FlagType { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("displayDurationMs")]
    public int DisplayDurationMs { get; set; } = 5000;

    /// <summary>
    /// True when a timed panel transition owns the recorded cue. New clients suppress
    /// the ordinary flag-audio route so the cue can be scheduled against HOST time.
    /// Older clients safely ignore this additive field.
    /// </summary>
    [JsonPropertyName("panelTransitionOwnsAudio")]
    public bool PanelTransitionOwnsAudio { get; set; }
}

/// <summary>
/// Payload for penalty messages.
/// </summary>
public class PenaltyPayload
{
    [JsonPropertyName("penaltyType")]
    public PenaltyType PenaltyType { get; set; }

    [JsonPropertyName("driverName")]
    public string DriverName { get; set; } = string.Empty;

    [JsonPropertyName("driverNumber")]
    public string DriverNumber { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    /// <summary>Duration in seconds for time penalties and stop & go.</summary>
    [JsonPropertyName("durationSeconds")]
    public int DurationSeconds { get; set; } = 0;

    /// <summary>Host-only investigation note (not displayed on driver overlay).</summary>
    [JsonPropertyName("note")]
    public string Note { get; set; } = string.Empty;

    [JsonPropertyName("displayDurationMs")]
    public int DisplayDurationMs { get; set; } = 10000;
}

/// <summary>
/// Payload for join request messages (local server).
/// </summary>
public class JoinRequestPayload
{
    [JsonPropertyName("driverName")]
    public string DriverName { get; set; } = string.Empty;

    [JsonPropertyName("sessionCode")]
    public string SessionCode { get; set; } = string.Empty;
}

/// <summary>
/// Unified join payload used by the cloud relay.
/// Role = "host" or "driver".
/// </summary>
public class JoinPayload
{
    [JsonPropertyName("driverName")]
    public string DriverName { get; set; } = string.Empty;

    [JsonPropertyName("sessionCode")]
    public string SessionCode { get; set; } = string.Empty;

    /// <summary>"host" or "driver"</summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = "driver";

    /// <summary>
    /// Short-lived token issued by the authenticated Race Control API. Public relay
    /// deployments require this token and derive identity, session and role from it.
    /// Legacy fields above remain only for an explicitly enabled development transition.
    /// </summary>
    [JsonPropertyName("sessionJoinToken")]
    public string SessionJoinToken { get; set; } = string.Empty;

    /// <summary>Optional feature names. Older protocol 1.0 peers ignore this property.</summary>
    [JsonPropertyName("capabilities")]
    public string[] Capabilities { get; set; } = [];
}

/// <summary>
/// Payload for join acknowledgement messages.
/// </summary>
public class JoinAckPayload
{
    [JsonPropertyName("driverId")]
    public string DriverId { get; set; } = string.Empty;

    [JsonPropertyName("sessionInfo")]
    public SessionInfo? SessionInfo { get; set; }

    /// <summary>Features accepted by the relay for this connection.</summary>
    [JsonPropertyName("capabilities")]
    public string[] Capabilities { get; set; } = [];
}

/// <summary>
/// Payload for join rejection messages.
/// </summary>
public class JoinRejectPayload
{
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Payload for text messages.
/// </summary>
public class TextMessagePayload
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("displayDurationMs")]
    public int DisplayDurationMs { get; set; } = 8000;
}

/// <summary>
/// Live telemetry snapshot produced by the existing Assetto Corsa bridge.
/// Nullable fields mean the current bridge/game state does not expose the value.
/// </summary>
public class TelemetrySnapshotPayload
{
    [JsonPropertyName("timestampUtc")]
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("isAcConnected")]
    public bool IsAcConnected { get; set; }

    [JsonPropertyName("simulator")]
    public string Simulator { get; set; } = "AssettoCorsa";

    [JsonPropertyName("isConnected")]
    public bool IsConnected { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("driverName")]
    public string? DriverName { get; set; }

    [JsonPropertyName("carNumber")]
    public string? CarNumber { get; set; }

    [JsonPropertyName("carClass")]
    public string? CarClass { get; set; }

    [JsonPropertyName("sessionType")]
    public string? SessionType { get; set; }

    [JsonPropertyName("sessionTimeLeft")]
    public float? SessionTimeLeft { get; set; }

    [JsonPropertyName("currentLap")]
    public int? CurrentLap { get; set; }

    [JsonPropertyName("completedLaps")]
    public int? CompletedLaps { get; set; }

    [JsonPropertyName("totalLaps")]
    public int? TotalLaps { get; set; }

    [JsonPropertyName("fuelLiters")]
    public float? FuelLiters { get; set; }

    [JsonPropertyName("maxFuelLiters")]
    public float? MaxFuelLiters { get; set; }

    [JsonPropertyName("fuelPerLap")]
    public float? FuelPerLap { get; set; }

    [JsonPropertyName("usedFuel")]
    public float? UsedFuel { get; set; }

    [JsonPropertyName("estimatedLapsLeft")]
    public int? EstimatedLapsLeft { get; set; }

    [JsonPropertyName("speedKmh")]
    public float? SpeedKmh { get; set; }

    [JsonPropertyName("gear")]
    public int? Gear { get; set; }

    [JsonPropertyName("rpm")]
    public int? Rpm { get; set; }

    [JsonPropertyName("currentLapTimeMs")]
    public int? CurrentLapTimeMs { get; set; }

    [JsonPropertyName("lastLapTimeMs")]
    public int? LastLapTimeMs { get; set; }

    [JsonPropertyName("bestLapTimeMs")]
    public int? BestLapTimeMs { get; set; }

    [JsonPropertyName("currentLapTime")]
    public string? CurrentLapTime { get; set; }

    [JsonPropertyName("lastLapTime")]
    public string? LastLapTime { get; set; }

    [JsonPropertyName("bestLapTime")]
    public string? BestLapTime { get; set; }

    [JsonPropertyName("delta")]
    public string? Delta { get; set; }

    [JsonPropertyName("position")]
    public int? Position { get; set; }

    [JsonPropertyName("driverCount")]
    public int? DriverCount { get; set; }

    [JsonPropertyName("isInPit")]
    public bool? IsInPit { get; set; }

    [JsonPropertyName("isInPitLane")]
    public bool? IsInPitLane { get; set; }

    [JsonPropertyName("pitLimiterOn")]
    public bool? PitLimiterOn { get; set; }

    [JsonPropertyName("flag")]
    public FlagType? Flag { get; set; }

    [JsonPropertyName("tyreCoreTemperature")]
    public float[]? TyreCoreTemperature { get; set; }

    [JsonPropertyName("tyrePressure")]
    public float[]? TyrePressure { get; set; }

    [JsonPropertyName("tyreWear")]
    public float[]? TyreWear { get; set; }

    [JsonPropertyName("brakeTemperature")]
    public float[]? BrakeTemperature { get; set; }

    [JsonPropertyName("carDamage")]
    public float[]? CarDamage { get; set; }

    [JsonPropertyName("carModel")]
    public string? CarModel { get; set; }

    [JsonPropertyName("trackName")]
    public string? TrackName { get; set; }

    [JsonPropertyName("trackConfiguration")]
    public string? TrackConfiguration { get; set; }

    [JsonPropertyName("tyreCompound")]
    public string? TyreCompound { get; set; }

    [JsonPropertyName("worldPosition")]
    public double[]? WorldPosition { get; set; }

    [JsonPropertyName("normalizedTrackPosition")]
    public double? NormalizedTrackPosition { get; set; }

    [JsonPropertyName("sessionElapsedSeconds")]
    public double? SessionElapsedSeconds { get; set; }

    [JsonPropertyName("telemetryAvailable")]
    public bool TelemetryAvailable => IsConnected || IsAcConnected;
}

/// <summary>
/// Public driver data synchronized to the authenticated client. Password
/// hashes, salts and administrative notes are deliberately excluded.
/// </summary>
public sealed class DriverProfilePayload
{
    public string DriverId { get; set; } = string.Empty;
    public string DriverName { get; set; } = string.Empty;
    public string Login { get; set; } = string.Empty;
    public string DriverNumber { get; set; } = string.Empty;
    public string LeagueId { get; set; } = string.Empty;
    public string SeasonId { get; set; } = string.Empty;
    public string Team { get; set; } = string.Empty;
    public string Affiliation { get; set; } = string.Empty;
    public string LicenseCategory { get; set; } = string.Empty;
    public double SafetyRating { get; set; }
    public int SeasonRaceCount { get; set; }
    public int SeasonRaceLimit { get; set; }
    public int PenaltyPoints { get; set; }
    public int PenaltyPointsLimit { get; set; }
    public int? ChampionshipPosition { get; set; }
    public AccountStatus Status { get; set; }
    /// <summary>
    /// This account's granted permissions within its own <see cref="Team"/> — populated
    /// from <see cref="LeagueProfile.TeamPermissions"/> when a profile is supplied to
    /// <see cref="FromAccount"/>, otherwise left at the least-privilege default (all false).
    /// </summary>
    public bool CanAnnounce { get; set; }
    public bool CanManageEconomy { get; set; }
    public bool CanAddMembers { get; set; }
    public bool IsCeo { get; set; }

    public static DriverProfilePayload FromAccount(UserAccount account, LeagueProfile? leagueProfile = null)
    {
        var permission = leagueProfile?.FindMemberPermission(account.Team, account.Login);
        return new DriverProfilePayload
        {
            DriverId = account.Id,
            DriverName = account.DriverName,
            Login = account.Login,
            DriverNumber = account.DriverNumber,
            LeagueId = account.LeagueId,
            SeasonId = account.SeasonId,
            Team = account.Team,
            Affiliation = account.Affiliation,
            LicenseCategory = account.LicenseCategory,
            SafetyRating = account.SafetyRating,
            SeasonRaceCount = account.SeasonRaceCount,
            SeasonRaceLimit = account.SeasonRaceLimit,
            PenaltyPoints = account.PenaltyPoints,
            PenaltyPointsLimit = account.PenaltyPointsLimit,
            ChampionshipPosition = account.ChampionshipPosition,
            Status = account.Status,
            CanAnnounce = permission?.CanAnnounce ?? false,
            CanManageEconomy = permission?.CanManageEconomy ?? false,
            CanAddMembers = permission?.CanAddMembers ?? false,
            IsCeo = permission?.IsCeo ?? false
        };
    }
}

/// <summary>
/// Payload for ACK messages.
/// </summary>
public class AckPayload
{
    [JsonPropertyName("originalMessageId")]
    public string OriginalMessageId { get; set; } = string.Empty;
}

/// <summary>
/// Payload for a driver's app-version announcement to the HOST.
/// </summary>
public class ClientVersionAnnouncePayload
{
    [JsonPropertyName("appVersion")]
    public string AppVersion { get; set; } = string.Empty;
}

/// <summary>
/// Payload for driver list broadcast.
/// </summary>
public class DriverListPayload
{
    [JsonPropertyName("drivers")]
    public List<DriverInfo> Drivers { get; set; } = new();
}

public sealed class TelemetryBridgeStatusPayload
{
    [JsonPropertyName("simulator")]
    public string Simulator { get; set; } = "None";

    [JsonPropertyName("source")]
    public string Source { get; set; } = "Unavailable";

    [JsonPropertyName("state")]
    public string State { get; set; } = "Disconnected";

    [JsonPropertyName("lastPacketUtc")]
    public DateTime? LastPacketUtc { get; set; }

    [JsonPropertyName("reconnectCount")]
    public int ReconnectCount { get; set; }

    [JsonPropertyName("detail")]
    public string Detail { get; set; } = string.Empty;
}

/// <summary>
/// Payload for registering or syncing a single user account with host.
/// </summary>
public class AccountRegisterPayload
{
    [JsonPropertyName("account")]
    public UserAccount Account { get; set; } = new();
}

/// <summary>
/// Payload for broadcasting the account roster to every connected driver. Uses
/// <see cref="DriverProfilePayload"/> (not the raw <see cref="UserAccount"/>) so that
/// password hashes/salts are never sent to peers other than the account's own owner —
/// this payload previously carried full <see cref="UserAccount"/> objects, which meant
/// every connected driver's password hash and salt was broadcast to every other
/// connected driver on every connect/register event (a real, since-fixed loophole).
/// </summary>
public class AccountSyncPayload
{
    [JsonPropertyName("accounts")]
    public List<DriverProfilePayload> Accounts { get; set; } = new();
}

/// <summary>
/// Client → Host request to add another driver to the requester's own team. Carries only
/// the target's login — the Host always derives which team the request applies to from
/// the requester's own account, never from a client-asserted team name, so a request
/// can't be spoofed into adding someone to a team the requester doesn't belong to.
/// </summary>
public sealed class TeamAddMemberRequestPayload
{
    [JsonPropertyName("targetLogin")]
    public string TargetLogin { get; set; } = string.Empty;
}

/// <summary>
/// Host → requester result of a <see cref="TeamAddMemberRequestPayload"/>.
/// </summary>
public sealed class TeamAddMemberResultPayload
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
    [JsonPropertyName("targetLogin")]
    public string TargetLogin { get; set; } = string.Empty;
    [JsonPropertyName("targetDriverName")]
    public string TargetDriverName { get; set; } = string.Empty;
}

/// <summary>
/// Client → Host application to join a specific team by name. Carries no permission or
/// membership claim — the HOST always queues this in LeagueProfile.PendingJoinRequests
/// for manual review rather than applying it immediately (see TeamJoinRequestValidator).
/// </summary>
public sealed class TeamJoinRequestPayload
{
    [JsonPropertyName("teamName")]
    public string TeamName { get; set; } = string.Empty;
}

/// <summary>
/// Host → requester immediate acknowledgement of a <see cref="TeamJoinRequestPayload"/> —
/// confirms the application was received and queued (or rejected outright), not that it
/// was later accepted by a Host operator.
/// </summary>
public sealed class TeamJoinResultPayload
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
    [JsonPropertyName("teamName")]
    public string TeamName { get; set; } = string.Empty;
}

/// <summary>
/// Team CEO → Host decision on a pending join application. The HOST always resolves and
/// validates the requester's own CEO status server-side from its own account/profile data
/// — a client-asserted "I am the CEO" is never trusted (see TeamJoinDecisionValidator).
/// </summary>
public sealed class TeamJoinDecisionPayload
{
    [JsonPropertyName("targetLogin")]
    public string TargetLogin { get; set; } = string.Empty;
    [JsonPropertyName("teamName")]
    public string TeamName { get; set; } = string.Empty;
    [JsonPropertyName("approve")]
    public bool Approve { get; set; }
}

/// <summary>Host → CEO result of a <see cref="TeamJoinDecisionPayload"/>.</summary>
public sealed class TeamJoinDecisionResultPayload
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Driver → Host, sent immediately after connecting to the Team Hub channel. Binds this
/// connection to the account whose UserAccount.TeamHubToken matches AuthToken — see
/// TeamHubAuthService.Verify. Never broadcast, never logged in full (RotatingFileLogger's
/// redaction only catches known keywords, so callers must not string-interpolate AuthToken
/// into a log message either).
/// </summary>
public sealed class TeamHubAuthPayload
{
    [JsonPropertyName("login")]
    public string Login { get; set; } = string.Empty;
    [JsonPropertyName("authToken")]
    public string AuthToken { get; set; } = string.Empty;
}

/// <summary>Host → driver result of a <see cref="TeamHubAuthPayload"/> attempt.</summary>
public sealed class TeamHubAuthResultPayload
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Host → driver, pushed automatically on Team Hub connect (mirrors LeagueProfile/
/// AccountSync/DriverProfileUpdate) and after any local economy change while the driver is
/// connected (MainViewModel.OnEconomyChanged). Scoped to exactly ONE team — the connecting
/// driver's own — never broadcast, so no client ever learns another team's finances (plan
/// L.1/L.2/L.3's anti-leak principle extended to money). Every number here (Balance,
/// Status, ProjectedBalance, ShowDetails) is Host-computed; see EconomyService.BuildSnapshot.
/// </summary>
public sealed class TeamEconomySnapshotPayload
{
    [JsonPropertyName("teamId")]
    public string TeamId { get; set; } = string.Empty;
    [JsonPropertyName("teamName")]
    public string TeamName { get; set; } = string.Empty;
    [JsonPropertyName("currencyCode")]
    public string CurrencyCode { get; set; } = "VRC";
    [JsonPropertyName("currencyName")]
    public string CurrencyName { get; set; } = "kredyt";
    [JsonPropertyName("balance")]
    public long Balance { get; set; }
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
    [JsonPropertyName("nextRoundName")]
    public string? NextRoundName { get; set; }
    [JsonPropertyName("nextRoundNetChange")]
    public long? NextRoundNetChange { get; set; }
    [JsonPropertyName("projectedBalance")]
    public long? ProjectedBalance { get; set; }
    /// <summary>Gate for the Client's level-2 (Expander) detail — resolved Host-side from
    /// the CONNECTING driver's own TeamMemberPermission (IsCeo || CanManageEconomy), never
    /// asserted by the Client. When false, Contracts/RecentEntries are always empty.</summary>
    [JsonPropertyName("showDetails")]
    public bool ShowDetails { get; set; }
    [JsonPropertyName("contracts")]
    public List<TeamContract> Contracts { get; set; } = new();
    [JsonPropertyName("recentEntries")]
    public List<EconomyLedgerEntry> RecentEntries { get; set; } = new();
    /// <summary>This team's own pending contract offers awaiting Host approval (Faza 8) —
    /// like Contracts/RecentEntries, only populated when ShowDetails is true.</summary>
    [JsonPropertyName("pendingOffers")]
    public List<ContractOffer> PendingOffers { get; set; } = new();
    [JsonPropertyName("generatedAtUtc")]
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// CEO/economy-manager → Host: propose a contract for an existing driver. Carries no TeamId
/// (same anti-spoofing shape as TeamAddMemberRequestPayload) — the Host always derives the
/// proposer's team from their own account server-side, never from a client-asserted value.
/// </summary>
public sealed class TeamContractOfferRequestPayload
{
    [JsonPropertyName("targetLogin")]
    public string TargetLogin { get; set; } = string.Empty;
    [JsonPropertyName("salaryPerRound")]
    public long SalaryPerRound { get; set; }
    [JsonPropertyName("signingFee")]
    public long SigningFee { get; set; }
    [JsonPropertyName("roundsTotal")]
    public int RoundsTotal { get; set; }
}

/// <summary>Host → proposer, immediate acknowledgement that the offer was queued (or
/// rejected outright, e.g. no permission / queue full) — not that a Host operator has
/// approved it yet (see EconomyService.ApproveContractOffer).</summary>
public sealed class TeamContractOfferResultPayload
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}
