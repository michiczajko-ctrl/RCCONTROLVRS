using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

public static class ImpactMessageFormatter
{
    public static string Format(IncidentReport report)
    {
        var verb = report.IncidentType switch
        {
            IncidentType.CarToCarContact => "Collision",
            IncidentType.PossibleContact => "Possible collision",
            IncidentType.HeavyImpact => "Heavy impact",
            IncidentType.PossibleBarrierImpact => "Barrier impact",
            IncidentType.UnknownImpact => "Impact",
            IncidentType.Contact => "Collision",
            _ => "Incident"
        };

        var driver = string.IsNullOrWhiteSpace(report.ReportedDriver) ? "Unknown driver" : report.ReportedDriver;
        var carNumber = string.IsNullOrWhiteSpace(report.ReportedCarNumber) ? string.Empty : $" #{report.ReportedCarNumber}";

        var locationParts = new List<string>();
        if (report.Sector is > 0)
        {
            locationParts.Add($"sector {report.Sector}");
        }
        if (report.Lap is > 0)
        {
            locationParts.Add($"lap {report.Lap}");
        }
        var location = locationParts.Count > 0 ? $", {string.Join(' ', locationParts)}" : string.Empty;

        return $"{verb} {driver}{carNumber}{location}";
    }
}
