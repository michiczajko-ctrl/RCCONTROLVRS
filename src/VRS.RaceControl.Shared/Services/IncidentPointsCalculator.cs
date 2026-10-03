using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

public static class IncidentPointsCalculator
{
    private const int MinorPoints = 1;
    private const int MediumPoints = 2;
    private const int HeavyPoints = 4;
    private const int SeverePoints = 6;

    public static int Suggest(IncidentSeverity severity) => severity switch
    {
        IncidentSeverity.Minor => MinorPoints,
        IncidentSeverity.Medium => MediumPoints,
        IncidentSeverity.Heavy => HeavyPoints,
        IncidentSeverity.Severe => SeverePoints,
        _ => MinorPoints
    };
}
