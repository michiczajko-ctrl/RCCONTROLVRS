using System.Text;
using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Protocol;

/// <summary>
/// Carries BETA message types through the previously deployed VRS server,
/// whose enum only contains the original protocol message types. The server
/// sees a regular TextMessage and only current HOST/CLIENT applications decode
/// the inner, fully versioned envelope.
/// </summary>
public static class LegacyProtocolTunnel
{
    private const string Prefix = "__VRS_BETA_TUNNEL_V1__:";
    private const int MaximumEncodedLength = 900_000;

    private static readonly HashSet<MessageType> LegacyMessageTypes = new()
    {
        MessageType.Join,
        MessageType.JoinRequest,
        MessageType.JoinAck,
        MessageType.Flag,
        MessageType.Penalty,
        MessageType.TextMessage,
        MessageType.Telemetry,
        MessageType.Ack,
        MessageType.Heartbeat,
        MessageType.DriverList,
        MessageType.Disconnect,
        MessageType.JoinReject
    };

    public static ProtocolMessage WrapIfRequired(
        ProtocolMessage message,
        bool legacyServer)
    {
        if (!legacyServer || LegacyMessageTypes.Contains(message.Type))
        {
            return message;
        }

        var encoded = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(message.ToJson()));
        if (encoded.Length > MaximumEncodedLength)
        {
            throw new InvalidDataException(
                "Wiadomość jest zbyt duża dla zgodności ze starszym serwerem.");
        }

        var outer = ProtocolMessage.Create(
            MessageType.TextMessage,
            new TextMessagePayload
            {
                Text = Prefix + encoded,
                DisplayDurationMs = 0
            },
            message.Priority);
        outer.SessionId = message.SessionId;
        outer.SessionCode = message.SessionCode;
        outer.SenderId = message.SenderId;
        outer.TargetId = message.TargetId;
        outer.Sequence = message.Sequence;
        return outer;
    }

    public static bool IsTunnelEnvelope(ProtocolMessage message)
    {
        if (message.Type != MessageType.TextMessage)
        {
            return false;
        }

        var payload = message.GetPayload<TextMessagePayload>();
        return payload?.Text.StartsWith(Prefix, StringComparison.Ordinal) == true;
    }

    public static bool TryUnwrap(
        ProtocolMessage outer,
        out ProtocolMessage? inner)
    {
        inner = null;
        if (!IsTunnelEnvelope(outer))
        {
            return false;
        }

        try
        {
            var payload = outer.GetPayload<TextMessagePayload>();
            var encoded = payload!.Text[Prefix.Length..];
            if (encoded.Length == 0 || encoded.Length > MaximumEncodedLength)
            {
                return false;
            }

            var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            if (!ProtocolMessage.TryFromJson(json, out inner) || inner == null)
            {
                return false;
            }

            // Routing metadata from the outer envelope is authoritative. This
            // also prevents a tunneled payload from impersonating another
            // sender after it has passed through the old server.
            if (!string.IsNullOrWhiteSpace(outer.SenderId))
            {
                inner.SenderId = outer.SenderId;
            }
            if (!string.IsNullOrWhiteSpace(outer.SessionCode))
            {
                inner.SessionCode = outer.SessionCode;
            }
            if (!string.IsNullOrWhiteSpace(outer.SessionId))
            {
                inner.SessionId = outer.SessionId;
            }
            if (!string.IsNullOrWhiteSpace(outer.TargetId))
            {
                inner.TargetId = outer.TargetId;
            }
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
