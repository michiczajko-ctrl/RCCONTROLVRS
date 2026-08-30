using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VRS.RaceControl.Shared.Models;
using VRS.RaceControl.Shared.Protocol;

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

app.MapGet("/", () => Results.Ok(new
{
    status = "VRS Race Control Relay",
    version = "BETA 2.3.1",
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

        if (joinMessage?.Type != MessageType.Join
            || joinPayload == null
            || !RelayValidation.TryNormalizeSessionCode(joinPayload.SessionCode, out var sessionCode)
            || !RelayValidation.TryNormalizeRole(joinPayload.Role, out var role))
        {
            await RelayWebSockets.RejectAsync(
                socket,
                "Nieprawidłowe żądanie dołączenia.",
                limits.MaxMessageBytes,
                context.RequestAborted);
            return;
        }

        if (role == "driver" && !sessions.TryGetValue(sessionCode, out session))
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

        var clientName = RelayValidation.SanitizeName(joinPayload.DriverName, role);
        client = new RelayClient(
            socket,
            Guid.NewGuid().ToString("N")[..12],
            clientName,
            role,
            limits.MaxMessageBytes);

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
                }
            });
        acknowledgement.SessionCode = sessionCode;
        acknowledgement.SessionId = sessionCode;
        acknowledgement.SenderId = "relay";
        acknowledgement.TargetId = client.Id;
        await client.SendAsync(acknowledgement, context.RequestAborted);
        await session.BroadcastDriverListAsync(context.RequestAborted);

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

            client.MarkSeen();
            message.SenderId = client.Id;
            message.SessionCode = sessionCode;
            message.SessionId = sessionCode;

            if (message.Type == MessageType.Heartbeat)
            {
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
                await session.RouteHostMessageAsync(message, context.RequestAborted);
            }
            else
            {
                await session.SendToHostAsync(message, context.RequestAborted);
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
            if (session.ClientCount == 0 || session.Host == null)
            {
                await session.CloseAllAsync("HOST disconnected", CancellationToken.None);
                sessions.TryRemove(session.Code, out _);
            }
        }
        socket?.Dispose();
        connectionGate.Release();
    }
});

_ = RunSessionWatchdogAsync(app.Lifetime.ApplicationStopping);
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
                if (session.ClientCount == 0 || session.Host == null)
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

    public RelayClient(
        WebSocket socket,
        string id,
        string name,
        string role,
        int maxMessageBytes)
    {
        Socket = socket;
        Id = id;
        Name = name;
        Role = role;
        _maxMessageBytes = maxMessageBytes;
    }

    public WebSocket Socket { get; }
    public string Id { get; }
    public string Name { get; }
    public string Role { get; }
    public DateTime LastSeenUtc { get; private set; } = DateTime.UtcNow;

    public void MarkSeen() => LastSeenUtc = DateTime.UtcNow;

    public async Task SendAsync(ProtocolMessage message, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
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
        Socket.Dispose();
        _sendLock.Dispose();
    }
}

public sealed class RelaySession
{
    private readonly ConcurrentDictionary<string, RelayClient> _clients = new();
    private readonly object _membershipGate = new();
    private readonly int _maxClients;

    public RelaySession(string code, int maxClients)
    {
        Code = code;
        _maxClients = maxClients;
    }

    public string Code { get; }
    public DateTime CreatedAtUtc { get; } = DateTime.UtcNow;
    public int ClientCount => _clients.Count;
    public RelayClient? Host => _clients.Values.FirstOrDefault(c => c.Role == "host");
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
            if (client.Role == "host" && Host != null)
            {
                rejectionReason = "HOST dla tej sesji jest już połączony.";
                return false;
            }
            if (client.Role == "driver"
                && _clients.Values.Any(existing =>
                    existing.Role == "driver"
                    && string.Equals(
                        existing.Name,
                        client.Name,
                        StringComparison.OrdinalIgnoreCase)))
            {
                rejectionReason = "To konto kierowcy jest już połączone z sesją.";
                return false;
            }

            rejectionReason = string.Empty;
            return _clients.TryAdd(client.Id, client);
        }
    }

    public void RemoveClient(string id)
    {
        RelayClient? client;
        lock (_membershipGate)
        {
            _clients.TryRemove(id, out client);
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
        if (!string.IsNullOrWhiteSpace(message.TargetId) && message.TargetId != "all")
        {
            if (_clients.TryGetValue(message.TargetId, out var target)
                && target.Role == "driver")
            {
                await target.SendAsync(message, cancellationToken);
            }
            return;
        }

        await Task.WhenAll(_clients.Values
            .Where(c => c.Role == "driver")
            .Select(c => c.SendAsync(message, cancellationToken)));
    }

    public async Task SendToHostAsync(
        ProtocolMessage message,
        CancellationToken cancellationToken)
    {
        var host = Host;
        if (host != null)
        {
            await host.SendAsync(message, cancellationToken);
        }
    }

    public async Task BroadcastDriverListAsync(CancellationToken cancellationToken)
    {
        var recipients = _clients.Values
            .Where(c => c.Socket.State == WebSocketState.Open)
            .ToArray();
        if (recipients.Length == 0)
        {
            return;
        }

        var message = ProtocolMessage.Create(
            MessageType.DriverList,
            new DriverListPayload
            {
                Drivers = recipients.Select(c => new DriverInfo
                {
                    Id = c.Id,
                    Name = c.Name,
                    IsConnected = true
                }).ToList()
            });
        message.SessionCode = Code;
        message.SessionId = Code;
        message.SenderId = "relay";
        await Task.WhenAll(recipients.Select(c => c.SendAsync(message, cancellationToken)));
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
