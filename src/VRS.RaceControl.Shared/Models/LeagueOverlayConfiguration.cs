namespace VRS.RaceControl.Shared.Models;

/// <summary>
/// League-controlled overlay defaults. Monitor selection and click-through
/// remain device-local safety settings in the Driver Client.
/// </summary>
public sealed class LeagueOverlayConfiguration
{
    public string BannerPosition { get; set; } = "TopCenter";
    public string FlagPosition { get; set; } = "TopRight";
    public string PenaltyPosition { get; set; } = "TopCenter";
    public string RaceControlPosition { get; set; } = "TopCenter";
    public string NeutralizationPosition { get; set; } = "TopCenter";
    public string BottomBarPosition { get; set; } = "BottomCenter";
    public double SafeAreaMargin { get; set; } = 24;
    public double BannerScale { get; set; } = 1;
    public double FlagScale { get; set; } = 1;
    public double BottomBarScale { get; set; } = 1;
    public double BottomBarWidth { get; set; } = 820;
    public double BannerWidth { get; set; } = 820;
    public double BannerHeight { get; set; } = 82;
    public double Opacity { get; set; } = 1;
    public int DefaultDisplayDurationMs { get; set; } = 8000;
    public string EnterAnimation { get; set; } = "Slide";
    public string ExitAnimation { get; set; } = "Fade";
    public int BannerLayer { get; set; } = 20;
    public int FlagLayer { get; set; } = 30;
    public int BottomBarLayer { get; set; } = 10;

    public string? Validate()
    {
        var positions = new[]
        {
            BannerPosition, FlagPosition, PenaltyPosition,
            RaceControlPosition, NeutralizationPosition, BottomBarPosition
        };
        if (positions.Any(position => !ValidPositions.Contains(position)))
            return "Pozycja elementu overlayu jest nieprawidłowa.";
        if (SafeAreaMargin is < 0 or > 300
            || BannerScale is < 0.5 or > 2
            || FlagScale is < 0.5 or > 2
            || BottomBarScale is < 0.5 or > 2
            || BottomBarWidth is < 320 or > 1600
            || BannerWidth is < 480 or > 1400
            || BannerHeight is < 48 or > 180
            || Opacity is < 0.3 or > 1
            || DefaultDisplayDurationMs is < 1000 or > 120_000
            || BannerLayer is < 0 or > 100
            || FlagLayer is < 0 or > 100
            || BottomBarLayer is < 0 or > 100)
            return "Wymiary lub czas overlayu są poza dozwolonym zakresem.";
        if (!ValidAnimations.Contains(EnterAnimation)
            || !ValidAnimations.Contains(ExitAnimation))
            return "Animacja overlayu jest nieprawidłowa.";
        return null;
    }

    private static readonly HashSet<string> ValidPositions = new(
        new[]
        {
            "TopLeft", "TopCenter", "TopRight",
            "BottomLeft", "BottomCenter", "BottomRight"
        },
        StringComparer.Ordinal);

    private static readonly HashSet<string> ValidAnimations = new(
        new[] { "None", "Fade", "Slide", "Scale" },
        StringComparer.Ordinal);
}
