namespace VRS.RaceControl.Shared.AIEngineer.Services;

public sealed class EngineerLocalizationService
{
    public string FormatLiters(double value)
    {
        var rounded = Math.Round(value, 1);
        return $"approximately {rounded:0.0} litres";
    }

    public string FormatLaps(double value)
    {
        var rounded = Math.Round(value, 1);
        return $"approximately {rounded:0.0} laps";
    }

    public string FormatLapTime(int? milliseconds)
    {
        if (!milliseconds.HasValue || milliseconds.Value <= 0)
        {
            return "--";
        }

        var time = TimeSpan.FromMilliseconds(milliseconds.Value);
        return $"{(int)time.TotalMinutes}:{time.Seconds:00}.{time.Milliseconds:000}";
    }

    public string FuelRange(double laps) =>
        $"You have fuel for {FormatLaps(laps)}.";

    public string FuelDeficit(double deficitLiters) =>
        $"At the current consumption, you will be short by {FormatLiters(deficitLiters)}.";

    public string FuelEnough(double reserveLaps) =>
        $"Fuel should be enough to finish with a reserve of {FormatLaps(reserveLaps)}.";

    public string LastLap(int lapMs)
    {
        var time = FormatLapTime(lapMs);
        return $"Last lap: {time}.";
    }

    public string BestLap() => "That is your best lap.";
}
