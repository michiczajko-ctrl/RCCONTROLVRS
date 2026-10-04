using System.Net.WebSockets;

namespace VRS.RaceControl.Shared.Services;

/// <summary>Formats why a Relay connection ended so a disconnect can be matched with the Relay log. Never includes tokens.</summary>
public static class ConnectionDiagnostics
{
    private const int MaximumDescriptionLength = 160;

    public static string DescribeClose(WebSocketCloseStatus? status, string? description)
    {
        var code = status is { } value ? $"{value} ({(int)value})" : "no close status";
        if (string.IsNullOrWhiteSpace(description)) return code;
        var text = description.Trim();
        return $"{code}: {(text.Length > MaximumDescriptionLength ? text[..MaximumDescriptionLength] + "…" : text)}";
    }

    public static string Summarize(DateTime atUtc, string what) => $"{atUtc.ToUniversalTime():O} {what}";

    public static string DescribeJoinReject(string? code, bool? retryable, string? reason) =>
        $"join rejected code={code ?? "unknown"} retryable={(retryable?.ToString().ToLowerInvariant() ?? "unspecified")}"
        + (string.IsNullOrWhiteSpace(reason) ? "" : $": {reason}");
}
