using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

public sealed record RaceGuardCaseRow(string Id, string Number, int? Lap, string Location, string Drivers,
    string Type, string Severity, string Status, IncidentDecision Decision, string Steward, TimeSpan Age, bool AwaitingDecision);

public sealed record RaceGuardDriverRow(string Driver, int Incidents, int Warnings, int Penalties, int Open);

public sealed record RaceGuardSummary(int Open, int AwaitingDecision, int Warnings, int Penalties, int Closed,
    IReadOnlyList<RaceGuardCaseRow> Cases, IReadOnlyList<RaceGuardDriverRow> Drivers);

/// <summary>Aggregates the incident queue for the dashboard "RaceGuard+" widget (pure, no UI).</summary>
public static class RaceGuardSummaryBuilder
{
    public static RaceGuardSummary Build(IEnumerable<IncidentReport> incidents, DateTime nowUtc)
    {
        var list = incidents.ToList();
        var cases = list.Select(report => new RaceGuardCaseRow(
                report.Id, report.DisplayNumber, report.Lap, Location(report), Drivers(report),
                report.IncidentType.ToString(), report.SeverityDisplay, report.CaseStatusDisplay, report.Decision,
                report.AssignedSteward, nowUtc > report.CreatedAtUtc ? nowUtc - report.CreatedAtUtc : TimeSpan.Zero,
                report.CaseStatus != IncidentCaseStatus.Closed && report.Decision == IncidentDecision.Pending))
            .OrderByDescending(row => row.AwaitingDecision)
            .ThenByDescending(row => row.AwaitingDecision ? row.Age : TimeSpan.Zero)
            .ThenBy(row => row.Number, StringComparer.Ordinal).ToArray();

        var drivers = list.SelectMany(report => DriverNames(report).Select(name => (name, report)))
            .GroupBy(item => item.name, StringComparer.OrdinalIgnoreCase)
            .Select(group => new RaceGuardDriverRow(group.Key, group.Count(),
                group.Count(item => item.report.Decision == IncidentDecision.Warning),
                group.Count(item => item.report.Decision == IncidentDecision.Penalty),
                group.Count(item => item.report.CaseStatus != IncidentCaseStatus.Closed)))
            .OrderByDescending(row => row.Penalties).ThenByDescending(row => row.Warnings)
            .ThenByDescending(row => row.Incidents).ThenBy(row => row.Driver, StringComparer.OrdinalIgnoreCase).ToArray();

        return new RaceGuardSummary(
            list.Count(report => report.CaseStatus != IncidentCaseStatus.Closed),
            cases.Count(row => row.AwaitingDecision),
            list.Count(report => report.Decision == IncidentDecision.Warning),
            list.Count(report => report.Decision == IncidentDecision.Penalty),
            list.Count(report => report.CaseStatus == IncidentCaseStatus.Closed),
            cases, drivers);
    }

    private static string Location(IncidentReport report) => !string.IsNullOrWhiteSpace(report.TrackSection)
        ? report.TrackSection
        : report.TrackPositionNormalized is { } position ? position.ToString("P0") : string.Empty;

    private static List<string> DriverNames(IncidentReport report)
    {
        if (!string.IsNullOrWhiteSpace(report.ReportedDriver))
        {
            return new List<string> { report.ReportedDriver.Trim() };
        }

        return report.IncidentParticipants.Select(item => item.DriverName?.Trim() ?? string.Empty)
            .Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Drivers(IncidentReport report)
    {
        var names = DriverNames(report);
        foreach (var participant in report.IncidentParticipants)
        {
            var name = participant.DriverName?.Trim();
            if (!string.IsNullOrEmpty(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }

        return string.Join(" / ", names);
    }
}
