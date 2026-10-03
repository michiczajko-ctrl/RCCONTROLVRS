namespace VRS.RaceControl.Shared.Services;

/// <summary>
/// Backstop that guarantees the process really ends: if it is still alive when the grace period
/// runs out, the process is terminated. On a normal exit the process is already gone and the timer
/// never fires, so this changes nothing for a clean shutdown. It exists because a still-running
/// process (a blocked <c>Closed</c>/<c>Dispose</c> handler, a stray foreground thread) keeps the
/// exe locked — which is exactly what breaks updating and reinstalling.
/// </summary>
public sealed class ExitWatchdog : IDisposable
{
    // The timer must stay reachable for the whole life of the process: an unreferenced
    // System.Threading.Timer is garbage collected and silently stops firing.
    private static ExitWatchdog? s_armed;

    private readonly Timer _timer;

    private ExitWatchdog(TimeSpan grace, Action forceExit)
    {
        _timer = new Timer(_ => forceExit(), null, grace, Timeout.InfiniteTimeSpan);
    }

    /// <param name="forceExit">Defaults to <see cref="Environment.Exit"/> with code 0; injectable for tests.</param>
    public static ExitWatchdog Arm(TimeSpan grace, Action? forceExit = null)
    {
        var watchdog = new ExitWatchdog(grace, forceExit ?? (() => Environment.Exit(0)));
        s_armed = watchdog;
        return watchdog;
    }

    public void Dispose()
    {
        _timer.Dispose();
        if (ReferenceEquals(s_armed, this)) s_armed = null;
    }
}
