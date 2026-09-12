using System.Text.Json.Serialization;
using VRS.RaceControl.Shared.Enums;

namespace VRS.RaceControl.Shared.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FastLaneState
{
    Closed = 0,
    Opening = 1,
    Open = 2
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PanelFlashMode
{
    Steady = 0,
    Flashing = 1
}

public sealed class TimedPanelTransitionPayload
{
    public string TransitionId { get; set; } = string.Empty;
    public FlagType SourceState { get; set; } = FlagType.Green;
    public FlagType TargetState { get; set; } = FlagType.FullCourseYellow;
    public DateTimeOffset CueStartsAtHostTime { get; set; }
    public DateTimeOffset CountdownStartsAtHostTime { get; set; }
    public DateTimeOffset TargetEffectiveAtHostTime { get; set; }
    public int CountdownSeconds { get; set; } = 5;

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(TransitionId) || TransitionId.Length > 128)
            return "Transition id is invalid.";
        if (!Enum.IsDefined(SourceState) || !Enum.IsDefined(TargetState))
            return "Transition flag state is invalid.";
        if (CountdownSeconds is < 1 or > 30)
            return "Countdown must be between 1 and 30 seconds.";
        if (CueStartsAtHostTime > CountdownStartsAtHostTime
            || CountdownStartsAtHostTime >= TargetEffectiveAtHostTime)
            return "Transition timestamps are out of order.";
        var expected = TimeSpan.FromSeconds(CountdownSeconds);
        if (Math.Abs((TargetEffectiveAtHostTime - CountdownStartsAtHostTime - expected).TotalMilliseconds) > 50)
            return "Transition duration does not match countdown seconds.";
        return null;
    }
}

public sealed class TimedFastLaneTransitionPayload
{
    public string TransitionId { get; set; } = string.Empty;
    public FastLaneState SourceState { get; set; } = FastLaneState.Closed;
    public FastLaneState TargetState { get; set; } = FastLaneState.Open;
    public DateTimeOffset StartsAtHostTime { get; set; }
    public DateTimeOffset TargetEffectiveAtHostTime { get; set; }
    public DateTimeOffset? DeactivateAtHostTime { get; set; }

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(TransitionId) || TransitionId.Length > 128)
            return "Fast Lane transition id is invalid.";
        if (!Enum.IsDefined(SourceState) || !Enum.IsDefined(TargetState))
            return "Fast Lane transition state is invalid.";
        if (StartsAtHostTime == default
            || TargetEffectiveAtHostTime <= StartsAtHostTime
            || TargetEffectiveAtHostTime - StartsAtHostTime > TimeSpan.FromSeconds(30))
            return "Fast Lane transition timestamps are out of order.";
        if (DeactivateAtHostTime is { } deactivateAt
            && (deactivateAt <= TargetEffectiveAtHostTime
                || deactivateAt - TargetEffectiveAtHostTime > TimeSpan.FromMinutes(5)))
            return "Fast Lane deactivation timestamp is invalid.";
        return null;
    }
}

/// <summary>
/// Recipient-specific projection of HOST-owned Race Flags plus the independent
/// Fast Lane state. This is a snapshot, not a second flag event stream.
/// </summary>
public sealed class RaceControlPanelStatePayload
{
    public string EpochId { get; set; } = string.Empty;
    public long Revision { get; set; }
    public FlagType FlagState { get; set; } = FlagType.None;
    public PanelFlashMode FlashMode { get; set; } = PanelFlashMode.Steady;
    public int FlashPeriodMs { get; set; } = 1000;
    public DateTimeOffset FlashEpochHostTime { get; set; }
    public bool FastLaneActive { get; set; }
    public FastLaneState FastLaneState { get; set; } = FastLaneState.Closed;
    public TimedFastLaneTransitionPayload? FastLaneTransition { get; set; }
    public DateTimeOffset HostNow { get; set; }
    public TimedPanelTransitionPayload? Transition { get; set; }

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(EpochId) || EpochId.Length > 128 || Revision < 0)
            return "Panel state epoch or revision is invalid.";
        if (!Enum.IsDefined(FlagState) || !Enum.IsDefined(FlashMode) || !Enum.IsDefined(FastLaneState))
            return "Panel state contains an unknown enum value.";
        if (FlashPeriodMs is < 250 or > 5000)
            return "Panel flash period is outside the allowed range.";
        if (HostNow == default || FlashEpochHostTime == default)
            return "Panel state contains an invalid HOST timestamp.";
        if (Transition != null
            && Math.Abs((Transition.TargetEffectiveAtHostTime - HostNow).TotalMinutes) > 5)
            return "Panel transition is outside the accepted HOST-time window.";
        if (FastLaneTransition != null
            && Math.Abs((FastLaneTransition.TargetEffectiveAtHostTime - HostNow).TotalMinutes) > 5)
            return "Fast Lane transition is outside the accepted HOST-time window.";
        return Transition?.Validate() ?? FastLaneTransition?.Validate();
    }
}

public sealed class TimeSyncRequestPayload
{
    public string RequestId { get; set; } = string.Empty;
}

public sealed class TimeSyncResponsePayload
{
    public string RequestId { get; set; } = string.Empty;
    public DateTimeOffset HostReceivedAt { get; set; }
    public DateTimeOffset HostSentAt { get; set; }
}

/// <summary>Pure HOST-time projection used by UI and tests.</summary>
public readonly record struct TrackPanelFrame(
    FlagType State,
    string? Text,
    bool FlashOn,
    DateTimeOffset NextChangeAtHostTime);

public readonly record struct FastLaneFrame(
    FastLaneState State,
    bool Visible,
    DateTimeOffset NextChangeAtHostTime);

public static class TrackPanelTimeline
{
    public static TrackPanelFrame Resolve(
        RaceControlPanelStatePayload snapshot,
        DateTimeOffset hostNow)
    {
        var transition = snapshot.Transition;
        if (transition != null && transition.Validate() == null)
        {
            if (hostNow < transition.CountdownStartsAtHostTime)
            {
                return new TrackPanelFrame(
                    transition.SourceState,
                    null,
                    true,
                    transition.CountdownStartsAtHostTime);
            }

            if (hostNow < transition.TargetEffectiveAtHostTime)
            {
                var elapsed = hostNow - transition.CountdownStartsAtHostTime;
                var elapsedWholeSeconds = Math.Clamp((int)Math.Floor(elapsed.TotalSeconds), 0, transition.CountdownSeconds - 1);
                var value = transition.CountdownSeconds - elapsedWholeSeconds;
                return new TrackPanelFrame(
                    transition.TargetState,
                    value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    true,
                    transition.CountdownStartsAtHostTime.AddSeconds(elapsedWholeSeconds + 1));
            }

            return new TrackPanelFrame(
                transition.TargetState,
                GetTerminalText(transition.TargetState),
                true,
                DateTimeOffset.MaxValue);
        }

        if (snapshot.FlashMode != PanelFlashMode.Flashing)
            return new TrackPanelFrame(snapshot.FlagState, null, true, DateTimeOffset.MaxValue);

        var periodMs = Math.Clamp(snapshot.FlashPeriodMs, 250, 5000);
        var halfMs = periodMs / 2d;
        var elapsedMs = Math.Max(0, (hostNow - snapshot.FlashEpochHostTime).TotalMilliseconds);
        var halfIndex = (long)Math.Floor(elapsedMs / halfMs);
        var next = snapshot.FlashEpochHostTime.AddMilliseconds((halfIndex + 1) * halfMs);
        return new TrackPanelFrame(snapshot.FlagState, null, halfIndex % 2 == 0, next);
    }

    public static string? GetTerminalText(FlagType state) => state switch
    {
        FlagType.FullCourseYellow => "FCY",
        FlagType.SafetyCar => "SC",
        FlagType.VirtualSafetyCar => "VSC",
        FlagType.ReadyForGreen => "RDY",
        _ => null
    };
}

public static class FastLaneTimeline
{
    public static FastLaneFrame Resolve(
        RaceControlPanelStatePayload snapshot,
        DateTimeOffset hostNow)
    {
        var transition = snapshot.FastLaneTransition;
        if (transition != null && transition.Validate() == null)
        {
            if (transition.DeactivateAtHostTime is { } deactivateAt
                && hostNow >= deactivateAt)
            {
                return new FastLaneFrame(
                    FastLaneState.Closed,
                    false,
                    DateTimeOffset.MaxValue);
            }

            if (hostNow < transition.StartsAtHostTime)
            {
                return new FastLaneFrame(
                    transition.SourceState,
                    true,
                    transition.StartsAtHostTime);
            }

            if (hostNow < transition.TargetEffectiveAtHostTime)
            {
                return new FastLaneFrame(
                    transition.TargetState == FastLaneState.Open
                        ? FastLaneState.Opening
                        : transition.TargetState,
                    true,
                    transition.TargetEffectiveAtHostTime);
            }

            return new FastLaneFrame(
                transition.TargetState,
                true,
                transition.DeactivateAtHostTime ?? DateTimeOffset.MaxValue);
        }

        return new FastLaneFrame(
            snapshot.FastLaneState,
            snapshot.FastLaneActive,
            DateTimeOffset.MaxValue);
    }
}
