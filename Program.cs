using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using VRS.RaceControl.Shared.Models;
using VRS.RaceControl.Shared.Protocol;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{Environment.GetEnvironmentVariable("PORT") ?? "8080"}");

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });

// ── Session registry ─────────────────────────────────────────────────────────
var sessions = new ConcurrentDictionary<string, RelaySession>(StringComparer.OrdinalIgnoreCase);

// ── Health check ─────────────────────────────────────────────────────────────
app.MapGet("/", () => Results.Ok(new { status = "VRS Race Control Relay", time = DateTime.UtcNow }));
app.MapGet("/health", () => Results.Ok("ok"));

// ── WebSocket endpoint ────────────────────────────────────────────────────────
app.Map("/vrs/", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        return;
    }

    var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    Console.WriteLine($"[RELAY] New WS connection from {ctx.Connection.RemoteIpAddress}");

    // ── First message must be JOIN ────────────────────────────────────────────
    ProtocolMessage? joinMsg = null;
    try
    {
        joinMsg = await RelayWebSockets.ReceiveMessageAsync(ws, CancellationToken.None);
    }
    catch { }

    if (joinMsg == null || joinMsg.Type != MessageType.Join)
    {
        await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Expected JOIN", CancellationToken.None);
        return;
    }

    var joinPayload = joinMsg.GetPayload<JoinPayload>();
    if (joinPayload == null || string.IsNullOrEmpty(joinPayload.SessionCode))
    {
        await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Invalid JOIN payload", CancellationToken.None);
        return;
    }

    var sessionCode = joinPayload.SessionCode.ToUpperInvariant();
    var driverName  = joinPayload.DriverName ?? "Unknown";
    var role        = joinPayload.Role ?? "driver";   // "host" or "driver"
    var driverId    = Guid.NewGuid().ToString("N")[..8];

    Console.WriteLine($"[RELAY] JOIN session={sessionCode} role={role} name={driverName} id={driverId}");

    // ── Get or create session ─────────────────────────────────────────────────
    var session = sessions.GetOrAdd(sessionCode, code => new RelaySession(code));

    // ── Register client in session ────────────────────────────────────────────
    var client = new RelayClient(ws, driverId, driverName, role);
    session.AddClient(client);

    // Send ACK back to this client
    var ack = ProtocolMessage.Create(MessageType.JoinAck,
        new JoinAckPayload
        {
            DriverId = driverId,
            SessionInfo = new SessionInfo
            {
                SessionCode = sessionCode,
                CreatedAt   = DateTime.UtcNow,
                IsActive    = true
            }
        });
    ack.SessionCode = sessionCode;
    ack.TargetId    = driverId;
    await RelayWebSockets.SendMessageAsync(ws, ack);

    // Notify everyone of updated driver list
    await session.BroadcastDriverListAsync();

    Console.WriteLine($"[RELAY] session={sessionCode} now has {session.ClientCount} client(s)");

    // ── Receive loop ──────────────────────────────────────────────────────────
    try
    {
        while (ws.State == WebSocketState.Open)
        {
            var msg = await RelayWebSockets.ReceiveMessageAsync(ws, CancellationToken.None);
            if (msg == null) break;

            msg.SenderId = driverId;
            client.UpdateHeartbeat();

            switch (msg.Type)
            {
                case MessageType.Heartbeat:
                    // Echo heartbeat back so client stays connected
                    await RelayWebSockets.SendMessageAsync(ws, new ProtocolMessage { Type = MessageType.Heartbeat });
                    break;

                case MessageType.Ack:
                    // Forward ACK to host
                    await session.SendToHostAsync(msg);
                    break;

                default:
                    if (role == "host")
                    {
                        // Host → broadcast to all drivers
                        Console.WriteLine($"[RELAY] {sessionCode} HOST→ALL type={msg.Type}");
                        await session.BroadcastToDriversAsync(msg);
                    }
                    else
                    {
                        // Driver → forward to host
                        await session.SendToHostAsync(msg);
                    }
                    break;
            }
        }
    }
    catch (WebSocketException) { /* disconnected */ }
    catch (Exception ex) { Console.WriteLine($"[RELAY] Error: {ex.Message}"); }
    finally
    {
        session.RemoveClient(client);
        await session.BroadcastDriverListAsync();
        Console.WriteLine($"[RELAY] LEFT session={sessionCode} id={driverId}. Remaining: {session.ClientCount}");

        if (session.ClientCount == 0)
        {
            sessions.TryRemove(sessionCode, out _);
        }
    }
});

app.Run();

// ─────────────────────────────────────────────────────────────────────────────
// WebSocket Helpers
// ─────────────────────────────────────────────────────────────────────────────
public static class RelayWebSockets
{
    public static async Task SendMessageAsync(WebSocket ws, ProtocolMessage msg)
    {
        if (ws.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(msg.ToJson());
        try
        {
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch { /* ignore send errors */ }
    }

    public static async Task<ProtocolMessage?> ReceiveMessageAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var sb = new StringBuilder();

        try
        {
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            } while (!result.EndOfMessage);

            return ProtocolMessage.FromJson(sb.ToString());
        }
        catch { return null; }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Domain types
// ─────────────────────────────────────────────────────────────────────────────
public class RelayClient(WebSocket socket, string id, string name, string role)
{
    public WebSocket Socket   { get; } = socket;
    public string Id          { get; } = id;
    public string Name        { get; } = name;
    public string Role        { get; } = role;  // "host" or "driver"
    public DateTime LastSeen  { get; private set; } = DateTime.UtcNow;
    public SemaphoreSlim Lock { get; } = new(1, 1);

    public void UpdateHeartbeat() => LastSeen = DateTime.UtcNow;

    public async Task SendAsync(ProtocolMessage msg)
    {
        await Lock.WaitAsync();
        try   { await RelayWebSockets.SendMessageAsync(Socket, msg); }
        finally { Lock.Release(); }
    }
}

public class RelaySession(string code)
{
    private readonly ConcurrentDictionary<string, RelayClient> _clients = new();
    public string Code => code;
    public int ClientCount => _clients.Count;

    public void AddClient(RelayClient c)    => _clients[c.Id] = c;
    public void RemoveClient(RelayClient c) => _clients.TryRemove(c.Id, out _);

    public RelayClient? Host => _clients.Values.FirstOrDefault(c => c.Role == "host");

    public async Task SendToHostAsync(ProtocolMessage msg)
    {
        var host = Host;
        if (host != null) await host.SendAsync(msg);
    }

    public async Task BroadcastToDriversAsync(ProtocolMessage msg)
    {
        var tasks = _clients.Values
            .Where(c => c.Role == "driver")
            .Select(c => c.SendAsync(msg));
        await Task.WhenAll(tasks);
    }

    public async Task BroadcastDriverListAsync()
    {
        var drivers = _clients.Values.Select(c => new DriverInfo
        {
            Id          = c.Id,
            Name        = c.Name,
            IsConnected = c.Socket.State == WebSocketState.Open
        }).ToList();

        var msg = ProtocolMessage.Create(MessageType.DriverList,
            new DriverListPayload { Drivers = drivers });
        msg.SessionCode = Code;

        var tasks = _clients.Values.Select(c => c.SendAsync(msg));
        await Task.WhenAll(tasks);
    }
}
