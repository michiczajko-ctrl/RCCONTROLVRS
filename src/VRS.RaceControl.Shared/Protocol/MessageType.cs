using System.Text.Json.Serialization;

namespace VRS.RaceControl.Shared.Protocol;

/// <summary>
/// All message types in the VRS Race Control protocol.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessageType
{
    /// <summary>Relay: initial join with sessionCode + role (host/driver).</summary>
    Join,

    /// <summary>Client requests to join a session.</summary>
    JoinRequest,

    /// <summary>Host acknowledges a join request.</summary>
    JoinAck,

    /// <summary>Host sends a flag signal.</summary>
    Flag,

    /// <summary>Host sends a penalty to a driver.</summary>
    Penalty,

    /// <summary>Host sends a text message.</summary>
    TextMessage,

    /// <summary>Host clears the current overlay message and pending queue.</summary>
    ClearOverlay,

    /// <summary>Host AC Bridge broadcasts a live telemetry snapshot.</summary>
    Telemetry,

    /// <summary>Client acknowledges receipt of a message.</summary>
    Ack,

    /// <summary>Heartbeat to keep connection alive.</summary>
    Heartbeat,

    /// <summary>Host broadcasts updated driver list.</summary>
    DriverList,

    /// <summary>Clean disconnect notification.</summary>
    Disconnect,

    /// <summary>Join was rejected (wrong code, etc.)</summary>
    JoinReject,

    /// <summary>Driver submits a race incident report.</summary>
    IncidentReport,

    /// <summary>Host confirms durable receipt of an incident report.</summary>
    IncidentReportAck,

    /// <summary>Host updates the status or steward notes of an incident.</summary>
    IncidentReportUpdate,

    /// <summary>Host sends a configured custom flag.</summary>
    CustomFlag,

    /// <summary>Synchronizes available custom flag definitions.</summary>
    CustomFlagList,

    /// <summary>Host withdraws a previously sent custom flag from the overlay (e.g. after disabling it).</summary>
    CustomFlagWithdraw,

    /// <summary>Synchronizes current driver account/profile data.</summary>
    DriverProfileUpdate,

    /// <summary>Synchronizes organization/league configuration.</summary>
    LeagueProfile,

    /// <summary>Simulator bridge connection status and heartbeat.</summary>
    TelemetryBridgeStatus,

    /// <summary>Session lifecycle state.</summary>
    SessionState,

    /// <summary>Diagnostics snapshot or test result.</summary>
    Diagnostics,

    /// <summary>Client registers a new user account with Host.</summary>
    AccountRegister,

    /// <summary>Host synchronizes user accounts database with clients.</summary>
    AccountSync,

    /// <summary>Race Director engaged Manual Override — force-sync all clients to GREEN, clear queues.</summary>
    ForceSystemReset,

    /// <summary>Race Director deactivated Manual Override — automated systems resume, clients should clear the override indicator.</summary>
    ManualOverrideCleared,

    /// <summary>Driver announces its app version to the HOST right after (re)connecting, so the
    /// HOST can flag a Client/Host version mismatch. Sent as a normal driver-to-host application
    /// message rather than carried in the Join handshake, since the cloud relay does not forward
    /// arbitrary Join payload fields to the HOST.</summary>
    ClientVersionAnnounce,

    /// <summary>Driver requests that another driver (by login) be added to the requester's own
    /// team. The HOST always resolves the team from the requester's own account server-side and
    /// validates the requester's CanAddMembers permission — never trusts a client-asserted team.</summary>
    TeamAddMemberRequest,

    /// <summary>HOST's targeted response to a TeamAddMemberRequest.</summary>
    TeamAddMemberResult,

    /// <summary>Driver (with or without an existing team) applies to join a specific team by
    /// name. Unlike TeamAddMemberRequest this requires no prior team standing or permission —
    /// the HOST always queues it in LeagueProfile.PendingJoinRequests for manual review rather
    /// than applying it immediately.</summary>
    TeamJoinRequest,

    /// <summary>HOST's immediate acknowledgement that a TeamJoinRequest was received and
    /// queued (or rejected outright) — not a notification of the later accept/reject
    /// decision, which is not itself pushed to the driver.</summary>
    TeamJoinResult,

    /// <summary>A team's designated CEO accepts or rejects a pending join application to
    /// their own team. The HOST always resolves and validates CEO status server-side from
    /// its own account/profile data — never trusts a client-asserted CEO claim.</summary>
    TeamJoinDecision,

    /// <summary>HOST's targeted response to a TeamJoinDecision.</summary>
    TeamJoinDecisionResult,

    /// <summary>Driver → HOST, sent immediately after connecting to the Team Hub channel,
    /// before any other message: binds this connection to the account whose TeamHubToken
    /// matches. Every stateful Team Hub action requires this binding first — the channel's
    /// join code is derivable from the public league id, so the self-declared display name
    /// alone must never be trusted as identity (see TeamHubAuthService).</summary>
    TeamHubAuth,

    /// <summary>HOST's response to a TeamHubAuth attempt.</summary>
    TeamHubAuthResult,

    /// <summary>HOST → driver, pushed on Team Hub connect and after any local economy
    /// change while connected — a snapshot of exactly ONE team (the driver's own), never
    /// broadcast. See TeamEconomySnapshotPayload/EconomyService.BuildSnapshot.</summary>
    TeamEconomyUpdate,

    /// <summary>CEO/economy-manager → HOST: propose a contract for an existing driver
    /// (Faza 8). The HOST always queues this for operator approval — never applies it
    /// immediately, same pattern as TeamJoinRequest.</summary>
    TeamContractOfferRequest,

    /// <summary>HOST's immediate response to a TeamContractOfferRequest.</summary>
    TeamContractOfferResult
}

