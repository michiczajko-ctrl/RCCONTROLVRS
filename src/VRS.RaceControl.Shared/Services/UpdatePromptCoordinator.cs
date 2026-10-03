using System;
using System.Threading;
using System.Threading.Tasks;

namespace VRS.RaceControl.Shared.Services;

public enum UpdatePromptOutcome
{
    /// <summary>No update was found (or none has been checked yet) — nothing was shown.</summary>
    NoUpdate,
    /// <summary>A newer version exists but its release has no installable package yet.</summary>
    NoInstallPackage,
    /// <summary>The user answered "No" — the app stays on the current version.</summary>
    Declined,
    /// <summary>The user answered "Yes" and the install failed; <see cref="UpdatePromptResult.ErrorMessage"/> says why.</summary>
    InstallFailed,
    /// <summary>The user answered "Yes" and the installer reported success (in production the process exits first).</summary>
    InstallStarted,
    /// <summary>A previous prompt is still open or installing (double click on the badge).</summary>
    AlreadyRunning,
}

public sealed record UpdatePromptResult(UpdatePromptOutcome Outcome, string ErrorMessage = "");

/// <summary>
/// The decision logic behind the "Do you want to update?" dialog, kept free of any UI so it can be
/// unit-tested without launching the real installer (which spawns a script and terminates the
/// process). The window supplies <c>confirm</c> (shows the Yes/No dialog) and <c>install</c> (shows
/// progress and calls <see cref="AutoUpdaterService.DownloadAndInstallUpdateAsync"/>).
/// </summary>
public sealed class UpdatePromptCoordinator
{
    private readonly Func<UpdateCheckResult, bool> _confirm;
    private readonly Func<string, Task<UpdateInstallResult>> _install;
    private int _running;

    public UpdatePromptCoordinator(
        Func<UpdateCheckResult, bool> confirm,
        Func<string, Task<UpdateInstallResult>> install)
    {
        ArgumentNullException.ThrowIfNull(confirm);
        ArgumentNullException.ThrowIfNull(install);
        _confirm = confirm;
        _install = install;
    }

    public async Task<UpdatePromptResult> RunAsync(UpdateCheckResult? update)
    {
        if (update is null || !update.IsUpdateAvailable)
        {
            return new UpdatePromptResult(UpdatePromptOutcome.NoUpdate);
        }

        if (string.IsNullOrWhiteSpace(update.DownloadUrl))
        {
            return new UpdatePromptResult(UpdatePromptOutcome.NoInstallPackage);
        }

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return new UpdatePromptResult(UpdatePromptOutcome.AlreadyRunning);
        }

        try
        {
            if (!_confirm(update))
            {
                return new UpdatePromptResult(UpdatePromptOutcome.Declined);
            }

            var result = await _install(update.DownloadUrl).ConfigureAwait(true);
            return result.Success
                ? new UpdatePromptResult(UpdatePromptOutcome.InstallStarted)
                : new UpdatePromptResult(UpdatePromptOutcome.InstallFailed, result.ErrorMessage);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}
