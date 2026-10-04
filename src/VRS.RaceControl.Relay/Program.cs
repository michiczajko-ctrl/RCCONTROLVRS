using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VRS.RaceControl.Shared.Models;
using VRS.RaceControl.Shared.Protocol;
using VRS.RaceControl.Shared.Services;

var limits = RelayLimits.FromEnvironment();
var builder = WebApplication.CreateSlimBuilder(args);
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxConcurrentConnections = limits.MaxConnections + 16;
    options.Limits.MaxConcurrentUpgradedConnections = limits.MaxConnections;
    options.Limits.MaxRequestBodySize = 64 * 1024;
    options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
});
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
var durableRepository = SupabaseDurableSessionRepository.FromEnvironment();
var incidentRepository = SupabaseDurableIncidentRepository.FromEnvironment();
var authorityStore = SupabaseAuthorityStore.FromEnvironment();
var authorityEnabled = string.Equals(Environment.GetEnvironmentVariable("VRS_RELAY_AUTHORITY_ENABLED"),
    "true", StringComparison.OrdinalIgnoreCase);
var evidenceRetention = authorityStore != null && string.Equals(Environment.GetEnvironmentVariable("VRS_EVIDENCE_RETENTION_ENABLED"),
    "true", StringComparison.OrdinalIgnoreCase)
    ? new EvidenceRetentionWorker(authorityStore.PurgeExportedEvidenceAsync,
        kind => app.Logger.LogWarning("Evidence retention deferred: {FailureType}", kind)) : null;

var sessions = new ConcurrentDictionary<string, RelaySession>(StringComparer.OrdinalIgnoreCase);
var sessionCreationGate = new object();
var connectionGate = new RelayCapacityGate(limits.MaxConnections);
var replayGuard = new SessionTokenReplayGuard();
// A full session may arrive behind one NAT. Leave headroom for operator reconnects;
// token replay, per-account and total-connection limits still apply separately.
var ipJoinLimiter = new RelaySlidingWindowRateLimiter(
    Math.Max(80, limits.MaxClientsPerSession + 16), TimeSpan.FromMinutes(1));
var accountJoinLimiter = new RelaySlidingWindowRateLimiter(10, TimeSpan.FromMinutes(1));
var ingressBudget = new RelayIngressBudget();
var requireTls = string.Equals(
    Environment.GetEnvironmentVariable("VRS_REQUIRE_TLS"), "true", StringComparison.OrdinalIgnoreCase);
var trustProxyHeaders = string.Equals(
    Environment.GetEnvironmentVariable("VRS_TRUST_PROXY_HEADERS"), "true", StringComparison.OrdinalIgnoreCase);
var allowLegacyJoin = string.Equals(
    Environment.GetEnvironmentVariable("VRS_ALLOW_LEGACY_JOIN"), "true", StringComparison.OrdinalIgnoreCase);
SessionJoinTokenValidator? tokenValidator = null;
try
{
    tokenValidator = SessionJoinTokenValidator.FromEnvironment();
}
catch (ArgumentException) when (allowLegacyJoin)
{
    app.Logger.LogWarning("Legacy relay join is enabled. This mode is not suitable for a public deployment.");
}
if (tokenValidator == null && !allowLegacyJoin)
{
    app.Logger.LogCritical(
        "Relay startup blocked: VRS_SESSION_SIGNING_KEY is missing or shorter than 32 bytes. " +
        "Set the same signing key in Render and the Supabase Edge Functions, or explicitly enable " +
        "VRS_ALLOW_LEGACY_JOIN only for a private development relay.");
    throw new InvalidOperationException(
        "VRS_SESSION_SIGNING_KEY (minimum 32 bytes) is required unless VRS_ALLOW_LEGACY_JOIN=true.");
}

app.MapGet("/", async (Microsoft.AspNetCore.Http.HttpContext context) =>
{
    if (authorityEnabled && authorityStore != null) await authorityStore.CheckSchemaAsync(context.RequestAborted);
    return Results.Ok(new
{
    status = "VRS Race Control Relay",
    version = typeof(RelaySession).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
    build = RuntimeBuildIdentity.Current,
    protocolVersion = ProtocolMessage.CurrentProtocolVersion,
    minimumProtocolVersion = ProtocolMessage.CurrentProtocolVersion,
    capabilities = (incidentRepository.IsEnabled
        ? new[] { ProtocolCapabilities.MultiHostOperators, ProtocolCapabilities.LiveIncidents,
            ProtocolCapabilities.SessionStateV2, ProtocolCapabilities.MultiHostV2,
            ProtocolCapabilities.DurableIncidentCases }
        : new[] { ProtocolCapabilities.MultiHostOperators, ProtocolCapabilities.LiveIncidents,
            ProtocolCapabilities.SessionStateV2, ProtocolCapabilities.MultiHostV2 })
        .Concat(new[] { ProtocolCapabilities.FleetTelemetry })
        .Concat(authorityEnabled && authorityStore?.SchemaReady == true && durableRepository.IsEnabled && incidentRepository.IsEnabled
            ? new[] { ProtocolCapabilities.SessionAuthority, ProtocolCapabilities.ScheduledPanelAudio,
                ProtocolCapabilities.IncidentEvidence, ProtocolCapabilities.SharedTrackDefinitions, ProtocolCapabilities.DetailedTelemetry,
                ProtocolCapabilities.AtomicIncidentPenalty, ProtocolCapabilities.AdvancedTelemetryRules }
            : Array.Empty<string>()).ToArray(),
    activeSessions = sessions.Count,
    durableSessionStore = durableRepository.Status,
    durableIncidentStore = incidentRepository.IsEnabled ? "configured" : "disabled",
    authoritySchemaReady = authorityStore?.SchemaReady ?? false,
    evidenceRetention = evidenceRetention?.Status,
    connections = connectionGate.ActiveCount,
    workingSetMb = Environment.WorkingSet / (1024 * 1024),
    limits = new
    {
        limits.MaxSessions,
        limits.MaxClientsPerSession,
        limits.MaxConnections,
        limits.MaxMessageBytes,
        limits.MemoryRejectMb,
        limits.MemoryHealthMb
    },
    timeUtc = DateTime.UtcNow
});
});
app.MapGet("/health", () =>
    RelayMemoryPolicy.IsHealthy(Environment.WorkingSet, limits.MemoryHealthBytes)
        ? Results.Ok("ok")
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.Map("/vrs", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var forwardedProtocol = context.Request.Headers["X-Forwarded-Proto"].ToString();
    if (requireTls && !context.Request.IsHttps
        && !string.Equals(forwardedProtocol, "https", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
        await context.Response.WriteAsync("TLS is required.");
        return;
    }

    var remoteAddress = RelayValidation.GetRateLimitAddress(context, trustProxyHeaders);
    if (!ipJoinLimiter.TryAcquire(remoteAddress))
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        return;
    }

    if (!RelayMemoryPolicy.CanAcceptConnection(Environment.WorkingSet, limits.MemoryRejectBytes))
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsync("Relay memory protection is active.");
        return;
    }

    if (!connectionGate.TryAcquire())
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsync("Relay connection limit reached.");
        return;
    }

    WebSocket? socket = null;
    RelaySession? session = null;
    RelayClient? client = null;

    try
    {
        socket = await context.WebSockets.AcceptWebSocketAsync();
        using var joinTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        joinTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        var joinMessage = await RelayWebSockets.ReceiveAsync(
            socket,
            limits.MaxMessageBytes,
            joinTimeout.Token);
        var joinPayload = joinMessage?.GetPayload<JoinPayload>();

        if (joinMessage?.Type != MessageType.Join || joinPayload == null)
        {
            await RelayWebSockets.RejectAsync(
                socket,
                "Nieprawidłowe żądanie dołączenia.",
                limits.MaxMessageBytes,
                context.RequestAborted);
            return;
        }

        string sessionCode;
        string role;
        string userId;
        string clientName;
        int permissions;
        bool supportsMultiHost;
        bool supportsLiveIncidents;
        bool supportsSessionStateV2;
        bool supportsMultiHostV2;
        bool supportsDurableCases;
        if (tokenValidator != null
            && tokenValidator.TryValidate(joinPayload.SessionJoinToken, out var identity, out _)
            && identity != null)
        {
            if (!replayGuard.TryAccept(identity.TokenId, identity.ExpiresAt)
                || !accountJoinLimiter.TryAcquire(identity.UserId))
            {
                // Every reconnect attempt fetches a fresh token, so a replayed token or a tripped limiter is transient.
                app.Logger.LogWarning("Relay join rejected: session={Session} role={Role} code={Code} retryable={Retryable}",
                    identity.SessionId, identity.Role, "token_replayed_or_rate_limited", true);
                await RelayWebSockets.RejectAsync(
                    socket, "Token połączenia został już użyty albo przekroczono limit prób.",
                    limits.MaxMessageBytes, context.RequestAborted, retryable: true, code: "token_replayed_or_rate_limited");
                return;
            }
            sessionCode = identity.SessionId;
            role = identity.Role;
            userId = identity.UserId;
            clientName = RelayValidation.SanitizeName(identity.DisplayName, role);
            permissions = identity.Permissions;
            supportsMultiHost = joinPayload.Capabilities?.Contains(
                ProtocolCapabilities.MultiHostOperators, StringComparer.Ordinal) == true;
            supportsLiveIncidents = joinPayload.Capabilities?.Contains(
                ProtocolCapabilities.LiveIncidents, StringComparer.Ordinal) == true;
            supportsSessionStateV2 = joinPayload.Capabilities?.Contains(
                ProtocolCapabilities.SessionStateV2, StringComparer.Ordinal) == true
                && durableRepository.IsEnabled;
            supportsMultiHostV2 = joinPayload.Capabilities?.Contains(
                ProtocolCapabilities.MultiHostV2, StringComparer.Ordinal) == true
                && durableRepository.IsEnabled;
            supportsDurableCases = joinPayload.Capabilities?.Contains(
                ProtocolCapabilities.DurableIncidentCases, StringComparer.Ordinal) == true
                && incidentRepository.IsEnabled && supportsLiveIncidents;
        }
        else if (allowLegacyJoin
                 && RelayValidation.TryNormalizeSessionCode(joinPayload.SessionCode, out sessionCode)
                 && RelayValidation.TryNormalizeRole(joinPayload.Role, out role))
        {
            clientName = RelayValidation.SanitizeName(joinPayload.DriverName, role);
            userId = $"legacy:{clientName.ToUpperInvariant()}";
            permissions = role == "host" ? 7 : 0;
            supportsMultiHost = false;
            supportsLiveIncidents = false;
            supportsSessionStateV2 = false;
            supportsMultiHostV2 = false;
            supportsDurableCases = false;
        }
        else
        {
            app.Logger.LogWarning("Rejected invalid session token from {RemoteAddress}", remoteAddress);
            await RelayWebSockets.RejectAsync(
                socket, "Brak prawidłowego tokenu sesji.",
                limits.MaxMessageBytes, context.RequestAborted);
            return;
        }

        if (RelayRoles.IsSecondaryOperator(role) && !supportsMultiHost)
        {
            await RelayWebSockets.RejectAsync(socket, "Operator wymaga obsługi multi-host-v1.",
                limits.MaxMessageBytes, context.RequestAborted);
            return;
        }

        if (!sessions.TryGetValue(sessionCode, out session)
            && Guid.TryParse(sessionCode, out _) && durableRepository.IsEnabled)
        {
            try
            {
                var durableState = await durableRepository.LoadAsync(sessionCode, context.RequestAborted);
                if (durableState == null)
                    throw new DurableSessionStoreException("The durable session record is missing.");
                if (durableState != null)
                {
                    lock (sessionCreationGate)
                    {
                        if (!sessions.TryGetValue(sessionCode, out session)
                            && sessions.Count < limits.MaxSessions)
                        {
                            session = sessions.GetOrAdd(sessionCode,
                                code => new RelaySession(code, limits.MaxClientsPerSession,
                                    durableRepository, durableState, incidentRepository) { Log = app.Logger });
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is DurableSessionStoreException
                                       or HttpRequestException
                                       or TaskCanceledException)
            {
                app.Logger.LogWarning(ex,
                    "Durable session state could not be restored for {SessionCode}", sessionCode);
                await RelayWebSockets.RejectAsync(socket, "Durable session state is temporarily unavailable.",
                    limits.MaxMessageBytes, context.RequestAborted, retryable: true, code: "storage_unavailable");
                return;
            }
        }

        if (session?.RequiresAuthority == true)
        {
            var missingCapabilities = AuthorityAdmissionPolicy.MissingCapabilities(role, joinPayload.Capabilities);
            if (missingCapabilities.Length != 0)
            {
                await RelayWebSockets.RejectAsync(socket, "This session requires updated race-control capabilities: " + string.Join(", ", missingCapabilities),
                    limits.MaxMessageBytes, context.RequestAborted, code: "authority_capability_required");
                return;
            }
            if (!authorityEnabled || authorityStore == null)
            {
                await RelayWebSockets.RejectAsync(socket, "Relay authority is not enabled on this server.",
                    limits.MaxMessageBytes, context.RequestAborted, retryable: true, code: "authority_unavailable");
                return;
            }
            try
            {
                if (!await authorityStore.CheckSchemaAsync(context.RequestAborted))
                    throw new InvalidOperationException("The authority database schema is unavailable or requires migration.");
                await session.BootstrapConfiguredAuthorityAsync(authorityStore, userId,
                    app.Lifetime.ApplicationStopping, context.RequestAborted);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or InvalidOperationException)
            {
                app.Logger.LogWarning(ex, "Authority bootstrap unavailable for {SessionCode}", sessionCode);
                await RelayWebSockets.RejectAsync(socket, "Authority state is temporarily unavailable. Reconnect after resync.",
                    limits.MaxMessageBytes, context.RequestAborted, retryable: true, code: "authority_unavailable");
                return;
            }
        }

        if (role != "host" && session == null)
        {
            await RelayWebSockets.RejectAsync(
                socket,
                "Sesja nie istnieje lub HOST nie jest połączony.",
                limits.MaxMessageBytes,
                context.RequestAborted);
            return;
        }

        if (role == "host")
        {
            lock (sessionCreationGate)
            {
                if (!sessions.TryGetValue(sessionCode, out session)
                    && sessions.Count < limits.MaxSessions)
                {
                    session = sessions.GetOrAdd(
                        sessionCode,
                        code => new RelaySession(code, limits.MaxClientsPerSession,
                            durableRepository, incidentRepository: incidentRepository) { Log = app.Logger });
                }
            }

            if (session == null)
            {
                await RelayWebSockets.RejectAsync(
                    socket,
                    "Serwer osiągnął limit aktywnych sesji. Spróbuj ponownie później.",
                    limits.MaxMessageBytes,
                    context.RequestAborted);
                return;
            }
        }

        if (session == null)
        {
            await RelayWebSockets.RejectAsync(
                socket,
                "Sesja nie jest już dostępna. Połącz się ponownie.",
                limits.MaxMessageBytes,
                context.RequestAborted, retryable: true, code: "session_unavailable");
            return;
        }

        if (supportsDurableCases)
        {
            try { await session.RestoreIncidentStateAsync(context.RequestAborted); }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException
                                       or TaskCanceledException)
            {
                app.Logger.LogWarning(ex, "Durable incidents unavailable for {SessionCode}", sessionCode);
                await RelayWebSockets.RejectAsync(socket,
                    "Authoritative incident state is temporarily unavailable. Try again shortly.",
                    limits.MaxMessageBytes, context.RequestAborted, retryable: true, code: "storage_unavailable");
                return;
            }
        }

        client = new RelayClient(
            socket,
            Guid.NewGuid().ToString("N")[..12],
            userId,
            clientName,
            role,
            limits.MaxMessageBytes,
            permissions,
            supportsMultiHost,
            supportsLiveIncidents,
            supportsSessionStateV2,
            supportsMultiHostV2,
            supportsDurableCases) { SupportsFleetTelemetry = joinPayload.Capabilities?.Contains(
                ProtocolCapabilities.FleetTelemetry, StringComparer.Ordinal) == true,
                SupportsAuthority = session.RequiresAuthority };

        if (!session.TryAddClient(client, out var rejectionReason, out var rejectionRetryable, out var rejectionCode))
        {
            app.Logger.LogWarning("Relay join rejected: session={Session} role={Role} code={Code} retryable={Retryable}",
                sessionCode, client.Role, rejectionCode, rejectionRetryable?.ToString() ?? "unspecified");
            await RelayWebSockets.RejectAsync(
                socket,
                rejectionReason,
                limits.MaxMessageBytes,
                context.RequestAborted, retryable: rejectionRetryable, code: rejectionCode);
            if (session.ClientCount == 0 && !session.CanSurviveHostDisconnect)
            {
                sessions.TryRemove(sessionCode, out _);
            }
            return;
        }

        var acknowledgement = ProtocolMessage.Create(
            MessageType.JoinAck,
            new JoinAckPayload
            {
                RelayVersion = typeof(RelaySession).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
                DriverId = client.Id,
                SessionInfo = new SessionInfo
                {
                    SessionCode = sessionCode,
                    CreatedAt = session.CreatedAtUtc,
                    IsActive = true
                },
                Capabilities = client.AcceptedCapabilities()
            });
        acknowledgement.SessionCode = sessionCode;
        acknowledgement.SessionId = sessionCode;
        acknowledgement.SenderId = "relay";
        acknowledgement.TargetId = client.Id;
        await client.SendAsync(acknowledgement, context.RequestAborted);
        await session.BroadcastDriverListAsync(context.RequestAborted);
        await session.NotifyOperatorConnectionAsync(client, context.RequestAborted);
        await session.BroadcastOperatorSnapshotAsync(context.RequestAborted);
        if (session.RequiresAuthority)
        {
            await session.SendAuthoritySnapshotAsync(client, context.RequestAborted);
            await session.SendAuthorityPenaltyHistoryAsync(client, context.RequestAborted);
        }
        else await session.SendSessionSnapshotAsync(client, context.RequestAborted);
        await session.SendRetainedIncidentStateOnJoinAsync(client, context.RequestAborted);

        while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
        {
            var message = await RelayWebSockets.ReceiveAsync(
                socket,
                limits.MaxMessageBytes,
                context.RequestAborted);
            if (message == null)
            {
                break;
            }

            var admission = ingressBudget.Admit(client.UserId, sessionCode, message.Type, session.HasReservedControlBudget(client));
            if (admission == RelayIngressDecision.DisconnectSender)
            {
                app.Logger.LogWarning(
                    "Relay message rate limit exceeded for session {SessionCode}", sessionCode);
                await socket.CloseOutputAsync(
                    WebSocketCloseStatus.PolicyViolation,
                    "Message rate limit exceeded",
                    context.RequestAborted);
                break;
            }
            if (admission == RelayIngressDecision.DropData)
            {
                session.RecordIngressDataDrop();
                // No ACK or success: report callers retain their operation for retry.
                // Heartbeats and approved control have independent reserved budgets.
                continue;
            }

            client.MarkSeen();
            message.SenderId = client.Id;
            message.SessionCode = sessionCode;
            message.SessionId = sessionCode;

            if (message.Type == MessageType.Heartbeat)
            {
                await session.HandleHeartbeatAsync(client, context.RequestAborted);
                var heartbeat = new ProtocolMessage
                {
                    Type = MessageType.Heartbeat,
                    SessionCode = sessionCode,
                    SessionId = sessionCode,
                    SenderId = "relay",
                    TargetId = client.Id
                };
                await client.SendAsync(heartbeat, context.RequestAborted);
                continue;
            }

            // HOST retries use the same message ID when a CLIENT acknowledgement is
            // lost. Route those copies again: CLIENT deduplication suppresses a second
            // overlay display but deliberately emits the ACK again. Driver-originated
            // application messages remain deduplicated at the relay.
            if (await session.HandleAuthorityMessageAsync(client, message, context.RequestAborted)) continue;
            if (await session.HandleFleetMessageAsync(client, message, context.RequestAborted)) continue;
            if (client.Role != "host"
                && message.Type != MessageType.Ack
                && !session.Deduplicator.TryAccept(message))
            {
                continue;
            }

            if (client.Role == "host")
            {
                if (message.Type == MessageType.IncidentSnapshotRequest)
                {
                    await session.SendRetainedIncidentStateOnJoinAsync(client, context.RequestAborted);
                    continue;
                }
                if (await session.HandleDurableIncidentMessageAsync(client, message, context.RequestAborted))
                    continue;
                if (!await session.HandleHostOperatorMessageAsync(client, message, context.RequestAborted))
                    await session.RouteHostMessageAsync(message, context.RequestAborted);
            }
            else if (client.Role == "driver")
            {
                await session.SendToHostAsync(message, context.RequestAborted);
            }
            else if (RelayRoles.IsSecondaryOperator(client.Role))
            {
                await session.HandleOperatorMessageAsync(client, message, context.RequestAborted);
            }
        }
    }
    catch (OperationCanceledException)
    {
    }
    catch (WebSocketException ex)
    {
        app.Logger.LogWarning("Relay transport ended: session={Session} connection={Connection} websocketError={WebSocketError} closeCode={CloseCode}",
            session?.Code, client?.Id, ex.WebSocketErrorCode, socket?.CloseStatus);
    }
    catch (UnsupportedRelayProtocolException ex)
    {
        if (socket?.State == WebSocketState.Open)
        {
            if (client == null)
                await RelayWebSockets.RejectAsync(socket,
                    $"Unsupported protocol {ex.Version}; relay supports {ProtocolMessage.CurrentProtocolVersion}.",
                    limits.MaxMessageBytes, CancellationToken.None);
            else
                await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation,
                    "Unsupported protocol", CancellationToken.None);
        }
    }
    catch (Exception ex) when (ex is InvalidDataException or JsonException)
    {
        app.Logger.LogDebug("Rejected invalid relay message: {Reason}", ex.Message);
        if (socket?.State == WebSocketState.Open)
        {
            try
            {
                await socket.CloseOutputAsync(
                    WebSocketCloseStatus.PolicyViolation,
                    "Invalid or oversized message",
                    CancellationToken.None);
            }
            catch
            {
            }
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Relay WebSocket connection failed");
    }
    finally
    {
        if (client != null)
            app.Logger.LogWarning("Relay connection closed: session={Session} connection={Connection} role={Role} closeCode={CloseCode} generation={Generation} revision={Revision}",
                session?.Code, client.Id, client.Role, socket?.CloseStatus,
                session?.AuthoritySnapshot?.Generation, session?.AuthoritySnapshot?.Revision);
        if (session != null && client != null)
        {
            session.RemoveClient(client.Id);
            await session.BroadcastDriverListAsync(CancellationToken.None);
            await session.BroadcastOperatorSnapshotAsync(CancellationToken.None);
            if (session.ClientCount == 0 && !session.CanSurviveHostDisconnect)
            {
                sessions.TryRemove(session.Code, out _);
            }
        }
        socket?.Dispose();
        connectionGate.Release();
    }
});

_ = RunSessionWatchdogAsync(app.Lifetime.ApplicationStopping);
_ = RunOperatorWatchdogAsync(app.Lifetime.ApplicationStopping);
if (evidenceRetention != null) _ = evidenceRetention.RunAsync(app.Lifetime.ApplicationStopping);
await app.RunAsync();

async Task RunSessionWatchdogAsync(CancellationToken cancellationToken)
{
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
    try
    {
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(60);
            foreach (var session in sessions.Values)
            {
                await session.RemoveTimedOutClientsAsync(cutoff, cancellationToken);
                if (session.ClientCount == 0)
                {
                    await session.CloseAllAsync("Session expired", cancellationToken);
                    sessions.TryRemove(session.Code, out _);
                }
            }
        }
    }
    catch (OperationCanceledException)
    {
    }
}

async Task RunOperatorWatchdogAsync(CancellationToken cancellationToken)
{
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
    try
    {
        while (await timer.WaitForNextTickAsync(cancellationToken))
            foreach (var session in sessions.Values)
                await session.ExpireOperatorPriorityAsync(DateTime.UtcNow, cancellationToken);
    }
    catch (OperationCanceledException)
    {
    }
}

public static class RelayValidation
{
    private static readonly Regex SessionCodePattern =
        new("^[A-Z0-9][A-Z0-9-]{3,31}$", RegexOptions.Compiled);

    public static bool TryNormalizeSessionCode(string? value, out string sessionCode)
    {
        sessionCode = value?.Trim().ToUpperInvariant() ?? string.Empty;
        return SessionCodePattern.IsMatch(sessionCode);
    }

    public static bool TryNormalizeRole(string? value, out string role)
    {
        role = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return role is "host" or "driver";
    }

    public static string SanitizeName(string? value, string role)
    {
        var fallback = role == "host" ? "HOST" : "Unknown driver";
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }
        var sanitized = new string(value.Trim().Where(c => !char.IsControl(c)).Take(80).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }

    public static string GetRateLimitAddress(HttpContext context, bool trustProxyHeaders)
    {
        if (trustProxyHeaders)
        {
            var firstForwarded = context.Request.Headers["X-Forwarded-For"].ToString()
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (System.Net.IPAddress.TryParse(firstForwarded, out var forwardedAddress))
                return forwardedAddress.ToString();
        }
        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}

public static class RelayRoles
{
    public static bool IsSecondaryOperator(string role)
        => role is "operator" or "observer" or "viewer" or "steward";

    public static OperatorRole ToOperatorRole(string role) => role switch
    {
        "operator" => OperatorRole.Operator,
        "steward" => OperatorRole.Steward,
        _ => OperatorRole.Observer
    };

    public static OperatorPermissions ToPermissions(int value)
        => (OperatorPermissions)(value & (int)(OperatorPermissions.Observe
            | OperatorPermissions.Incidents | OperatorPermissions.Control));

}

public static class RelayWebSockets
{
    public static async Task SendAsync(
        WebSocket socket,
        ProtocolMessage message,
        int maxMessageBytes,
        CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(message.ToJson());
        if (bytes.Length > maxMessageBytes)
        {
            throw new InvalidDataException(
                $"Message exceeds the {maxMessageBytes}-byte relay limit.");
        }
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    public static async Task<ProtocolMessage?> ReceiveAsync(
        WebSocket socket,
        int maxMessageBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var message = new RelayMessageAccumulator(maxMessageBytes);
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new InvalidDataException("Only text messages are supported.");
            }

            message.Append(buffer.AsSpan(0, result.Count));
            if (result.EndOfMessage)
            {
                var json = message.ToUtf8String();
                var parsed = ProtocolMessage.FromJson(json);
                if (parsed != null) return parsed;
                try
                {
                    using var document = JsonDocument.Parse(json);
                    if (document.RootElement.ValueKind == JsonValueKind.Object
                        && document.RootElement.TryGetProperty("protocolVersion", out var version)
                        && version.ValueKind == JsonValueKind.String
                        && version.GetString() is { Length: > 0 } value
                        && value != ProtocolMessage.CurrentProtocolVersion)
                        throw new UnsupportedRelayProtocolException(value[..Math.Min(value.Length, 32)]);
                }
                catch (JsonException) { }
                return null;
            }
        }
    }

    public static async Task RejectAsync(
        WebSocket socket,
        string reason,
        int maxMessageBytes,
        CancellationToken cancellationToken, bool? retryable = false, string? code = null)
    {
        var rejection = ProtocolMessage.Create(
            MessageType.JoinReject,
            new JoinRejectPayload { Reason = reason, Retryable = retryable, Code = code ?? "join_rejected" });
        rejection.SenderId = "relay";
        await SendAsync(socket, rejection, maxMessageBytes, cancellationToken);

        if (socket.State == WebSocketState.Open)
        {
            await socket.CloseOutputAsync(
                WebSocketCloseStatus.PolicyViolation,
                reason,
                cancellationToken);
        }
    }
}

public sealed class UnsupportedRelayProtocolException(string version)
    : Exception("Unsupported relay protocol")
{
    public string Version { get; } = version;
}

public sealed class RelayClient : IDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly int _maxMessageBytes;
    private int _disposeState;

    public RelayClient(
        WebSocket socket,
        string id,
        string name,
        string role,
        int maxMessageBytes,
        int permissions = 0,
        bool supportsMultiHost = false,
        bool supportsLiveIncidents = false,
        bool supportsSessionStateV2 = false,
        bool supportsMultiHostV2 = false,
        bool supportsDurableIncidents = false)
        : this(socket, id, $"legacy:{id}", name, role, maxMessageBytes, permissions,
            supportsMultiHost, supportsLiveIncidents, supportsSessionStateV2,
            supportsMultiHostV2, supportsDurableIncidents)
    {
    }

    public RelayClient(
        WebSocket socket,
        string id,
        string userId,
        string name,
        string role,
        int maxMessageBytes,
        int permissions = 0,
        bool supportsMultiHost = false,
        bool supportsLiveIncidents = false,
        bool supportsSessionStateV2 = false,
        bool supportsMultiHostV2 = false,
        bool supportsDurableIncidents = false)
    {
        Socket = socket;
        Id = id;
        UserId = userId;
        Name = name;
        Role = role;
        Permissions = permissions;
        SupportsMultiHost = supportsMultiHost;
        SupportsLiveIncidents = supportsLiveIncidents;
        SupportsSessionStateV2 = supportsSessionStateV2;
        SupportsMultiHostV2 = supportsMultiHostV2;
        SupportsDurableIncidents = supportsDurableIncidents;
        _maxMessageBytes = maxMessageBytes;
    }

    public WebSocket Socket { get; }
    public string Id { get; }
    public string UserId { get; }
    public string Name { get; }
    public string Role { get; }
    public int Permissions { get; }
    public bool SupportsMultiHost { get; }
    public bool SupportsLiveIncidents { get; }
    public bool SupportsSessionStateV2 { get; }
    public bool SupportsMultiHostV2 { get; }
    public bool SupportsDurableIncidents { get; }
    public bool SupportsFleetTelemetry { get; init; }
    public bool SupportsAuthority { get; init; }
    public DateTime LastSeenUtc { get; private set; } = DateTime.UtcNow;

    public void MarkSeen() => LastSeenUtc = DateTime.UtcNow;

    public string[] AcceptedCapabilities()
    {
        var capabilities = new List<string>(4);
        if (SupportsMultiHost) capabilities.Add(ProtocolCapabilities.MultiHostOperators);
        if (SupportsLiveIncidents) capabilities.Add(ProtocolCapabilities.LiveIncidents);
        if (SupportsSessionStateV2) capabilities.Add(ProtocolCapabilities.SessionStateV2);
        if (SupportsMultiHostV2) capabilities.Add(ProtocolCapabilities.MultiHostV2);
        if (SupportsDurableIncidents) capabilities.Add(ProtocolCapabilities.DurableIncidentCases);
        if (SupportsFleetTelemetry) capabilities.Add(ProtocolCapabilities.FleetTelemetry);
        if (SupportsAuthority) capabilities.AddRange([ProtocolCapabilities.SessionAuthority, ProtocolCapabilities.ScheduledPanelAudio,
            ProtocolCapabilities.IncidentEvidence, ProtocolCapabilities.SharedTrackDefinitions, ProtocolCapabilities.DetailedTelemetry,
            ProtocolCapabilities.AtomicIncidentPenalty, ProtocolCapabilities.AdvancedTelemetryRules]);
        return capabilities.ToArray();
    }

    public async Task SendAsync(ProtocolMessage message, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (Socket.State != WebSocketState.Open)
            {
                throw new WebSocketException(
                    WebSocketError.InvalidState,
                    $"Relay recipient {Id} is no longer open.");
            }
            await RelayWebSockets.SendAsync(
                Socket,
                message,
                _maxMessageBytes,
                cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;
        Socket.Dispose();
        _sendLock.Dispose();
    }
}

public sealed partial class RelaySession
{
    private readonly ConcurrentDictionary<string, RelayClient> _clients = new();
    private readonly object _membershipGate = new();
    private readonly int _maxClients;
    private string? _mainUserId;
    private OperatorPriority? _operatorPriority;
    private ProtocolMessage? _latestPanelState;
    private readonly Dictionary<string, IncidentReport> _incidents = new(StringComparer.Ordinal);
    private long _incidentRevision;
    private bool _durableV2Session;
    private readonly IDurableSessionRepository _durableRepository;
    private readonly IDurableIncidentRepository _durableIncidents;
    private readonly SemaphoreSlim _incidentLoadGate = new(1, 1);
    private bool _incidentStateLoaded;
    private readonly HashSet<Guid> _sessionOperationIds = [];
    private JsonElement _sessionSnapshot;
    private long _sessionGeneration = 1;
    private long _sessionRevision;
    private string _sessionOwnerUserId = string.Empty;

    public RelaySession(
        string code,
        int maxClients,
        IDurableSessionRepository? durableRepository = null,
        DurableRelaySession? durableState = null,
        IDurableIncidentRepository? incidentRepository = null)
    {
        Code = code;
        _maxClients = maxClients;
        _durableRepository = durableRepository ?? DisabledDurableSessionRepository.Instance;
        _durableIncidents = incidentRepository ?? DisabledDurableIncidentRepository.Instance;
        _sessionSnapshot = JsonDocument.Parse("{}").RootElement.Clone();
        if (durableState != null)
        {
            if (durableState.AuthorityMode is not ("legacy" or "relay-v3"))
                throw new InvalidDataException("Unsupported durable authority mode.");
            RequiresAuthority = durableState.AuthorityMode == "relay-v3";
            if (!string.Equals(durableState.SessionId, code, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Durable state belongs to another session.", nameof(durableState));
            _mainUserId = durableState.MainUserId ?? durableState.OwnerUserId;
            _sessionOwnerUserId = durableState.OwnerUserId;
            _sessionGeneration = durableState.Generation;
            _sessionRevision = durableState.Revision;
            _sessionSnapshot = durableState.Snapshot.Clone();
            _operatorPriority = new OperatorPriority(
                _mainUserId, "Main HOST", DateTime.UtcNow);
            _operatorPriority.RestoreDurableAuthority(durableState.Generation, durableState.Revision);
            _durableV2Session = true;
        }
    }

    public string Code { get; }
    public DateTime CreatedAtUtc { get; } = DateTime.UtcNow;
    public int ClientCount => _clients.Count;
    public bool CanSurviveHostDisconnect => _durableV2Session || _authority != null;

    public async Task RestoreIncidentStateAsync(CancellationToken cancellationToken)
    {
        if (!_durableIncidents.IsEnabled || _incidentStateLoaded) return;
        await _incidentLoadGate.WaitAsync(cancellationToken);
        try
        {
            if (_incidentStateLoaded) return;
            var state = await _durableIncidents.LoadAsync(Code, cancellationToken)
                ?? throw new InvalidDataException("No durable incident session exists.");
            lock (_membershipGate)
            {
                _incidents.Clear();
                foreach (var report in state.Reports.Where(item => !string.IsNullOrWhiteSpace(item.Id)))
                    _incidents[report.Id] = report;
                _incidentRevision = state.Revision;
                _sessionGeneration = _authority?.Snapshot.Generation ?? state.Generation;
                _incidentStateLoaded = true;
            }
        }
        finally { _incidentLoadGate.Release(); }
    }

    public Task SendRetainedIncidentStateOnJoinAsync(RelayClient client,
        CancellationToken cancellationToken) => SendRetainedIncidentStateAsync(client, cancellationToken);
    public RelayClient? Host => _clients.Values.FirstOrDefault(c => c.Role == "host");
    public OperatorPriorityState? OperatorState => _operatorPriority?.Snapshot();
    public MessageDeduplicator Deduplicator { get; } = new(
        TimeSpan.FromMinutes(2),
        RelayLimits.DeduplicationCapacityPerSession);

    public bool TryAddClient(RelayClient client, out string rejectionReason)
        => TryAddClient(client, out rejectionReason, out _, out _);

    /// <summary>
    /// <paramref name="retryable"/>: true = the client should keep reconnecting with a fresh token, false = permanent,
    /// null = unspecified (a driver that never joined this session stops, one that was connected before keeps retrying).
    /// </summary>
    public bool TryAddClient(RelayClient client, out string rejectionReason, out bool? retryable, out string rejectionCode)
    {
        lock (_membershipGate)
        {
            retryable = false; rejectionCode = "join_rejected";
            if (RequiresAuthority && (_authority == null || !client.SupportsAuthority))
            {
                rejectionReason = "This session requires an initialized v3 authority and compatible client.";
                rejectionCode = "authority_client_required";
                return false;
            }
            if (_clients.Count >= _maxClients)
            {
                rejectionReason = "Ta sesja osiągnęła limit połączonych kierowców.";
                rejectionCode = "session_full";
                retryable = client.Role == "driver" && Host == null;
                return false;
            }
            if (_clients.Values.Any(existing =>
                string.Equals(existing.UserId, client.UserId, StringComparison.Ordinal)))
            {
                // After a network drop the previous socket stays registered until the Relay heartbeat cutoff
                // (about 75 s), so the same account reconnecting must be retried, not abandoned.
                // A driver client decides itself (stops on its first-ever join, so a second PC gets a clear error).
                rejectionReason = "To konto jest już połączone z sesją.";
                rejectionCode = "account_already_connected";
                if (client.Role != "driver" || Host == null) retryable = true;
                else retryable = null;
                return false;
            }
            if (client.Role == "host" && Host != null)
            {
                // A different account (the same account is rejected above), so waiting does not help.
                rejectionReason = "HOST dla tej sesji jest już połączony.";
                rejectionCode = "host_already_connected";
                return false;
            }
            if (client.Role == "host" && _mainUserId != null
                && !string.Equals(_mainUserId, client.UserId, StringComparison.Ordinal))
            {
                rejectionReason = "Tylko właściciel sesji może ponownie połączyć główny HOST.";
                rejectionCode = "host_owner_required";
                return false;
            }
            if (RequiresAuthority && RelayRoles.IsSecondaryOperator(client.Role)) RestoreAuthorityApproval(client);
            if (RelayRoles.IsSecondaryOperator(client.Role) && Host == null
                && !RequiresAuthority && _durableV2Session && client.SupportsMultiHostV2 && _operatorPriority != null
                && _operatorPriority.Snapshot().Operators.All(seat => seat.Id != client.UserId))
            {
                _operatorPriority.RestoreTrustedSeat(client.UserId, client.Name,
                    RelayRoles.ToOperatorRole(client.Role), RelayRoles.ToPermissions(client.Permissions),
                    DateTime.UtcNow);
            }
            if (client.Role == "driver" && Host == null && _authority == null)
            {
                rejectionReason = "Główny HOST jest niedostępny; zdalni kierowcy nie mogą teraz dołączyć.";
                rejectionCode = "host_unavailable";
                retryable = true;
                return false;
            }
            if (RelayRoles.IsSecondaryOperator(client.Role) && Host == null
                && (!_durableV2Session || !client.SupportsMultiHostV2
                    || _operatorPriority?.Snapshot().Operators.All(seat => seat.Id != client.UserId) != false))
            {
                rejectionReason = "Główny HOST jest niedostępny; tylko wcześniej zatwierdzony operator może wrócić do sesji.";
                rejectionCode = "operator_not_approved";
                return false;
            }
            if (RelayRoles.IsSecondaryOperator(client.Role)
                && (_operatorPriority == null || !client.SupportsMultiHost
                    || (Host != null && !Host.SupportsMultiHost)))
            {
                rejectionReason = "Ta sesja nie obsługuje operatorów multi-HOST.";
                rejectionCode = "multi_host_unsupported";
                return false;
            }

            rejectionReason = string.Empty;
            if (!_clients.TryAdd(client.Id, client)) return false;
            if (client.Role == "host")
            {
                _mainUserId ??= client.UserId;
                if (_authority == null && string.IsNullOrWhiteSpace(_sessionOwnerUserId))
                    _sessionOwnerUserId = client.UserId;
                _operatorPriority ??= new OperatorPriority(client.UserId, client.Name, DateTime.UtcNow);
                _operatorPriority.Heartbeat(client.UserId, DateTime.UtcNow);
                _durableV2Session |= client.SupportsSessionStateV2 && client.SupportsMultiHostV2;
                _sessionGeneration = _authority?.Snapshot.Generation ?? _operatorPriority.Generation;
            }
            else if (RelayRoles.IsSecondaryOperator(client.Role))
                _operatorPriority?.Heartbeat(client.UserId, DateTime.UtcNow);
            return true;
        }
    }

    public void RemoveClient(string id)
    {
        RelayClient? client;
        lock (_membershipGate)
        {
            _clients.TryRemove(id, out client);
            if (client != null && (client.Role == "host" || RelayRoles.IsSecondaryOperator(client.Role)))
                _operatorPriority?.Disconnect(client.UserId);
        }
        if (client != null)
        {
            client.Dispose();
        }
    }

    /// <summary>Durable case writes are committed before any live update is emitted.</summary>
    public async Task<bool> HandleDurableIncidentMessageAsync(RelayClient client,
        ProtocolMessage message, CancellationToken cancellationToken)
    {
        if (!client.SupportsDurableIncidents || !_durableIncidents.IsEnabled
            || message.Type is not (MessageType.IncidentStatusCommand or
                MessageType.IncidentStateUpdate or MessageType.IncidentSnapshot)) return false;
        if (message.Type == MessageType.IncidentSnapshot) return true;

        Guid operationId;
        long knownRevision;
        long knownGeneration;
        string caseId;
        string kind;
        JsonElement payload;
        if (message.Type == MessageType.IncidentStatusCommand)
        {
            var command = message.GetPayload<IncidentStatusCommandPayload>();
            if (command == null || command.CommandId == Guid.Empty
                || string.IsNullOrWhiteSpace(command.IncidentId) || command.IncidentId.Length > 80
                || command.CaseStatus == null || command.Decision == null
                || !Enum.IsDefined(command.CaseStatus.Value)
                || !Enum.IsDefined(command.Decision.Value)
                || command.Note == null || command.Note.Length > 1000
                || (command.ResponseToDriver?.Length ?? 0) > 600
                || (command.AssignedSteward?.Length ?? 0) > 120
                || (client.Role != "host" && _operatorPriority?.HasPermission(
                    client.UserId, OperatorPermissions.Incidents, DateTime.UtcNow) != true))
                return true;
            operationId = command.CommandId;
            knownRevision = command.KnownRevision;
            knownGeneration = command.KnownGeneration;
            caseId = command.IncidentId;
            kind = "decision";
            payload = JsonSerializer.SerializeToElement(new
            {
                caseStatus = command.CaseStatus.Value.ToString(),
                decision = command.Decision.Value.ToString(),
                stewardNote = command.Note,
                assignedSteward = command.AssignedSteward ?? "",
                responseToDriver = command.ResponseToDriver ?? ""
            });
        }
        else
        {
            if (!string.Equals(client.UserId, _sessionOwnerUserId, StringComparison.Ordinal))
                return true;
            var update = message.GetPayload<IncidentStateUpdatePayload>();
            if (update?.Incident == null || IncidentReport.Validate(update.Incident) != null
                || !Guid.TryParse(message.Id, out operationId)) return true;
            knownRevision = update.Revision - 1;
            knownGeneration = update.Generation;
            caseId = update.Incident.Id;
            kind = "replace";
            payload = JsonSerializer.SerializeToElement(update.Incident,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }

        DurableIncidentResult? result = null;
        try
        {
            if (knownGeneration != _sessionGeneration)
                result = new DurableIncidentResult("conflict", _sessionGeneration,
                    _incidentRevision, null);
            else
                result = await _durableIncidents.ApplyAsync(Code, client.UserId, operationId,
                    knownGeneration, knownRevision, caseId, kind, payload, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException
                                   or TaskCanceledException)
        {
            result = new DurableIncidentResult("storage_unavailable", _sessionGeneration,
                _incidentRevision, null);
        }

        if (result.Applied && result.Report != null)
        {
            var update = ProtocolMessage.Create(MessageType.IncidentStateUpdate,
                new IncidentStateUpdatePayload(result.Revision, result.Report, result.Generation));
            StampRelay(update, "all");
            await RouteHostMessageAsync(update, cancellationToken);
        }
        else if (result.Conflict || result.Duplicate)
        {
            try
            {
                var fresh = await _durableIncidents.LoadAsync(Code, cancellationToken);
                if (fresh != null)
                {
                    var snapshot = ProtocolMessage.Create(MessageType.IncidentSnapshot,
                        new IncidentSnapshotPayload(fresh.Revision, fresh.Reports, fresh.Generation));
                    StampRelay(snapshot, "all");
                    await RouteHostMessageAsync(snapshot, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException
                                       or TaskCanceledException) { }
        }

        var response = ProtocolMessage.Create(MessageType.IncidentStatusResult,
            new IncidentStatusResultPayload(operationId,
                result.Applied || result.Duplicate, result.Revision, result.Result));
        StampRelay(response, client.Id);
        await TrySendAsync(client, response, cancellationToken);
        return true;
    }

    public async Task RouteHostMessageAsync(
        ProtocolMessage message,
        CancellationToken cancellationToken)
    {
        if (message.Type is MessageType.IncidentSnapshot or MessageType.IncidentStateUpdate
            or MessageType.IncidentStatusResult)
        {
            if (message.Type != MessageType.IncidentStatusResult)
                lock (_membershipGate)
                    if (!RetainIncidentState(message)) return;
            if (message.Type == MessageType.IncidentSnapshot && _durableIncidents.IsEnabled)
            {
                await Task.WhenAll(_clients.Values.Where(CanReceiveIncidentState)
                    .Where(client => string.IsNullOrWhiteSpace(message.TargetId)
                        || message.TargetId == "all" || message.TargetId == client.Id)
                    .Select(client => SendRetainedIncidentStateAsync(client, cancellationToken)));
                return;
            }
            await Task.WhenAll(_clients.Values
                .Where(CanReceiveIncidentState)
                .Where(client => string.IsNullOrWhiteSpace(message.TargetId)
                    || message.TargetId == "all" || message.TargetId == client.Id)
                .Select(client => TrySendAsync(client, message, cancellationToken)));
            return;
        }
        if (message.Type == MessageType.RaceControlPanelState
            && (string.IsNullOrWhiteSpace(message.TargetId) || message.TargetId == "all"))
        {
            // Keep the latest authoritative panel epoch/revision so an operator approved
            // after FCY/overlay activation receives the live state instead of an empty UI.
            lock (_membershipGate)
                _latestPanelState = ProtocolMessage.FromLegacyCompatibleJson(message.ToJson());
        }
        if (!string.IsNullOrWhiteSpace(message.TargetId) && message.TargetId != "all")
        {
            if (_clients.TryGetValue(message.TargetId, out var target) && CanReceiveHostState(target))
            {
                await TrySendAsync(target, message, cancellationToken);
            }
            return;
        }

        await Task.WhenAll(_clients.Values
            .Where(CanReceiveHostState)
            .Select(c => TrySendAsync(c, message, cancellationToken)));
    }

    public async Task SendToHostAsync(
        ProtocolMessage message,
        CancellationToken cancellationToken)
    {
        var host = Host ?? _clients.Values.FirstOrDefault(client =>
            RelayRoles.IsSecondaryOperator(client.Role)
            && string.Equals(client.UserId, _sessionOwnerUserId, StringComparison.Ordinal));
        if (host != null)
        {
            await TrySendAsync(host, message, cancellationToken);
        }
    }

    public async Task NotifyOperatorConnectionAsync(RelayClient client, CancellationToken cancellationToken)
    {
        if (!RelayRoles.IsSecondaryOperator(client.Role)) return;
        var host = Host;
        if (host == null) return;
        var request = ProtocolMessage.Create(MessageType.OperatorConnectionRequest,
            new OperatorConnectionRequestPayload(client.UserId, client.Id, client.Name,
                RelayRoles.ToOperatorRole(client.Role), RelayRoles.ToPermissions(client.Permissions)));
        StampRelay(request, host.Id);
        await TrySendAsync(host, request, cancellationToken);
    }

    public async Task HandleHeartbeatAsync(RelayClient client, CancellationToken cancellationToken)
    {
        if ((client.Role == "host" || RelayRoles.IsSecondaryOperator(client.Role))
            && _operatorPriority != null)
            _operatorPriority.Heartbeat(client.UserId, DateTime.UtcNow);
        await RenewAuthorityFromHeartbeatAsync(client, cancellationToken);
    }

    public async Task<bool> HandleHostOperatorMessageAsync(RelayClient client, ProtocolMessage message,
        CancellationToken cancellationToken)
    {
        if (await HandleConfiguredAuthorityApprovalAsync(client, message, cancellationToken)) return true;
        if (message.Type == MessageType.SessionOwnershipV2 && client.SupportsMultiHostV2)
        {
            var request = message.GetPayload<SessionOwnershipRequestPayload>();
            var accepted = false;
            var reason = "Main HOST return is unavailable or the generation changed.";
            var returnPriority = _operatorPriority;
            if (request != null && request.RequestId != Guid.Empty
                && client.UserId == _mainUserId
                && returnPriority?.CanReturnToMain(client.UserId, request.ExpectedGeneration) == true)
            {
                try
                {
                    if (_durableRepository.IsEnabled)
                    {
                        var durable = await _durableRepository.TakeOwnershipAsync(Code,
                            client.UserId, request.ExpectedGeneration, "Main HOST return",
                            cancellationToken);
                        returnPriority.ApplyDurableOwnership(client.UserId, durable.Generation,
                            durable.Revision);
                        _sessionGeneration = durable.Generation;
                        _sessionRevision = durable.Revision;
                        accepted = true;
                    }
                    else
                    {
                        accepted = returnPriority.ReturnToMain(client.UserId, request.ExpectedGeneration);
                        _sessionGeneration = returnPriority.Generation;
                    }
                    if (accepted) _sessionOwnerUserId = client.UserId;
                }
                catch (Exception ex) when (ex is DurableSessionStoreException
                                           or HttpRequestException or TaskCanceledException)
                { reason = "Durable ownership service is unavailable."; }
            }
            var result = ProtocolMessage.Create(MessageType.SessionOwnershipV2,
                new SessionOwnershipResultPayload(request?.RequestId ?? Guid.Empty,
                    accepted, _sessionGeneration, accepted ? null : reason));
            StampRelay(result, client.Id);
            await TrySendAsync(client, result, cancellationToken);
            if (accepted)
            {
                await BroadcastOperatorSnapshotAsync(cancellationToken);
                await BroadcastSessionSnapshotAsync(cancellationToken);
                var fresh = await _durableIncidents.LoadAsync(Code, cancellationToken);
                if (fresh != null)
                {
                    var snapshot = ProtocolMessage.Create(MessageType.IncidentSnapshot,
                        new IncidentSnapshotPayload(fresh.Revision, fresh.Reports, fresh.Generation));
                    StampRelay(snapshot, "all");
                    await RouteHostMessageAsync(snapshot, cancellationToken);
                }
            }
            return true;
        }
        if (message.Type == MessageType.SessionOperationV2 && client.SupportsSessionStateV2)
        {
            await HandleSessionOperationAsync(client, message, cancellationToken);
            return true;
        }
        if (message.Type is not (MessageType.OperatorApproval
            or MessageType.OperatorPriorityDecision
            or MessageType.OperatorPriorityTransfer))
            return false;
        var host = Host;
        var priority = _operatorPriority;
        if (host == null || priority == null || host.Id != client.Id) return true;
        RelayClient? approvedOperator = null;
        if (message.Type == MessageType.OperatorApproval)
        {
            var approval = message.GetPayload<OperatorApprovalPayload>();
            if (approval == null) return true;
            var target = _clients.Values.FirstOrDefault(candidate => RelayRoles.IsSecondaryOperator(candidate.Role)
                && candidate.UserId == approval.OperatorId);
            if (target == null) return true;
            if (approval.Approved)
            {
                var maximum = RelayRoles.ToPermissions(target.Permissions);
                var granted = approval.Permissions & maximum;
                priority.Approve(client.UserId, target.UserId, target.Name,
                    RelayRoles.ToOperatorRole(target.Role), granted, DateTime.UtcNow);
                if (granted.HasFlag(OperatorPermissions.Observe)) approvedOperator = target;
            }
            else
            {
                priority.Revoke(client.UserId, target.UserId);
            }
        }
        else if (message.Type == MessageType.OperatorPriorityDecision)
        {
            var decision = message.GetPayload<OperatorPriorityDecisionPayload>();
            if (decision != null)
                priority.DecideControlRequest(client.UserId, decision.OperatorId,
                    decision.Approved, DateTime.UtcNow);
        }
        else
        {
            var transfer = message.GetPayload<OperatorPriorityTransferPayload>();
            if (transfer != null)
                priority.Transfer(client.UserId, transfer.OperatorId, DateTime.UtcNow);
        }
        await BroadcastOperatorSnapshotAsync(cancellationToken);
        await BroadcastDriverListAsync(cancellationToken);
        if (approvedOperator != null)
        {
            await SendAuthoritySnapshotAsync(approvedOperator, cancellationToken);
            await SendRetainedPanelStateAsync(approvedOperator, cancellationToken);
            await SendRetainedIncidentStateAsync(approvedOperator, cancellationToken);
        }
        return true;
    }

    public async Task<bool> HandleOperatorMessageAsync(RelayClient client, ProtocolMessage message,
        CancellationToken cancellationToken)
    {
        var priority = _operatorPriority;
        if (priority == null || !RelayRoles.IsSecondaryOperator(client.Role)) return false;
        if (message.Type == MessageType.IncidentSnapshotRequest)
        {
            await SendRetainedIncidentStateAsync(client, cancellationToken);
            return true;
        }
        if (await HandleDurableIncidentMessageAsync(client, message, cancellationToken)) return true;
        if (message.Type == MessageType.SessionOperationV2 && client.SupportsSessionStateV2)
        {
            await HandleSessionOperationAsync(client, message, cancellationToken);
            return true;
        }
        if (string.Equals(client.UserId, _sessionOwnerUserId, StringComparison.Ordinal)
            && priority.ControllerId == client.UserId && IsOwnerRoutable(message.Type))
        {
            await RouteHostMessageAsync(message, cancellationToken);
            return true;
        }
        if (message.Type == MessageType.OperatorHeartbeat)
        {
            priority.Heartbeat(client.UserId, DateTime.UtcNow);
            return true;
        }
        if (message.Type == MessageType.OperatorPriorityRequest)
        {
            if (priority.RequestControl(client.UserId, DateTime.UtcNow))
            {
                message.SenderId = client.UserId;
                await SendToHostAsync(message, cancellationToken);
                await BroadcastOperatorSnapshotAsync(cancellationToken);
            }
            return true;
        }
        if (message.Type == MessageType.SessionOwnershipV2 && client.SupportsMultiHostV2)
        {
            var request = message.GetPayload<SessionOwnershipRequestPayload>();
            var accepted = false;
            string? ownershipError = null;
            if (request != null && request.RequestId != Guid.Empty
                && priority.CanTakeOwnership(client.UserId, request.ExpectedGeneration, DateTime.UtcNow))
            {
                try
                {
                    DurableOwnershipResult? durable = null;
                    if (_durableRepository.IsEnabled)
                    {
                        durable = await _durableRepository.TakeOwnershipAsync(
                            Code, client.UserId, request.ExpectedGeneration, request.Reason,
                            cancellationToken);
                    }
                    if (durable != null)
                    {
                        priority.ApplyDurableOwnership(client.UserId, durable.Generation,
                            durable.Revision);
                        accepted = true;
                    }
                    else
                        accepted = priority.TakeOwnership(
                            client.UserId, request.ExpectedGeneration, DateTime.UtcNow);
                    if (accepted)
                    {
                        _sessionOwnerUserId = client.UserId;
                        _sessionGeneration = durable?.Generation ?? priority.Generation;
                        _sessionRevision = durable?.Revision ?? _sessionRevision + 1;
                    }
                }
                catch (Exception ex) when (ex is DurableSessionStoreException
                                           or HttpRequestException
                                           or TaskCanceledException)
                {
                    ownershipError = "Durable ownership service is unavailable or the generation changed.";
                }
            }
            var result = ProtocolMessage.Create(MessageType.SessionOwnershipV2,
                new SessionOwnershipResultPayload(request?.RequestId ?? Guid.Empty, accepted,
                    accepted ? _sessionGeneration : priority.Generation,
                    accepted ? null : ownershipError ?? "Ownership is unavailable or the generation changed."));
            StampRelay(result, client.Id);
            await TrySendAsync(client, result, cancellationToken);
            if (accepted)
            {
                await BroadcastOperatorSnapshotAsync(cancellationToken);
                await BroadcastSessionSnapshotAsync(cancellationToken);
                if (_durableIncidents.IsEnabled)
                {
                    var fresh = await _durableIncidents.LoadAsync(Code, cancellationToken);
                    if (fresh != null)
                    {
                        var snapshot = ProtocolMessage.Create(MessageType.IncidentSnapshot,
                            new IncidentSnapshotPayload(fresh.Revision, fresh.Reports,
                                fresh.Generation));
                        StampRelay(snapshot, "all");
                        await RouteHostMessageAsync(snapshot, cancellationToken);
                    }
                }
            }
            return true;
        }
        if (message.Type == MessageType.OperatorCommand)
        {
            var command = message.GetPayload<OperatorCommandPayload>();
            if (command != null && OperatorCommandRules.IsAllowed(command.CommandType)
                && priority.AcceptControl(client.UserId, command.Generation,
                command.CommandId, DateTime.UtcNow))
            {
                message.SenderId = client.UserId;
                await SendToHostAsync(message, cancellationToken);
            }
            return true;
        }
        if (message.Type == MessageType.IncidentReport
            && priority.HasPermission(client.UserId, OperatorPermissions.Incidents, DateTime.UtcNow))
        {
            message.SenderId = client.UserId;
            await SendToHostAsync(message, cancellationToken);
            return true;
        }
        if (message.Type == MessageType.IncidentStatusCommand
            && client.SupportsLiveIncidents
            && priority.HasPermission(client.UserId, OperatorPermissions.Incidents, DateTime.UtcNow))
        {
            var command = message.GetPayload<IncidentStatusCommandPayload>();
            if (command != null && command.CommandId != Guid.Empty
                && !string.IsNullOrWhiteSpace(command.IncidentId)
                && command.IncidentId.Length <= 80
                && Enum.IsDefined(command.Status)
                && command.Note != null && command.Note.Length <= 1000
                && (command.AssignedSteward?.Length ?? 0) <= 120
                && (command.ResponseToDriver?.Length ?? 0) <= 600
                && (!command.CaseStatus.HasValue || Enum.IsDefined(command.CaseStatus.Value))
                && (!command.Decision.HasValue || Enum.IsDefined(command.Decision.Value)))
            {
                message.SenderId = client.UserId;
                message.TargetId = client.Id; // Authenticated return address for the result.
                await SendToHostAsync(message, cancellationToken);
            }
            return true;
        }
        return false;
    }

    private static bool IsOwnerRoutable(MessageType type) =>
        OperatorCommandRules.IsAllowed(type)
        || type is MessageType.RaceControlPanelState
            or MessageType.IncidentSnapshot
            or MessageType.IncidentStateUpdate
            or MessageType.IncidentStatusResult
            or MessageType.IncidentReportAck
            or MessageType.IncidentReportUpdate
            or MessageType.CustomFlagList
            or MessageType.DriverProfileUpdate
            or MessageType.LeagueProfile
            or MessageType.TimeSyncResponse
            or MessageType.AccountSync
            or MessageType.TeamAddMemberResult
            or MessageType.TeamJoinResult
            or MessageType.TeamJoinDecisionResult
            or MessageType.TeamContractOfferResult;

    public async Task ExpireOperatorPriorityAsync(DateTime now, CancellationToken cancellationToken)
    {
        if (_operatorPriority?.Expire(now) == true)
            await BroadcastOperatorSnapshotAsync(cancellationToken);
    }

    private async Task HandleSessionOperationAsync(
        RelayClient client,
        ProtocolMessage message,
        CancellationToken cancellationToken)
    {
        var envelope = message.GetPayload<SessionOperationEnvelope>();
        SessionOperationResult result;
        var applied = false;
        if (envelope == null || envelope.Operation.OperationId == Guid.Empty
            || envelope.Snapshot.ValueKind != JsonValueKind.Object
            || string.IsNullOrWhiteSpace(envelope.Operation.Kind)
            || envelope.Operation.Kind.Length > 80
            || !string.Equals(envelope.Operation.ControlSessionId, Code, StringComparison.OrdinalIgnoreCase))
        {
            result = SessionOperationResult.Rejected("Invalid session operation", _sessionRevision);
        }
        else if (!string.Equals(client.UserId, _sessionOwnerUserId, StringComparison.Ordinal))
        {
            result = SessionOperationResult.Rejected("Current session owner required", _sessionRevision);
        }
        else
        {
            var trustedOperation = envelope.Operation with { OperatorId = client.UserId };
            var trustedEnvelope = new SessionOperationEnvelope(trustedOperation, envelope.Snapshot);
            try
            {
                if (_durableRepository.IsEnabled)
                {
                    var durable = await _durableRepository.ApplyOperationAsync(
                        client.UserId, trustedEnvelope, cancellationToken);
                    lock (_membershipGate)
                    {
                        _sessionGeneration = durable.Generation;
                        _sessionRevision = durable.Revision;
                        if (durable.IsApplied)
                        {
                            _sessionSnapshot = envelope.Snapshot.Clone();
                            _sessionOperationIds.Add(trustedOperation.OperationId);
                            applied = true;
                        }
                    }
                    result = durable.IsApplied
                        ? SessionOperationResult.Applied(durable.Revision)
                        : durable.IsDuplicate
                            ? SessionOperationResult.Duplicate(durable.Revision)
                            : SessionOperationResult.Conflict(durable.Revision);
                }
                else
                {
                    lock (_membershipGate)
                    {
                        if (_sessionOperationIds.Contains(trustedOperation.OperationId))
                            result = SessionOperationResult.Duplicate(_sessionRevision);
                        else if (trustedOperation.Generation != _sessionGeneration
                                 || trustedOperation.ExpectedRevision != _sessionRevision)
                            result = SessionOperationResult.Conflict(_sessionRevision);
                        else
                        {
                            _sessionSnapshot = envelope.Snapshot.Clone();
                            _sessionOperationIds.Add(trustedOperation.OperationId);
                            _sessionRevision++;
                            applied = true;
                            result = SessionOperationResult.Applied(_sessionRevision);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is DurableSessionStoreException
                                       or HttpRequestException
                                       or TaskCanceledException)
            {
                result = SessionOperationResult.Rejected(
                    "Durable session storage is unavailable", _sessionRevision);
            }
        }

        var response = ProtocolMessage.Create(MessageType.SessionOperationResultV2,
            new SessionOperationResultPayload(envelope?.Operation.OperationId ?? Guid.Empty, result));
        StampRelay(response, client.Id);
        await TrySendAsync(client, response, cancellationToken);
        if (applied) await BroadcastSessionSnapshotAsync(cancellationToken);
    }

    public async Task SendSessionSnapshotAsync(
        RelayClient recipient,
        CancellationToken cancellationToken)
    {
        if (!recipient.SupportsSessionStateV2
            || (recipient.Role != "host" && !CanReceiveHostState(recipient))) return;
        SessionSnapshotV2Payload snapshot;
        lock (_membershipGate)
            snapshot = new SessionSnapshotV2Payload(_sessionGeneration, _sessionRevision,
                _sessionOwnerUserId, _sessionSnapshot.Clone());
        var message = ProtocolMessage.Create(MessageType.SessionSnapshotV2, snapshot);
        StampRelay(message, recipient.Id);
        await TrySendAsync(recipient, message, cancellationToken);
    }

    private Task BroadcastSessionSnapshotAsync(CancellationToken cancellationToken)
    {
        var recipients = _clients.Values.Where(client => client.SupportsSessionStateV2
            && (client.Role == "host" || CanReceiveHostState(client))).ToArray();
        return Task.WhenAll(recipients.Select(client =>
            SendSessionSnapshotAsync(client, cancellationToken)));
    }

    public async Task BroadcastOperatorSnapshotAsync(CancellationToken cancellationToken)
    {
        var state = _operatorPriority?.Snapshot();
        if (state == null) return;
        var approved = state.Operators.Select(seat => seat.Id).ToHashSet(StringComparer.Ordinal);
        var recipients = _clients.Values.Where(client => client.Role == "host"
            || (RelayRoles.IsSecondaryOperator(client.Role) && approved.Contains(client.UserId))).ToArray();
        if (recipients.Length == 0) return;
        var message = ProtocolMessage.Create(MessageType.OperatorSnapshot, new OperatorSnapshotPayload(state));
        StampRelay(message, "all");
        await Task.WhenAll(recipients.Select(client => TrySendAsync(client, message, cancellationToken)));
    }

    public async Task BroadcastDriverListAsync(CancellationToken cancellationToken)
    {
        var approvedOperators = _operatorPriority?.Snapshot().Operators
            .Where(seat => seat.IsConnected && seat.Permissions.HasFlag(OperatorPermissions.Observe))
            .Select(seat => seat.Id)
            .ToHashSet(StringComparer.Ordinal) ?? [];
        var recipients = _clients.Values
            .Where(c => c.Socket.State == WebSocketState.Open
                && (c.Role is "host" or "driver"
                    || (RelayRoles.IsSecondaryOperator(c.Role)
                        && approvedOperators.Contains(c.UserId))))
            .ToArray();
        if (recipients.Length == 0)
        {
            return;
        }

        var message = ProtocolMessage.Create(
            MessageType.DriverList,
            new DriverListPayload
            {
                Drivers = recipients.Where(c => c.Role == "driver").Select(c => new DriverInfo
                {
                    Id = c.Id,
                    Name = c.Name,
                    AccountUserId = Guid.TryParse(c.UserId, out var authenticatedUserId)
                        ? authenticatedUserId.ToString("D") : null,
                    IsConnected = true
                }).ToList()
            });
        message.SessionCode = Code;
        message.SessionId = Code;
        message.SenderId = "relay";
        await Task.WhenAll(recipients.Select(c => TrySendAsync(c, message, cancellationToken)));
    }

    private bool CanReceiveHostState(RelayClient client)
    {
        if (client.Role == "driver") return true;
        if (!RelayRoles.IsSecondaryOperator(client.Role)) return false;
        return _operatorPriority?.Snapshot().Operators.Any(seat => seat.Id == client.UserId
            && seat.IsConnected && seat.Permissions.HasFlag(OperatorPermissions.Observe)) == true;
    }

    private bool CanReceiveIncidentState(RelayClient client)
    {
        if (client.Role == "host" && client.SupportsDurableIncidents) return true;
        if (!client.SupportsLiveIncidents || !RelayRoles.IsSecondaryOperator(client.Role)) return false;
        return _operatorPriority?.Snapshot().Operators.Any(seat => seat.Id == client.UserId
            && seat.IsConnected && seat.Permissions.HasFlag(OperatorPermissions.Incidents)) == true;
    }

    private bool RetainIncidentState(ProtocolMessage message)
    {
        if (message.Type == MessageType.IncidentSnapshot)
        {
            var snapshot = message.GetPayload<IncidentSnapshotPayload>();
            if (snapshot == null || snapshot.Revision < _incidentRevision) return false;
            _incidents.Clear();
            foreach (var incident in snapshot.Incidents.Where(item => item != null && !string.IsNullOrWhiteSpace(item.Id)))
                _incidents[incident.Id] = incident;
            _incidentRevision = snapshot.Revision;
            return true;
        }
        else if (message.Type == MessageType.IncidentStateUpdate)
        {
            var update = message.GetPayload<IncidentStateUpdatePayload>();
            if (update == null || update.Revision <= _incidentRevision || update.Incident == null
                || string.IsNullOrWhiteSpace(update.Incident.Id)) return false;
            _incidents[update.Incident.Id] = update.Incident;
            _incidentRevision = update.Revision;
            return true;
        }
        return false;
    }

    private async Task SendRetainedIncidentStateAsync(
        RelayClient recipient,
        CancellationToken cancellationToken)
    {
        if (!CanReceiveIncidentState(recipient)
            || (_incidentRevision <= 0 && !recipient.SupportsDurableIncidents)) return;
        IncidentSnapshotPayload snapshot;
        lock (_membershipGate)
            snapshot = new IncidentSnapshotPayload(_incidentRevision, _incidents.Values.ToArray(),
                _sessionGeneration);
        if (recipient.SupportsDurableIncidents)
        {
            var pages = new List<List<IncidentReport>> { new() };
            var currentBytes = 0;
            foreach (var incident in snapshot.Incidents)
            {
                var size = JsonSerializer.SerializeToUtf8Bytes(incident).Length;
                if (size > 160_000) throw new InvalidDataException("Incident exceeds snapshot page size.");
                if (currentBytes + size > 160_000 && pages[^1].Count > 0)
                {
                    pages.Add(new List<IncidentReport>());
                    currentBytes = 0;
                }
                pages[^1].Add(incident);
                currentBytes += size;
            }
            for (var index = 0; index < pages.Count; index++)
            {
                var page = ProtocolMessage.Create(MessageType.IncidentSnapshotPage,
                    new IncidentSnapshotPagePayload(snapshot.Generation, snapshot.Revision,
                        index, pages.Count, pages[index]));
                StampRelay(page, recipient.Id);
                if (!await TrySendAsync(recipient, page, cancellationToken)) break;
            }
        }
        else
        {
            var message = ProtocolMessage.Create(MessageType.IncidentSnapshot, snapshot);
            StampRelay(message, recipient.Id);
            await TrySendAsync(recipient, message, cancellationToken);
        }
    }

    private async Task SendRetainedPanelStateAsync(
        RelayClient recipient,
        CancellationToken cancellationToken)
    {
        ProtocolMessage? retained;
        lock (_membershipGate)
            retained = _latestPanelState == null
                ? null
                : ProtocolMessage.FromLegacyCompatibleJson(_latestPanelState.ToJson());
        if (retained != null && CanReceiveHostState(recipient))
            await TrySendAsync(recipient, retained, cancellationToken);
    }

    private void StampRelay(ProtocolMessage message, string targetId)
    {
        message.SessionCode = Code;
        message.SessionId = Code;
        message.SenderId = "relay";
        message.TargetId = targetId;
    }

    private async Task<bool> TrySendAsync(
        RelayClient recipient,
        ProtocolMessage message,
        CancellationToken cancellationToken)
    {
        using var sendTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        sendTimeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await recipient.SendAsync(message, sendTimeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            RemoveClient(recipient.Id);
            return false;
        }
        catch (Exception ex) when (ex is WebSocketException
                                   or IOException
                                   or InvalidOperationException
                                   or ArgumentException
                                   or InvalidDataException
                                   or ObjectDisposedException)
        {
            // A dead recipient is local to that connection. Never let its send
            // failure escape into the sender's request loop and tear down HOST.
            RemoveClient(recipient.Id);
            return false;
        }
    }

    public async Task RemoveTimedOutClientsAsync(
        DateTime cutoffUtc,
        CancellationToken cancellationToken)
    {
        foreach (var client in _clients.Values.Where(c => c.LastSeenUtc < cutoffUtc).ToArray())
        {
            if (client.Socket.State == WebSocketState.Open)
            {
                try
                {
                    await client.Socket.CloseOutputAsync(
                        WebSocketCloseStatus.EndpointUnavailable,
                        "Heartbeat timeout",
                        cancellationToken);
                }
                catch
                {
                }
            }
            RemoveClient(client.Id);
        }
    }

    public async Task CloseAllAsync(string reason, CancellationToken cancellationToken)
    {
        await StopAuthorityRuntimeAsync();
        foreach (var client in _clients.Values.ToArray())
        {
            if (client.Socket.State == WebSocketState.Open)
            {
                try
                {
                    await client.Socket.CloseOutputAsync(
                        WebSocketCloseStatus.EndpointUnavailable,
                        reason,
                        cancellationToken);
                }
                catch
                {
                }
            }
            RemoveClient(client.Id);
        }
    }
}
