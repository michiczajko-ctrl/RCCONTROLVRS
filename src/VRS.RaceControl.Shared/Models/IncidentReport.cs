using System.Text.Json.Serialization;

namespace VRS.RaceControl.Shared.Models;

public sealed class IncidentReport
{
    public string ReporterAccountKey { get; set; } = string.Empty;
    public List<IncidentDriverReply> DriverReplies { get; set; } = new();
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IncidentDriverReply? DriverReply { get; set; }
    [JsonIgnore] public string LatestDriverResponse => DriverReplies.LastOrDefault()?.Text ?? string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("reporterId")]
    public string ReporterId { get; set; } = string.Empty;

    [JsonPropertyName("reporterName")]
    public string ReporterName { get; set; } = string.Empty;

    [JsonPropertyName("reportedDriver")]
    public string ReportedDriver { get; set; } = string.Empty;

    [JsonPropertyName("reportedCarNumber")]
    public string ReportedCarNumber { get; set; } = string.Empty;

    [JsonPropertyName("lap")]
    public int? Lap { get; set; }

    [JsonPropertyName("sessionTimeSeconds")]
    public double? SessionTimeSeconds { get; set; }

    [JsonPropertyName("trackSection")]
    public string TrackSection { get; set; } = string.Empty;

    [JsonPropertyName("incidentType")]
    public IncidentType IncidentType { get; set; } = IncidentType.Other;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("priority")]
    public IncidentPriority Priority { get; set; } = IncidentPriority.Normal;

    [JsonPropertyName("participants")]
    public string Participants { get; set; } = string.Empty;

    [JsonPropertyName("replayTimestamp")]
    public string ReplayTimestamp { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public IncidentStatus Status { get; set; } = IncidentStatus.New;

    [JsonPropertyName("assignedSteward")]
    public string AssignedSteward { get; set; } = string.Empty;

    [JsonPropertyName("stewardNote")]
    public string StewardNote { get; set; } = string.Empty;

    [JsonPropertyName("responseToDriver")]
    public string ResponseToDriver { get; set; } = string.Empty;

    [JsonPropertyName("createdAtUtc")]
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("updatedAtUtc")]
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("history")]
    public List<IncidentHistoryEntry> History { get; set; } = new();

    public static string? Validate(IncidentReport report)
    {
        if (string.IsNullOrWhiteSpace(report.SessionId))
            return "Zgłoszenie wymaga aktywnej sesji.";
        if (string.IsNullOrWhiteSpace(report.ReporterId))
            return "Brak identyfikatora zgłaszającego.";
        if (string.IsNullOrWhiteSpace(report.ReportedDriver)
            && string.IsNullOrWhiteSpace(report.ReportedCarNumber))
            return "Podaj kierowcę lub numer zgłaszanego samochodu.";
        if (string.IsNullOrWhiteSpace(report.Description))
            return "Opis incydentu jest wymagany.";
        if (report.Description.Trim().Length > 600)
            return "Opis incydentu może mieć maksymalnie 600 znaków.";
        if (report.TrackSection.Length > 80 || report.Participants.Length > 160)
            return "Jedno z pól formularza jest zbyt długie.";
        if (report.Lap < 0)
            return "Numer okrążenia nie może być ujemny.";
        return null;
    }
}

public sealed class IncidentHistoryEntry
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string Actor { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
}

public sealed class IncidentDriverReply
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class IncidentReportAckPayload
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ReplyId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? StewardNote { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? AssignedSteward { get; set; }
    [JsonPropertyName("reportId")]
    public string ReportId { get; set; } = string.Empty;

    [JsonPropertyName("accepted")]
    public bool Accepted { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public IncidentStatus Status { get; set; } = IncidentStatus.New;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IncidentType
{
    Contact,
    UnsafeRejoin,
    ForcingOffTrack,
    Blocking,
    DangerousDriving,
    TrackLimits,
    PitLaneInfringement,
    SpeedingUnderNeutralization,
    IgnoringFlags,
    Other
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IncidentPriority
{
    Low,
    Normal,
    High,
    Urgent
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IncidentStatus
{
    New,
    UnderReview,
    Investigating,
    NoFurtherAction,
    Warning,
    PenaltyIssued,
    Dismissed,
    Closed
}
