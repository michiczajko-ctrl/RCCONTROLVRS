using System.Globalization;
using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

/// <summary>
/// Explains, in plain words, why the Relay did or did not mark a fleet sample as verified. The Relay's rule is: the
/// publishing HOST was clock-synchronised for this session epoch, its clock uncertainty is at most 50 ms, and the sample
/// reached the Relay no more than 0.75 s after capture. Rule detection, evidence and track recording all require it.
/// </summary>
public static class FleetVerification
{
    public const double MaximumUncertaintyMs = 50;
    public const double MaximumAgeSeconds = .75;

    public static string Describe(TelemetryBatch fleet, string? currentClockEpoch)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(fleet.ClockEpoch))
            problems.Add("the sample was published before this HOST's clock was synchronised with the Relay");
        else if (currentClockEpoch != null && fleet.ClockEpoch != currentClockEpoch)
            problems.Add("the sample belongs to an earlier Relay clock epoch");
        if (fleet.ClockUncertaintyMs is not { } uncertainty || !double.IsFinite(uncertainty))
            problems.Add("the sample carries no clock uncertainty");
        else if (uncertainty > MaximumUncertaintyMs)
            problems.Add(string.Create(CultureInfo.InvariantCulture,
                $"clock uncertainty {uncertainty:0} ms is above {MaximumUncertaintyMs:0} ms (round trip to the Relay above about {MaximumUncertaintyMs * 2:0} ms)"));
        string? age = null;
        if (fleet.ReceivedAt is { } received)
        {
            var seconds = (received - fleet.CapturedAt).TotalSeconds;
            age = string.Create(CultureInfo.InvariantCulture, $"capture-to-Relay age {seconds * 1000:0} ms");
            if (seconds is < -.1 or > MaximumAgeSeconds)
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"capture-to-Relay age {seconds * 1000:0} ms is outside -100 to {MaximumAgeSeconds * 1000:0} ms"));
        }
        if (fleet.FreshnessVerified)
            return string.Create(CultureInfo.InvariantCulture,
                $"verified (clock uncertainty {fleet.ClockUncertaintyMs ?? double.NaN:0} ms{(age == null ? "" : ", " + age)})");
        if (problems.Count == 0)
            problems.Add("the Relay holds no recent clock report from this station that is within 50 ms");
        return "NOT verified: " + string.Join("; ", problems);
    }
}
