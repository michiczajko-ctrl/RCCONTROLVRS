using System.Text.Json.Serialization;

namespace VRS.RaceControl.Shared.Models;

public sealed class IncidentReport
{
    public string? IssuedPenaltyId { get; set; }
    [JsonPropertyName("sequenceNumber")]
    public int SequenceNumber { get; set; }

    [JsonIgnore]
    public string DisplayNumber => SequenceNumber > 0 ? SequenceNumber.ToString("000") : "---";

    [JsonIgnore]
    public string RaceClassDisplay
    {
        get
        {
            var classes = IncidentParticipants.Select(item => IncidentRaceClass.Normalize(item.RaceClass))
                .Where(value => value != null).Distinct(StringComparer.Ordinal).ToArray();
            return classes.Length == 0 ? "Unknown" : string.Join(" ↔ ", classes);
        }
    }

    [JsonIgnore]
    public string SeverityExplanation => Evidence.FirstOrDefault(item =>
        item.Kind == "SeverityReason")?.Detail ?? "Impact magnitude only";

    [JsonIgnore]
    public string ConfidenceExplanation => Evidence.FirstOrDefault(item =>
        item.Kind == "ConfidenceReason")?.Detail ?? "Evidence details unavailable";

    [JsonIgnore]
    public bool IsCrossClass => IncidentParticipants.Select(item =>
            IncidentRaceClass.Normalize(item.RaceClass))
        .Where(value => value != null).Distinct(StringComparer.Ordinal).Skip(1).Any();

    [JsonPropertyName("caseStatus")]
    public IncidentCaseStatus CaseStatus { get; set; } = IncidentCaseStatus.New;

    [JsonPropertyName("workflowVersion")]
    public int WorkflowVersion { get; set; }

    [JsonPropertyName("decision")]
    public IncidentDecision Decision { get; set; } = IncidentDecision.Pending;
    [JsonPropertyName("source")]
    public IncidentSource Source { get; set; } = IncidentSource.DriverReport;

    [JsonPropertyName("severity")]
    public IncidentSeverity Severity { get; set; } = IncidentSeverity.Medium;

    [JsonIgnore]
    public string SourceDisplay => Source switch
    {
        IncidentSource.Auto => "AUTO",
        IncidentSource.DriverReport => "DRIVER",
        _ => "AUTO + DRIVER"
    };

    [JsonIgnore]
    public string CaseStatusDisplay => CaseStatus switch
    {
        IncidentCaseStatus.New => "NEW",
        IncidentCaseStatus.Reviewing => "REVIEW",
        _ => "CLOSED"
    };

    [JsonIgnore]
    public string SeverityDisplay => Severity switch
    {
        IncidentSeverity.Minor => "LOW",
        IncidentSeverity.Medium => "MEDIUM",
        IncidentSeverity.Heavy => "HIGH",
        _ => "CRITICAL"
    };

    [JsonPropertyName("confidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IncidentConfidence? Confidence { get; set; }

    [JsonPropertyName("correlationKey")]
    public string CorrelationKey { get; set; } = string.Empty;
    [JsonPropertyName("telemetryRule")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TelemetryRuleReport? TelemetryRule { get; set; }
    public TelemetryObservation? TelemetryObservation { get; set; }
    [JsonPropertyName("telemetryEvidenceId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TelemetryEvidenceId { get; set; }

    [JsonPropertyName("trackPositionNormalized")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TrackPositionNormalized { get; set; }

    [JsonPropertyName("worldPosition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IncidentVector3? WorldPosition { get; set; }

    [JsonPropertyName("impactPosition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IncidentVector3? ImpactPosition { get; set; }

    [JsonPropertyName("relativeSpeedKmh")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? RelativeSpeedKmh { get; set; }

    [JsonPropertyName("impactMagnitude")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ImpactMagnitude { get; set; }

    [JsonPropertyName("detectedAtUtc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? DetectedAtUtc { get; set; }

    [JsonPropertyName("incidentParticipants")]
    public List<IncidentParticipant> IncidentParticipants { get; set; } = new();

    [JsonPropertyName("reporters")]
    public List<IncidentReporter> Reporters { get; set; } = new();

    /// <summary>Original report IDs retained when several reports become one case.</summary>
    [JsonPropertyName("sourceReportIds")]
    public List<string> SourceReportIds { get; set; } = new();

    [JsonPropertyName("evidence")]
    public List<IncidentEvidence> Evidence { get; set; } = new();

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

    [JsonPropertyName("sector")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Sector { get; set; }

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
        if (report.IncidentParticipants == null || report.Reporters == null || report.Evidence == null
            || report.History == null || report.SourceReportIds == null)
            return "Invalid incident collection.";
        if (string.IsNullOrWhiteSpace(report.SessionId))
            return "Zgłoszenie wymaga aktywnej sesji.";
        if (report.Source != IncidentSource.Auto && string.IsNullOrWhiteSpace(report.ReporterId))
            return "Brak identyfikatora zgłaszającego.";
        if (string.IsNullOrWhiteSpace(report.ReportedDriver)
            && string.IsNullOrWhiteSpace(report.ReportedCarNumber)
            && report.IncidentParticipants.Count == 0)
            return "Podaj kierowcę lub numer zgłaszanego samochodu.";
        if (string.IsNullOrWhiteSpace(report.Description))
            return "Opis incydentu jest wymagany.";
        if (report.Description.Trim().Length > 600)
            return "Opis incydentu może mieć maksymalnie 600 znaków.";
        if ((report.TrackSection?.Length ?? 0) > 80 || (report.Participants?.Length ?? 0) > 160)
            return "Jedno z pól formularza jest zbyt długie.";
        if (report.Lap < 0)
            return "Numer okrążenia nie może być ujemny.";
        return null;
    }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IncidentCaseStatus
{
    New,
    Reviewing,
    Closed
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IncidentDecision
{
    Pending,
    NoFurtherAction,
    Warning,
    Penalty
}

public sealed class IncidentHistoryEntry
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string Actor { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public IncidentStatus? PreviousStatus { get; set; }
    public IncidentStatus? NewStatus { get; set; }
    public IncidentCaseStatus? PreviousCaseStatus { get; set; }
    public IncidentCaseStatus? NewCaseStatus { get; set; }
    public IncidentDecision? PreviousDecision { get; set; }
    public IncidentDecision? NewDecision { get; set; }
    public string? PreviousAssignedSteward { get; set; }
    public string? NewAssignedSteward { get; set; }
    public string Note { get; set; } = string.Empty;
}

public sealed class IncidentVector3
{
    public IncidentVector3() { }
    public IncidentVector3(double x, double y, double z) => (X, Y, Z) = (x, y, z);
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }

    public double DistanceTo(IncidentVector3 other)
    {
        var x = X - other.X;
        var y = Y - other.Y;
        var z = Z - other.Z;
        return Math.Sqrt(x * x + y * y + z * z);
    }
}

public sealed class IncidentParticipant
{
    public int VehicleId { get; set; }
    public string DriverName { get; set; } = string.Empty;
    public string? CarNumber { get; set; }
    public string? RaceClass { get; set; }
    public IncidentVector3? Position { get; set; }
    public IncidentVector3? Velocity { get; set; }
    public double? SpeedKmh { get; set; }
}

public sealed class IncidentReporter
{
    public string ReporterId { get; set; } = string.Empty;
    public string ReporterName { get; set; } = string.Empty;
    public DateTime ReportedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class IncidentEvidence
{
    public string Kind { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
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
    Other,
    CarToCarContact,
    PossibleContact,
    HeavyImpact,
    PossibleBarrierImpact,
    UnknownImpact
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IncidentSource
{
    DriverReport,
    Auto,
    AutoAndDriverReport
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IncidentSeverity
{
    Minor,
    Medium,
    Heavy,
    Severe
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IncidentConfidence
{
    Low,
    Medium,
    High
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
