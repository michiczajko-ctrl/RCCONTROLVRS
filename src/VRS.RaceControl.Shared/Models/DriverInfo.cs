using System.ComponentModel;
using System.Text.Json.Serialization;
using VRS.RaceControl.Shared.Diagnostics;

namespace VRS.RaceControl.Shared.Models;

/// <summary>
/// Information about a connected driver.
/// </summary>
public class DriverInfo : INotifyPropertyChanged
{
    private string? _appVersion;

    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("connectedAt")]
    public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("isConnected")]
    public bool IsConnected { get; set; } = true;

    /// <summary>
    /// App version the driver reported, if any. Populated by a post-connect announce message,
    /// not the Join handshake itself — the cloud relay does not reliably forward new Join
    /// payload fields to the HOST, so this travels as a normal application message instead.
    /// </summary>
    [JsonPropertyName("appVersion")]
    public string? AppVersion
    {
        get => _appVersion;
        set
        {
            if (_appVersion == value)
            {
                return;
            }
            _appVersion = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AppVersion)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasVersionMismatch)));
        }
    }

    [JsonIgnore]
    public bool HasVersionMismatch =>
        !string.IsNullOrEmpty(AppVersion)
        && !string.Equals(AppVersion, BuildInfo.DisplayVersion, StringComparison.OrdinalIgnoreCase);

    public event PropertyChangedEventHandler? PropertyChanged;
}
