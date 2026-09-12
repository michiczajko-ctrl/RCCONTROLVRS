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
    /// see the asset-selection loop below. This is a naming *convention*, not a guarantee: it
    /// only works if released assets are actually named to contain the hint.
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
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? "" : "";
            var htmlUrl = root.TryGetProperty("html_url", out var urlProp) ? urlProp.GetString() ?? "" : "";
            var body = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? "" : "";

            var downloadUrl = string.Empty;
            if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
            {
                string? firstMatchUrl = null;
                string? hintMatchUrl = null;
                foreach (var asset in assetsProp.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                    var url = asset.TryGetProperty("browser_download_url", out var dlProp) ? dlProp.GetString() ?? "" : "";

                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        firstMatchUrl ??= url;
                        if (!string.IsNullOrEmpty(appAssetHint) &&
                            name.Contains(appAssetHint, StringComparison.OrdinalIgnoreCase))
                        {
                            hintMatchUrl ??= url;
                        }
                    }
                }
                // Prefer the asset matching this app's hint (e.g. "Host"/"Client") when a
                // release bundles both apps; fall back to the first package otherwise so
                // single-asset releases keep working unchanged.
                downloadUrl = hintMatchUrl ?? firstMatchUrl ?? string.Empty;
            }

            var cleanLatest = tagName.TrimStart('v', 'V');
            var isNewer = IsVersionNewer(cleanLatest, currentVersion);

            return new UpdateCheckResult(
                IsUpdateAvailable: isNewer,
                CurrentVersion: currentVersion,
                LatestVersion: cleanLatest,
                ReleaseUrl: string.IsNullOrEmpty(htmlUrl) ? $"https://github.com/{GitHubRepo}/releases" : htmlUrl,
                DownloadUrl: downloadUrl,
                ReleaseNotes: body,
                ErrorMessage: string.Empty
            );
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

    public static async Task<UpdateInstallResult> DownloadAndInstallUpdateAsync(
        string downloadUrl, Action<int>? progressCallback = null)
    {
        // Logs every step to %LocalAppData%\VRSRaceControl\logs\update.log — the SAME
        // directory the existing "Otwórz folder logów" Settings button already opens, so
        // diagnosing a failed update never again depends on guessing which layer broke.
        // The apply_update.bat script (which runs AFTER this process has already exited)
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

            var tempDir = Path.Combine(Path.GetTempPath(), "VRS_RaceControl_Update");
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            Directory.CreateDirectory(tempDir);

            var fileName = Path.GetFileName(new Uri(downloadUrl).AbsolutePath);
            var tempFilePath = Path.Combine(tempDir, fileName);
            updateLogger.Write("INFO", $"Pobieram do: {tempFilePath}");

            using (var response = await s_httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var totalBytes = response.Content.Headers.ContentLength ?? -1L;

                using (var contentStream = await response.Content.ReadAsStreamAsync())
                using (var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                {
                    var buffer = new byte[8192];
                    var totalRead = 0L;
                    int read;

                    while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, read);
                        totalRead += read;

                        if (totalBytes > 0)
                        {
                            var progress = (int)((totalRead * 100) / totalBytes);
                            progressCallback?.Invoke(progress);
                        }
                    }
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

            var extractDir = Path.Combine(tempDir, "Extracted");
            if (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(tempFilePath, extractDir);
                updateLogger.Write("INFO", $"Rozpakowano ZIP do: {extractDir}");
            }
            else
            {
                // Self-contained single-file releases are named for GitHub clarity (e.g.
                // "VRS-RaceControl-Client-3.0.exe"), which never matches the actual running
                // executable's filename. Copying under the downloaded name would leave an
                // inert extra file next to the untouched original — xcopy below must
                // overwrite the exe under the name the app is actually running as.
                Directory.CreateDirectory(extractDir);
                File.Copy(tempFilePath, Path.Combine(extractDir, exeName), true);
                updateLogger.Write("INFO", $"Skopiowano pojedynczy plik do: {Path.Combine(extractDir, exeName)}");
            }

            var scriptPath = Path.Combine(tempDir, "apply_update.bat");
            var logPath = updateLogger.LogPath;
            var scriptContent = $@"@echo off
echo [%date% %time%] BAT: start, currentAppDir={currentAppDir}, exeName={exeName} >> ""{logPath}""
echo Aktualizacja VRS Race Control...
timeout /t 2 /nobreak > NUL
echo [%date% %time%] BAT: wykonuję xcopy /E /Y /I ""{extractDir}\*"" ""{currentAppDir}"" >> ""{logPath}""
xcopy /E /Y /I ""{extractDir}\*"" ""{currentAppDir}"" >> ""{logPath}"" 2>&1
echo [%date% %time%] BAT: xcopy zakończony, kod wyjścia %ERRORLEVEL% >> ""{logPath}""
echo [%date% %time%] BAT: uruchamiam ""{Path.Combine(currentAppDir, exeName)}"" >> ""{logPath}""
start """" ""{Path.Combine(currentAppDir, exeName)}""
echo [%date% %time%] BAT: sprzątam tempDir, koniec >> ""{logPath}""
rd /s /q ""{tempDir}""
";
            File.WriteAllText(scriptPath, scriptContent);
            updateLogger.Write("INFO", $"Zapisano skrypt aktualizacyjny: {scriptPath}");

            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{scriptPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };

            updateLogger.Write("INFO", "Uruchamiam skrypt aktualizacyjny i kończę ten proces...");
            Process.Start(startInfo);
            Environment.Exit(0);
            return new UpdateInstallResult(true, string.Empty);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Update failed: {ex.Message}");
            updateLogger.Write("ERROR", $"Aktualizacja nie powiodła się: {ex}");
            return new UpdateInstallResult(false, ex.Message);
        }
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
