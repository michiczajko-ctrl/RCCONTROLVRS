using System.Text.Json.Serialization;

namespace VRS.RaceControl.Shared.Models;

public sealed class DriverInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("connectedAt")]
    public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("isConnected")]
    public bool IsConnected { get; set; } = true;

    [JsonPropertyName("appVersion")]
    public string? AppVersion { get; set; }
}

public sealed class SessionInfo
{
    [JsonPropertyName("sessionCode")]
    public string SessionCode { get; set; } = string.Empty;

    [JsonPropertyName("hostName")]
    public string HostName { get; set; } = "Race Control";

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; } = true;
}

public sealed class JoinPayload
{
    [JsonPropertyName("driverName")]
    public string DriverName { get; set; } = string.Empty;

    [JsonPropertyName("sessionCode")]
    public string SessionCode { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; set; } = "driver";
}

public sealed class JoinAckPayload
{
    [JsonPropertyName("driverId")]
    public string DriverId { get; set; } = string.Empty;

    [JsonPropertyName("sessionInfo")]
    public SessionInfo? SessionInfo { get; set; }
}

public sealed class JoinRejectPayload
{
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}

public sealed class DriverListPayload
{
    [JsonPropertyName("drivers")]
    public List<DriverInfo> Drivers { get; set; } = new();
}
