using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using VRS.RaceControl.Shared.Diagnostics;

namespace VRS.RaceControl.Shared.Services;

public record UpdateCheckResult(
    bool IsUpdateAvailable,
    string CurrentVersion,
    string LatestVersion,
    string ReleaseUrl,
    string DownloadUrl,
    string ReleaseNotes,
    string ErrorMessage
);

/// <summary>
/// Result of <see cref="AutoUpdaterService.DownloadAndInstallUpdateAsync"/>. On success the
/// caller never actually observes this — the method calls <c>Environment.Exit(0)</c> before
/// returning — so in practice every value a caller sees has <c>Success == false</c> with a
/// real, previously-swallowed exception message instead of a single canned UI string.
/// </summary>
public record UpdateInstallResult(bool Success, string ErrorMessage);

public static class AutoUpdaterService
{
    public static string GitHubRepo = "michiczajko-ctrl/RCET";
    private static string GitHubReleasesUrl => $"https://api.github.com/repos/{GitHubRepo}/releases/latest";
    private static readonly HttpClient s_httpClient = new();

    static AutoUpdaterService()
    {
        s_httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"VRSRaceControlApp/{BuildInfo.InformationalVersion}");
        s_httpClient.Timeout = TimeSpan.FromSeconds(10);
    }

    /// <summary>
    /// Checks the latest GitHub release. <paramref name="appAssetHint"/> (e.g. "Host"/"Client")
    /// lets a release that bundles both apps' packages steer each app toward its own asset —
    /// Only a matching component asset is installable; EXE is preferred over ZIP.
    /// <paramref name="currentVersionOverride"/> lets a caller report its own version instead
    /// of the shared <see cref="BuildInfo.DisplayVersion"/> constant — needed because that
    /// constant lives in this Shared assembly and can't differ between Host and Client on its
    /// own. Defaults to null (unchanged behavior: falls back to BuildInfo.DisplayVersion).
    /// </summary>
    public static async Task<UpdateCheckResult> CheckForUpdatesAsync(
        string appAssetHint = "", string? currentVersionOverride = null)
    {
        var currentVersion = currentVersionOverride ?? BuildInfo.DisplayVersion;
        try
        {
            var response = await s_httpClient.GetAsync(GitHubReleasesUrl);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(
                    IsUpdateAvailable: false,
                    CurrentVersion: currentVersion,
                    LatestVersion: currentVersion,
                    ReleaseUrl: $"https://github.com/{GitHubRepo}/releases",
                    DownloadUrl: string.Empty,
                    ReleaseNotes: string.Empty,
                    ErrorMessage: $"Brak opublikowanych wydań GitHub Release dla {GitHubRepo} (HTTP {(int)response.StatusCode})."
                );
            }

            var json = await response.Content.ReadAsStringAsync();
            return ParseRelease(json, appAssetHint, currentVersion);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(
                IsUpdateAvailable: false,
                CurrentVersion: currentVersion,
                LatestVersion: currentVersion,
                ReleaseUrl: $"https://github.com/{GitHubRepo}/releases",
                DownloadUrl: string.Empty,
                ReleaseNotes: string.Empty,
                ErrorMessage: $"Błąd połączenia z GitHub API: {ex.Message}"
            );
        }
    }

    /// <summary>Parse a release without network access, so selection/version behavior is testable.</summary>
    public static UpdateCheckResult ParseRelease(string json, string appAssetHint, string currentVersion)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
        var tag = Text(root, "tag_name");
        var releaseUrl = Text(root, "html_url");
        var hidden = (root.TryGetProperty("draft", out var draft) && draft.GetBoolean())
            || (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean());
        var candidates = new List<(string Name, string Url)>();
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var asset in assets.EnumerateArray())
            {
                var name = Text(asset, "name");
                var url = Text(asset, "browser_download_url");
                if (!(name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))) continue;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") continue;
                if (!string.IsNullOrEmpty(appAssetHint) && !Regex.IsMatch(name,
                    @"(?:^|[._ -])" + Regex.Escape(appAssetHint) + @"(?:[._ -]|$)", RegexOptions.IgnoreCase)) continue;
                if (Regex.IsMatch(name, @"(?:arm64|linux|osx|macos|x86)", RegexOptions.IgnoreCase)) continue;
                candidates.Add((name, url));
            }
        // A missing CLIENT asset must never fall back to HOST (or a source/backend ZIP).
        var download = candidates.OrderBy(c => c.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Select(c => c.Url).FirstOrDefault() ?? "";
        return new(!hidden && IsVersionNewer(tag, currentVersion), currentVersion, OfficialVersion(tag),
            string.IsNullOrEmpty(releaseUrl) ? $"https://github.com/{GitHubRepo}/releases" : releaseUrl,
            hidden ? "" : download, Text(root, "body"), "");
    }

    // Tags retain a monotonically increasing prefix for already-installed BETA updaters.
    // Example: v7.0.0-release.1.0.0 is displayed and compared here as official 1.0.0.
    private static string OfficialVersion(string value)
    {
        var official = Regex.Match(value, @"^v?\d+\.\d+\.\d+-release\.(\d+\.\d+\.\d+)$", RegexOptions.IgnoreCase);
        return official.Success ? official.Groups[1].Value : value.Trim().TrimStart('v', 'V').Trim();
    }

    public static string PrepareUpdatePayload(string downloadPath, string tempDir, string exeName)
    {
        if (Path.GetFileName(exeName) != exeName || !exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Nieprawidłowa nazwa aplikacji.");
        var extract = Path.Combine(tempDir, "Extracted");
        Directory.CreateDirectory(extract);
        if (downloadPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var unpack = Path.Combine(tempDir, "Unpacked");
            ZipFile.ExtractToDirectory(downloadPath, unpack);
            var files = Directory.GetFiles(unpack, "*", SearchOption.AllDirectories);
            if (files.Length != 1 || !files[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Wydanie musi zawierać jeden samodzielny plik EXE.");
            downloadPath = files[0];
        }
        else if (!downloadPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Nieobsługiwany format aktualizacji.");
        using (var stream = File.OpenRead(downloadPath))
        using (var pe = new System.Reflection.PortableExecutable.PEReader(stream))
            if (pe.PEHeaders.PEHeader is null || pe.PEHeaders.CoffHeader.Machine != System.Reflection.PortableExecutable.Machine.Amd64)
                throw new InvalidDataException("Aktualizacja nie jest prawidłowym plikiem Windows x64 EXE.");
        File.Copy(downloadPath, Path.Combine(extract, exeName), true);
        return extract;
    }

    public static async Task<UpdateInstallResult> DownloadAndInstallUpdateAsync(
        string downloadUrl, Action<int>? progressCallback = null)
    {
        // Logs every step to %LocalAppData%\VRSRaceControl\logs\update.log — the SAME
        // directory the existing "Otwórz folder logów" Settings button already opens, so
        // diagnosing a failed update never again depends on guessing which layer broke.
        // The apply_update.ps1 script (which runs AFTER this process has already exited)
        // appends its own steps to this same file — see below.
        var updateLogger = new RotatingFileLogger("update");
        try
        {
            updateLogger.Write("INFO", $"=== Rozpoczynam aktualizację. downloadUrl={downloadUrl} ===");

            if (string.IsNullOrEmpty(downloadUrl))
            {
                updateLogger.Write("ERROR", "Brak adresu pobierania — przerywam.");
                return new UpdateInstallResult(false, "Brak adresu pobierania.");
            }

            // Each attempt owns a distinct staging directory; HOST and CLIENT cannot delete each other's download.
            var tempDir = Path.Combine(Path.GetTempPath(), "VRS_RaceControl_Update_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var fileName = Path.GetFileName(new Uri(downloadUrl).AbsolutePath);
            var tempFilePath = Path.Combine(tempDir, fileName);
            updateLogger.Write("INFO", $"Pobieram do: {tempFilePath}");

            using var downloadTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            using (var response = await s_httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, downloadTimeout.Token))
            {
                response.EnsureSuccessStatusCode();
                var totalBytes = response.Content.Headers.ContentLength ?? -1L;

                using (var contentStream = await response.Content.ReadAsStreamAsync())
                using (var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                {
                    var buffer = new byte[8192];
                    var totalRead = 0L;
                    int read;

                    while ((read = await contentStream.ReadAsync(buffer.AsMemory(), downloadTimeout.Token)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, read), downloadTimeout.Token);
                        totalRead += read;

                        if (totalBytes > 0)
                        {
                            var progress = (int)((totalRead * 100) / totalBytes);
                            progressCallback?.Invoke(progress);
                        }
                    }
                    if (totalBytes >= 0 && totalRead != totalBytes)
                        throw new InvalidDataException("Niekompletne pobranie aktualizacji.");
                }
            }
            updateLogger.Write("INFO", $"Pobrano plik: {fileName}, rozmiar={new FileInfo(tempFilePath).Length} bajtów");

            // AppDomain.CurrentDomain.BaseDirectory is NOT reliably the directory the real
            // .exe lives in for a self-contained single-file publish (it can be a temp
            // extraction directory instead) — Environment.ProcessPath is the value already
            // trusted for exeName below, so derive the app directory from that same source
            // instead of a second, potentially-divergent one. For framework-dependent/.zip
            // builds both sources already point at the same place, so this is a no-op there.
            var currentProcessPath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
            var exeName = Path.GetFileName(currentProcessPath);
            var currentAppDir = Path.GetDirectoryName(currentProcessPath) is { Length: > 0 } processDir
                ? processDir
                : AppDomain.CurrentDomain.BaseDirectory;
            updateLogger.Write("INFO",
                $"currentProcessPath={currentProcessPath} | exeName={exeName} | currentAppDir={currentAppDir} " +
                $"| AppDomain.BaseDirectory={AppDomain.CurrentDomain.BaseDirectory}");

            var extractDir = PrepareUpdatePayload(tempFilePath, tempDir, exeName);
            var scriptPath = Path.Combine(tempDir, "apply_update.ps1");
            var logPath = updateLogger.LogPath;
            var scriptContent = BuildApplyUpdateScript(extractDir, currentAppDir, exeName, tempDir, logPath);
            File.WriteAllText(scriptPath, scriptContent, new System.Text.UTF8Encoding(true));
            updateLogger.Write("INFO", $"Zapisano skrypt aktualizacyjny: {scriptPath}");

            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };

            // Any other copy of this exe (a second window, an instance relaunched by an earlier
            // failed update) keeps the file locked, so the update could never be applied.
            var stopped = StopOtherInstances(currentProcessPath,
                message => updateLogger.Write("INFO", message));
            if (stopped > 0) updateLogger.Write("INFO", $"Zamknięto inne uruchomione kopie aplikacji: {stopped}.");

            updateLogger.Write("INFO", "Uruchamiam skrypt aktualizacyjny i kończę ten proces...");
            Process.Start(startInfo);
            await ExitForUpdateAsync(updateLogger);
            return new UpdateInstallResult(true, string.Empty); // unreachable: the process has ended
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Update failed: {ex.Message}");
            updateLogger.Write("ERROR", $"Aktualizacja nie powiodła się: {ex}");
            return new UpdateInstallResult(false, ex.Message);
        }
    }

    /// <summary>
    /// Set by the app at startup: asks every window to close and flush its state (for the CLIENT,
    /// <c>Application.Shutdown()</c>) before the process is ended for an update. It must not block.
    /// Left null, the process is simply terminated.
    /// </summary>
    public static Action? PrepareForExit { get; set; }

    /// <summary>How long the app gets to close its windows before the process is force-terminated.</summary>
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Ends this process for an update and never returns. The app is asked to shut down cleanly
    /// first (windows closed, settings saved, overlay and connection released), with a hard
    /// backstop so a stuck handler can never leave the process — and the exe — alive.
    /// </summary>
    private static async Task ExitForUpdateAsync(RotatingFileLogger updateLogger)
    {
        var prepare = PrepareForExit;
        if (prepare == null)
        {
            Environment.Exit(0);
            return;
        }

        ExitWatchdog.Arm(ExitGrace); // armed BEFORE asking the UI: it must work even if the UI thread blocks
        try { prepare(); }
        catch (Exception ex) { updateLogger.Write("WARN", $"Zamykanie aplikacji nie powiodło się, wymuszam wyjście: {ex.Message}"); }
        // Never resumes: the process ends when the app finishes shutting down, or when the backstop fires.
        await Task.Delay(Timeout.Infinite);
    }

    /// <summary>
    /// Stops every OTHER running process started from <paramref name="exePath"/> (matched by full
    /// image path, so other installs are untouched): asks it to close, then kills it if it has not
    /// exited within <paramref name="gracefulWait"/>. Returns how many were stopped. Processes that
    /// cannot be inspected (other users, elevated) are skipped.
    /// </summary>
    public static int StopOtherInstances(string exePath, Action<string>? log = null, TimeSpan? gracefulWait = null)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return 0;
        var wait = gracefulWait ?? TimeSpan.FromSeconds(3);
        var stopped = 0;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exePath)))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                string? path;
                try { path = process.MainModule?.FileName; }
                catch { continue; }
                if (!string.Equals(path, exePath, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    log?.Invoke($"Zamykam inną uruchomioną kopię (PID {process.Id}).");
                    process.CloseMainWindow();
                    if (!process.WaitForExit(wait))
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(wait);
                    }
                    stopped++;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    log?.Invoke($"Nie udało się zamknąć procesu {process.Id}: {ex.Message}");
                }
            }
        }
        return stopped;
    }

    /// <summary>Replace the single EXE atomically after exit; retain the previous binary on failure.</summary>
    public static string BuildApplyUpdateScript(string extractDir, string currentAppDir, string exeName,
        string tempDir, string logPath, bool launchAfterUpdate = true, int maxAttempts = 60)
    {
        if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        var source = Path.GetFullPath(Path.Combine(extractDir, exeName));
        var target = Path.GetFullPath(Path.Combine(currentAppDir, exeName));
        if (source.Equals(target, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Source equals destination");
        var stagingRoot = Path.GetFullPath(tempDir).TrimEnd(Path.DirectorySeparatorChar);
        var ownedStaging = stagingRoot.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(stagingRoot).StartsWith("VRS_RaceControl_Update_", StringComparison.Ordinal)
            && source.StartsWith(stagingRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !target.StartsWith(stagingRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source)));
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        return $$"""
            $ErrorActionPreference = 'Stop'
            $source = {{Quote(source)}}
            $target = {{Quote(target)}}
            $logFile = {{Quote(logPath)}}
            $candidate = $target + '.' + [guid]::NewGuid().ToString('N') + '.new'
            $backup = $target + '.previous'
            function Log([string]$message) { Add-Content -LiteralPath $logFile -Value ((Get-Date -Format o) + ' UPDATE: ' + $message) -Encoding UTF8 }
            $success = $false
            try {
                Copy-Item -LiteralPath $source -Destination $candidate
                $hasher = [Security.Cryptography.SHA256]::Create()
                $hashStream = [IO.File]::OpenRead($candidate)
                try { $actualHash = [BitConverter]::ToString($hasher.ComputeHash($hashStream)).Replace('-', '') }
                finally { $hashStream.Dispose(); $hasher.Dispose() }
                if ($actualHash -ne '{{hash}}') { throw 'Błędna suma SHA256 pobranej aktualizacji' }
                for ($attempt = 1; $attempt -le {{maxAttempts}}; $attempt++) {
                    Start-Sleep -Seconds 1
                    try {
                        [IO.File]::Replace($candidate, $target, $backup, $true)
                        $success = $true
                        Log 'Aktualizacja zakończona powodzeniem'
                        break
                    } catch {
                        Log ('Ponowienie podmiany EXE ' + $attempt + ': ' + $_.Exception.Message)
                    }
                }
                if (!$success) { throw 'Plik nadal zablokowany lub brak uprawnień do katalogu' }
            } catch {
                Log ('AKTUALIZACJA NIEUDANA; poprzedni EXE zachowany: ' + $_.Exception.Message)
            } finally {
                if (Test-Path -LiteralPath $candidate) { Remove-Item -LiteralPath $candidate -Force }
            }
            if ({{(launchAfterUpdate ? "$true" : "$false")}}) {
                try { Start-Process -FilePath $target -WorkingDirectory ([IO.Path]::GetDirectoryName($target)) }
                catch { Log ('Nie udało się ponownie uruchomić aplikacji: ' + $_.Exception.Message) }
            }
            if (!$success) { exit 1 }
            {{(ownedStaging ? "try { Remove-Item -LiteralPath " + Quote(stagingRoot) + " -Recurse -Force } catch { Log ('Nie można usunąć plików tymczasowych: ' + $_.Exception.Message) }" : "# Test/caller staging is owned by the caller.")}}
            exit 0
            """;
    }

    private static readonly Regex NumericVersionPattern = new(@"\d+(\.\d+){1,3}", RegexOptions.Compiled);

    /// <summary>
    /// Compares two version-like strings. Tries a direct <see cref="Version"/> parse first;
    /// if either side isn't a clean version (e.g. <c>BuildInfo.DisplayVersion</c> is the label
    /// "Beta 2.2", which never parses), extracts the first numeric "major.minor[.build[.rev]]"
    /// substring from both sides and retries — this keeps "Beta 2.2" comparing correctly
    /// against a release tag like "2.3.0" instead of silently falling through to an ordinal
    /// string compare, where "2.3.0" &lt; "Beta 2.2" (the digit '2' sorts before the letter 'B').
    /// Only falls back to the ordinal compare if no numeric version can be extracted at all.
    /// </summary>
    public static bool IsVersionNewer(string latestVersionStr, string currentVersionStr)
    {
        if (string.IsNullOrEmpty(latestVersionStr)) return false;
        var latestOfficialTag = latestVersionStr.Contains("-release.", StringComparison.OrdinalIgnoreCase);
        var currentIsBeta = currentVersionStr.Contains("beta", StringComparison.OrdinalIgnoreCase);
        if (latestOfficialTag && currentIsBeta) return true;
        if (latestVersionStr.Contains("beta", StringComparison.OrdinalIgnoreCase) && !currentIsBeta) return false;
        latestVersionStr = OfficialVersion(latestVersionStr);
        currentVersionStr = OfficialVersion(currentVersionStr);

        var letterPattern = @"(?:beta\s+)?(\d+)\.(\d+)(?:\.0-beta[.-]|\.)([A-Z])$";
        string Canonical(string value) => Regex.Replace(value.Trim().TrimStart('v', 'V'), letterPattern,
            m => $"{m.Groups[1]}.{m.Groups[2]}.0-beta.{m.Groups[3].Value.ToUpperInvariant()}", RegexOptions.IgnoreCase);
        latestVersionStr = Canonical(latestVersionStr);
        currentVersionStr = Canonical(currentVersionStr);
        if (TryExtractNormalizedVersion(latestVersionStr, out var lv) &&
            TryExtractNormalizedVersion(currentVersionStr, out var cv) && lv == cv)
        {
            var latestBeta = Regex.Match(latestVersionStr, @"-beta\.([A-Z])$", RegexOptions.IgnoreCase);
            var currentBeta = Regex.Match(currentVersionStr, @"-beta\.([A-Z])$", RegexOptions.IgnoreCase);
            if (latestBeta.Success && currentBeta.Success)
                return string.Compare(latestBeta.Groups[1].Value, currentBeta.Groups[1].Value, StringComparison.OrdinalIgnoreCase) > 0;
            if (latestBeta.Success || currentBeta.Success) return !latestBeta.Success;
            return false;
        }

        if (Version.TryParse(latestVersionStr, out var latest) && Version.TryParse(currentVersionStr, out var current))
        {
            return latest > current;
        }

        if (TryExtractNormalizedVersion(latestVersionStr, out var latestExtracted) &&
            TryExtractNormalizedVersion(currentVersionStr, out var currentExtracted))
        {
            return latestExtracted > currentExtracted;
        }

        return string.Compare(latestVersionStr, currentVersionStr, StringComparison.OrdinalIgnoreCase) > 0;
    }

    /// <summary>
    /// Extracts the first numeric "major.minor[.build[.revision]]" run from <paramref name="input"/>
    /// and zero-pads missing components to a full 4-part <see cref="Version"/>. Zero-padding
    /// matters: <see cref="Version"/> itself defaults an unspecified component to -1, which would
    /// make "2.2.0" compare as greater than "2.2" even though they mean the same release — that
    /// mismatch is exactly the shape of the values this method compares (a release tag like
    /// "2.2.0" against a label-derived version like "Beta 2.2" that only extracts "2.2").
    /// </summary>
    private static bool TryExtractNormalizedVersion(string input, out Version version)
    {
        var match = NumericVersionPattern.Match(input);
        if (!match.Success)
        {
            version = new Version(0, 0, 0, 0);
            return false;
        }

        var parts = match.Value.Split('.');
        var components = new int[4];
        for (var i = 0; i < components.Length; i++)
        {
            components[i] = i < parts.Length && int.TryParse(parts[i], out var value) ? value : 0;
        }

        version = new Version(components[0], components[1], components[2], components[3]);
        return true;
    }
}
