using System.Text.Json;
using System.Text.Json.Nodes;
using VRS.RaceControl.Shared.Protocol;

namespace VRS.RaceControl.Shared.Services;

public static class AuthorityIntentKey
{
    public static string Create(string? sessionId, string kind, string stableTarget, JsonElement payload)
    {
        var node = JsonNode.Parse(payload.GetRawText());
        if (kind == nameof(MessageType.Penalty) && node is JsonObject penalty)
        {
            penalty.Remove("id"); penalty.Remove("createdAtUtc"); penalty.Remove("accountId");
            penalty.Remove("driverName"); penalty.Remove("sessionCode");
            // Only the retry key omits refreshed review revision. The original command still carries its CAS revision.
            if (penalty["incidentCommit"] is JsonObject incident) incident.Remove("expectedRevision");
        }
        return $"{sessionId}:{kind}:{stableTarget}:{node?.ToJsonString()}";
    }
}
