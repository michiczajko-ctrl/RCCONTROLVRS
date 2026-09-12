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

var sessions = new ConcurrentDictionary<string, RelaySession>(StringComparer.OrdinalIgnoreCase);
var sessionCreationGate = new object();
var connectionGate = new RelayCapacityGate(limits.MaxConnections);
var replayGuard = new SessionTokenReplayGuard();
var ipJoinLimiter = new RelaySlidingWindowRateLimiter(30, TimeSpan.FromMinutes(1));
var accountJoinLimiter = new RelaySlidingWindowRateLimiter(10, TimeSpan.FromMinutes(1));
var accountMessageLimiter = new RelaySlidingWindowRateLimiter(600, TimeSpan.FromMinutes(1));
var sessionMessageLimiter = new RelaySlidingWindowRateLimiter(5000, TimeSpan.FromMinutes(1));
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
    throw new InvalidOperationException(
        "VRS_SESSION_SIGNING_KEY (minimum 32 bytes) is required unless VRS_ALLOW_LEGACY_JOIN=true.");
}

app.MapGet("/", () => Results.Ok(new
{
    status = "VRS Race Control Relay",
    version = VRS.RaceControl.Shared.Diagnostics.BuildInfo.DisplayVersion,
    protocolVersion = ProtocolMessage.CurrentProtocolVersion,
    activeSessions = sessions.Count,
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
}));
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
        if (tokenValidator != null
            && tokenValidator.TryValidate(joinPayload.SessionJoinToken, out var identity, out _)
            && identity != null)
        {
            if (!replayGuard.TryAccept(identity.TokenId, identity.ExpiresAt)
                || !accountJoinLimiter.TryAcquire(identity.UserId))
            {
                await RelayWebSockets.RejectAsync(
                    socket, "Token połączenia został już użyty albo przekroczono limit prób.",
                    limits.MaxMessageBytes, context.RequestAborted);
                return;
            }
            sessionCode = identity.SessionId;
            role = identity.Role;
            userId = identity.UserId;
            clientName = RelayValidation.SanitizeName(identity.DisplayName, role);
            permissions = identity.Permissions;
            supportsMultiHost = joinPayload.Capabilities?.Contains(
                ProtocolCapabilities.MultiHostOperators, StringComparer.Ordinal) == true;
        }
        else if (allowLegacyJoin
                 && RelayValidation.TryNormalizeSessionCode(joinPayload.SessionCode, out sessionCode)
                 && RelayValidation.TryNormalizeRole(joinPayload.Role, out role))
        {
            clientName = RelayValidation.SanitizeName(joinPayload.DriverName, role);
            userId = $"legacy:{clientName.ToUpperInvariant()}";
            permissions = role == "host" ? 7 : 0;
            supportsMultiHost = false;
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

        if (role != "host" && !sessions.TryGetValue(sessionCode, out session))
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
                        code => new RelaySession(code, limits.MaxClientsPerSession));
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
                context.RequestAborted);
            return;
        }

        client = new RelayClient(
            socket,
            Guid.NewGuid().ToString("N")[..12],
            userId,
            clientName,
            role,
            limits.MaxMessageBytes,
            permissions,
            supportsMultiHost);

        if (!session.TryAddClient(client, out var rejectionReason))
        {
            await RelayWebSockets.RejectAsync(
                socket,
                rejectionReason,
                limits.MaxMessageBytes,
                context.RequestAborted);
            if (session.ClientCount == 0)
            {
                sessions.TryRemove(sessionCode, out _);
            }
            return;
        }

        var acknowledgement = ProtocolMessage.Create(
            MessageType.JoinAck,
            new JoinAckPayload
            {
                DriverId = client.Id,
                SessionInfo = new SessionInfo
                {
                    SessionCode = sessionCode,
                    CreatedAt = session.CreatedAtUtc,
                    IsActive = true
                },
                Capabilities = client.SupportsMultiHost
                    ? [ProtocolCapabilities.MultiHostOperators]
                    : []
            });
        acknowledgement.SessionCode = sessionCode;
        acknowledgement.SessionId = sessionCode;
        acknowledgement.SenderId = "relay";
        acknowledgement.TargetId = client.Id;
        await client.SendAsync(acknowledgement, context.RequestAborted);
        await session.BroadcastDriverListAsync(context.RequestAborted);
        await session.NotifyOperatorConnectionAsync(client, context.RequestAborted);
        await session.BroadcastOperatorSnapshotAsync(context.RequestAborted);

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

            if (!accountMessageLimiter.TryAcquire(client.UserId)
                || !sessionMessageLimiter.TryAcquire(sessionCode))
            {
                app.Logger.LogWarning(
                    "Relay message rate limit exceeded for session {SessionCode}", sessionCode);
                await socket.CloseOutputAsync(
                    WebSocketCloseStatus.PolicyViolation,
                    "Message rate limit exceeded",
                    context.RequestAborted);
                break;
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
            if (client.Role != "host"
                && message.Type != MessageType.Ack
                && !session.Deduplicator.TryAccept(message))
            {
                continue;
            }

            if (client.Role == "host")
            {
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
    catch (WebSocketException)
    {
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
        if (session != null && client != null)
        {
            session.RemoveClient(client.Id);
            await session.BroadcastDriverListAsync(CancellationToken.None);
            await session.BroadcastOperatorSnapshotAsync(CancellationToken.None);
            if (session.ClientCount == 0)
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
                return ProtocolMessage.FromJson(message.ToUtf8String());
            }
        }
    }

    public static async Task RejectAsync(
        WebSocket socket,
        string reason,
        int maxMessageBytes,
        CancellationToken cancellationToken)
    {
        var rejection = ProtocolMessage.Create(
            MessageType.JoinReject,
            new JoinRejectPayload { Reason = reason });
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
        bool supportsMultiHost = false)
        : this(socket, id, $"legacy:{id}", name, role, maxMessageBytes, permissions, supportsMultiHost)
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
        bool supportsMultiHost = false)
    {
        Socket = socket;
        Id = id;
        UserId = userId;
        Name = name;
        Role = role;
        Permissions = permissions;
        SupportsMultiHost = supportsMultiHost;
        _maxMessageBytes = maxMessageBytes;
    }

    public WebSocket Socket { get; }
    public string Id { get; }
    public string UserId { get; }
    public string Name { get; }
    public string Role { get; }
    public int Permissions { get; }
    public bool SupportsMultiHost { get; }
    public DateTime LastSeenUtc { get; private set; } = DateTime.UtcNow;

    public void MarkSeen() => LastSeenUtc = DateTime.UtcNow;

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

public sealed class RelaySession
{
    private readonly ConcurrentDictionary<string, RelayClient> _clients = new();
    private readonly object _membershipGate = new();
    private readonly int _maxClients;
    private string? _mainUserId;
    private OperatorPriority? _operatorPriority;
    private ProtocolMessage? _latestPanelState;

    public RelaySession(string code, int maxClients)
    {
        Code = code;
        _maxClients = maxClients;
    }

    public string Code { get; }
    public DateTime CreatedAtUtc { get; } = DateTime.UtcNow;
    public int ClientCount => _clients.Count;
    public RelayClient? Host => _clients.Values.FirstOrDefault(c => c.Role == "host");
    public OperatorPriorityState? OperatorState => _operatorPriority?.Snapshot();
    public MessageDeduplicator Deduplicator { get; } = new(
        TimeSpan.FromMinutes(2),
        RelayLimits.DeduplicationCapacityPerSession);

    public bool TryAddClient(RelayClient client, out string rejectionReason)
    {
        lock (_membershipGate)
        {
            if (_clients.Count >= _maxClients)
            {
                rejectionReason = "Ta sesja osiągnęła limit połączonych kierowców.";
                return false;
            }
            if (_clients.Values.Any(existing =>
                string.Equals(existing.UserId, client.UserId, StringComparison.Ordinal)))
            {
                rejectionReason = "To konto jest już połączone z sesją.";
                return false;
            }
            if (client.Role == "host" && Host != null)
            {
                rejectionReason = "HOST dla tej sesji jest już połączony.";
                return false;
            }
            if (client.Role == "host" && _mainUserId != null
                && !string.Equals(_mainUserId, client.UserId, StringComparison.Ordinal))
            {
                rejectionReason = "Tylko właściciel sesji może ponownie połączyć główny HOST.";
                return false;
            }
            if (client.Role != "host" && Host == null)
            {
                rejectionReason = "Główny HOST jest niedostępny; sesja pozostaje tylko do odczytu dla już połączonych operatorów.";
                return false;
            }
            if (RelayRoles.IsSecondaryOperator(client.Role)
                && (_operatorPriority == null || Host?.SupportsMultiHost != true || !client.SupportsMultiHost))
            {
                rejectionReason = "Ta sesja nie obsługuje operatorów multi-HOST.";
                return false;
            }

            rejectionReason = string.Empty;
            if (!_clients.TryAdd(client.Id, client)) return false;
            if (client.Role == "host")
            {
                _mainUserId ??= client.UserId;
                _operatorPriority ??= new OperatorPriority(client.UserId, client.Name, DateTime.UtcNow);
                _operatorPriority.Heartbeat(client.UserId, DateTime.UtcNow);
            }
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

    public async Task RouteHostMessageAsync(
        ProtocolMessage message,
        CancellationToken cancellationToken)
    {
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
        var host = Host;
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

    public Task HandleHeartbeatAsync(RelayClient client, CancellationToken cancellationToken)
    {
        if ((client.Role == "host" || RelayRoles.IsSecondaryOperator(client.Role))
            && _operatorPriority != null)
            _operatorPriority.Heartbeat(client.UserId, DateTime.UtcNow);
        return Task.CompletedTask;
    }

    public async Task<bool> HandleHostOperatorMessageAsync(RelayClient client, ProtocolMessage message,
        CancellationToken cancellationToken)
    {
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
            await SendRetainedPanelStateAsync(approvedOperator, cancellationToken);
        return true;
    }

    public async Task<bool> HandleOperatorMessageAsync(RelayClient client, ProtocolMessage message,
        CancellationToken cancellationToken)
    {
        var priority = _operatorPriority;
        if (priority == null || !RelayRoles.IsSecondaryOperator(client.Role)) return false;
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
        return false;
    }

    public async Task ExpireOperatorPriorityAsync(DateTime now, CancellationToken cancellationToken)
    {
        if (_operatorPriority?.Expire(now) == true)
            await BroadcastOperatorSnapshotAsync(cancellationToken);
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
        try
        {
            await recipient.SendAsync(message, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is WebSocketException
                                   or IOException
                                   or InvalidOperationException
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
