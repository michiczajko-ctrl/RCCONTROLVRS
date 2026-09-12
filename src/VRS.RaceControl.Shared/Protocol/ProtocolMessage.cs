using System.Text.Json;
using System.Text.Json.Serialization;
using VRS.RaceControl.Shared.Enums;

namespace VRS.RaceControl.Shared.Protocol;

/// <summary>
/// Wire protocol message that wraps all communication between host and clients.
/// Serialized to JSON for WebSocket transport.
/// </summary>
public class ProtocolMessage
{
    public const string CurrentProtocolVersion = "1.0";

    /// <summary>Unique message identifier for tracking and ACK.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Type of this message.</summary>
    [JsonPropertyName("type")]
    public MessageType Type { get; set; }

    /// <summary>UTC timestamp of when the message was created.</summary>
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>Wire protocol version used by the sender.</summary>
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; set; } = CurrentProtocolVersion;

    /// <summary>Stable session identifier. SessionCode remains for BETA compatibility.</summary>
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Session code this message belongs to.</summary>
    [JsonPropertyName("sessionCode")]
    public string SessionCode { get; set; } = string.Empty;

    /// <summary>ID of the sender (host ID or driver ID).</summary>
    [JsonPropertyName("senderId")]
    public string SenderId { get; set; } = string.Empty;

    /// <summary>Target recipient. "all" for broadcast, or a specific driver ID.</summary>
    [JsonPropertyName("targetId")]
    public string TargetId { get; set; } = "all";

    /// <summary>Message priority for display ordering.</summary>
    [JsonPropertyName("priority")]
    public MessagePriority Priority { get; set; } = MessagePriority.Normal;

    /// <summary>Monotonic sender sequence where ordering is important.</summary>
    [JsonPropertyName("sequence")]
    public long? Sequence { get; set; }

    /// <summary>JSON payload containing the actual data (flag info, penalty info, etc.)</summary>
    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; set; }

    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Serialize this message to a JSON string.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, s_options);

    /// <summary>Deserialize a JSON string to a ProtocolMessage.</summary>
    public static ProtocolMessage? FromJson(string json)
    {
        return TryFromJson(json, out var message) ? message : null;
    }

    public static bool TryFromJson(string json, out ProtocolMessage? message)
        => TryFromJsonCore(json, allowLegacyProtocol: false, out message);

    /// <summary>
    /// Parses messages from the previously deployed VRS Internet server. That
    /// server predates the protocolVersion/sessionId fields. A present but
    /// incompatible version is still rejected.
    /// </summary>
    public static bool TryFromLegacyCompatibleJson(
        string json,
        out ProtocolMessage? message)
        => TryFromJsonCore(json, allowLegacyProtocol: true, out message);

    public static ProtocolMessage? FromLegacyCompatibleJson(string json)
        => TryFromLegacyCompatibleJson(json, out var message) ? message : null;

    private static bool TryFromJsonCore(
        string json,
        bool allowLegacyProtocol,
        out ProtocolMessage? message)
    {
        message = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("id", out var idElement)
                || idElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(idElement.GetString())
                || idElement.GetString()!.Length > 128
                || !root.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String
                || !Enum.TryParse<MessageType>(typeElement.GetString(), true, out _))
            {
                return false;
            }

            var hasProtocolVersion = root.TryGetProperty(
                "protocolVersion",
                out var versionElement);
            if (hasProtocolVersion
                && (versionElement.ValueKind != JsonValueKind.String
                    || !string.Equals(
                        versionElement.GetString(),
                        CurrentProtocolVersion,
                        StringComparison.Ordinal)))
            {
                return false;
            }
            if (!hasProtocolVersion && !allowLegacyProtocol)
            {
                return false;
            }

            message = JsonSerializer.Deserialize<ProtocolMessage>(json, s_options);
            if (message != null && !hasProtocolVersion)
            {
                message.ProtocolVersion = "legacy";
                if (string.IsNullOrWhiteSpace(message.SessionId))
                {
                    message.SessionId = message.SessionCode;
                }
            }
            return message != null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Create a typed payload from a data object.</summary>
    public static ProtocolMessage Create<T>(MessageType type, T payload, MessagePriority priority = MessagePriority.Normal)
    {
        var json = JsonSerializer.Serialize(payload, s_options);
        return new ProtocolMessage
        {
            Type = type,
            Priority = priority,
            Payload = JsonSerializer.Deserialize<JsonElement>(json)
        };
    }

    /// <summary>Extract typed payload data.</summary>
    public T? GetPayload<T>()
    {
        if (Payload == null) return default;
        return JsonSerializer.Deserialize<T>(Payload.Value.GetRawText(), s_options);
    }
}
