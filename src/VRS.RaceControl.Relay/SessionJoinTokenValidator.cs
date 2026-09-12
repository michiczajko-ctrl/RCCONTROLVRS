using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public sealed record SessionJoinIdentity(
    string UserId,
    string SessionId,
    string LeagueId,
    string Role,
    int Permissions,
    string DisplayName,
    string TokenId,
    DateTimeOffset ExpiresAt);

public sealed class SessionJoinTokenValidator
{
    private readonly byte[] _secret;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly TimeSpan _clockSkew;

    public SessionJoinTokenValidator(string secret, string issuer, string audience, TimeSpan? clockSkew = null)
    {
        _secret = Encoding.UTF8.GetBytes(secret ?? string.Empty);
        if (_secret.Length < 32) throw new ArgumentException("Session signing key must contain at least 32 bytes.", nameof(secret));
        _issuer = string.IsNullOrWhiteSpace(issuer) ? throw new ArgumentException("Issuer is required.", nameof(issuer)) : issuer;
        _audience = string.IsNullOrWhiteSpace(audience) ? throw new ArgumentException("Audience is required.", nameof(audience)) : audience;
        _clockSkew = clockSkew ?? TimeSpan.FromSeconds(15);
    }

    public static SessionJoinTokenValidator FromEnvironment()
    {
        var secret = Environment.GetEnvironmentVariable("VRS_SESSION_SIGNING_KEY") ?? string.Empty;
        var issuer = Environment.GetEnvironmentVariable("VRS_SESSION_TOKEN_ISSUER") ?? "vrs-race-control-edge";
        var audience = Environment.GetEnvironmentVariable("VRS_SESSION_TOKEN_AUDIENCE") ?? "vrs-race-control-relay";
        return new SessionJoinTokenValidator(secret, issuer, audience);
    }

    public bool TryValidate(string? token, out SessionJoinIdentity? identity, out string error)
    {
        identity = null;
        error = "invalid_session_token";
        if (string.IsNullOrWhiteSpace(token) || token.Length > 8192) return false;
        var parts = token.Split('.');
        if (parts.Length != 3) return false;

        try
        {
            using var header = JsonDocument.Parse(Decode(parts[0]));
            if (!header.RootElement.TryGetProperty("alg", out var alg) || alg.GetString() != "HS256") return false;
            var unsigned = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
            using var hmac = new HMACSHA256(_secret);
            var expected = hmac.ComputeHash(unsigned);
            var actual = Decode(parts[2]);
            if (actual.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(actual, expected)) return false;

            using var payload = JsonDocument.Parse(Decode(parts[1]));
            var root = payload.RootElement;
            var issuer = RequiredString(root, "iss", 160);
            var audience = RequiredString(root, "aud", 160);
            var userId = RequiredString(root, "sub", 128);
            var sessionId = RequiredString(root, "session_id", 128);
            var leagueId = RequiredString(root, "league_id", 128);
            var role = RequiredString(root, "role", 16);
            var permissions = root.TryGetProperty("permissions", out var permissionsElement)
                && permissionsElement.ValueKind == JsonValueKind.Number
                ? permissionsElement.GetInt32()
                : DefaultPermissions(role);
            var displayName = RequiredString(root, "display_name", 80);
            var tokenId = RequiredString(root, "jti", 128);
            var expires = root.GetProperty("exp").GetInt64();
            var notBefore = root.GetProperty("nbf").GetInt64();
            var now = DateTimeOffset.UtcNow;

            if (!string.Equals(issuer, _issuer, StringComparison.Ordinal)
                || !string.Equals(audience, _audience, StringComparison.Ordinal)
                || role is not ("host" or "driver" or "viewer" or "operator" or "observer" or "steward")
                || !ValidPermissions(role, permissions)
                || DateTimeOffset.FromUnixTimeSeconds(expires) < now.Subtract(_clockSkew)
                || DateTimeOffset.FromUnixTimeSeconds(notBefore) > now.Add(_clockSkew))
            {
                return false;
            }

            identity = new SessionJoinIdentity(
                userId, sessionId, leagueId, role, permissions, displayName, tokenId,
                DateTimeOffset.FromUnixTimeSeconds(expires));
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or KeyNotFoundException
                                   or InvalidOperationException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static int DefaultPermissions(string role) => role switch
    {
        "host" => 7,
        "viewer" or "observer" => 1,
        "steward" => 3,
        _ => 0
    };

    private static bool ValidPermissions(string role, int permissions) => role switch
    {
        "host" => permissions == 7,
        "driver" => permissions == 0,
        "viewer" or "observer" => permissions == 1,
        "steward" => permissions == 3,
        "operator" => permissions is 1 or 3 or 5 or 7,
        _ => false
    };

    private static string RequiredString(JsonElement root, string name, int maxLength)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new KeyNotFoundException(name);
        var text = value.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text) || text.Length > maxLength) throw new FormatException(name);
        return text;
    }

    private static byte[] Decode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += (normalized.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            0 => "",
            _ => throw new FormatException()
        };
        return Convert.FromBase64String(normalized);
    }
}

public sealed class SessionTokenReplayGuard
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _tokens = new(StringComparer.Ordinal);

    public bool TryAccept(string tokenId, DateTimeOffset expiresAt)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var expired in _tokens.Where(entry => entry.Value <= now).Select(entry => entry.Key).Take(128))
            _tokens.TryRemove(expired, out _);
        return expiresAt > now && _tokens.TryAdd(tokenId, expiresAt);
    }
}

public sealed class RelaySlidingWindowRateLimiter(int limit, TimeSpan window)
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _requests = new(StringComparer.Ordinal);
    private int _requestsUntilSweep;

    public bool TryAcquire(string key)
    {
        if ((Interlocked.Increment(ref _requestsUntilSweep) & 255) == 0)
            SweepExpired();
        var queue = _requests.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            var cutoff = DateTimeOffset.UtcNow - window;
            while (queue.Count > 0 && queue.Peek() <= cutoff) queue.Dequeue();
            if (queue.Count >= limit) return false;
            queue.Enqueue(DateTimeOffset.UtcNow);
            return true;
        }
    }

    private void SweepExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - window;
        foreach (var entry in _requests.Take(256))
        {
            lock (entry.Value)
            {
                while (entry.Value.Count > 0 && entry.Value.Peek() <= cutoff)
                    entry.Value.Dequeue();
                if (entry.Value.Count == 0)
                    _requests.TryRemove(entry.Key, out _);
            }
        }
    }
}
