using System.Text.Json.Serialization;

namespace VRS.RaceControl.Shared.Models;

/// <summary>
/// Session metadata shared between host and clients.
/// </summary>
public class SessionInfo
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
