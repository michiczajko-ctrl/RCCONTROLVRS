using System.IO.Compression;
using System.Text.RegularExpressions;

namespace VRS.RaceControl.Shared.Diagnostics;

public sealed class RotatingFileLogger
{
    private static readonly object Gate = new();
    private static readonly Regex SensitiveData = new(
        "(password|token|secret|authorization)([\"' :=]+)([^\\s,\"']+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string _component;
    private readonly long _maximumBytes;
    private readonly int _archiveCount;
    private readonly string _logDirectory;

    public RotatingFileLogger(
        string component,
        long maximumBytes = 2 * 1024 * 1024,
        int archiveCount = 5,
        string? logDirectory = null)
    {
        _component = string.Concat(component.Where(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_'));
        _maximumBytes = Math.Max(64 * 1024, maximumBytes);
        _archiveCount = Math.Clamp(archiveCount, 1, 20);
        _logDirectory = logDirectory ?? LogDirectory;
    }

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VRSRaceControl",
        "logs");

    public string LogPath => Path.Combine(_logDirectory, $"{_component}.log");

    public void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(_logDirectory);
                RotateIfNeeded();
                var sanitized = SensitiveData.Replace(
                    message.ReplaceLineEndings(" "),
                    "$1$2[REDACTED]");
                File.AppendAllText(
                    LogPath,
                    $"[{DateTime.UtcNow:O}] [{level.ToUpperInvariant()}] {sanitized}{Environment.NewLine}");
            }
        }
        catch
        {
            // Diagnostics must never crash the race-control process.
        }
    }

    public static void DeleteLogsOlderThan(TimeSpan maximumAge)
    {
        if (!Directory.Exists(LogDirectory))
        {
            return;
        }
        var threshold = DateTime.UtcNow - maximumAge;
        foreach (var path in Directory.EnumerateFiles(LogDirectory, "*.log*"))
        {
            if (File.GetLastWriteTimeUtc(path) < threshold)
            {
                File.Delete(path);
            }
        }
    }

    public static string ExportBundle(string destinationZip, string diagnosticsText)
    {
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "vrs-diagnostics",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            File.WriteAllText(
                Path.Combine(temporaryDirectory, "diagnostics.txt"),
                SensitiveData.Replace(diagnosticsText, "$1$2[REDACTED]"));
            if (Directory.Exists(LogDirectory))
            {
                foreach (var log in Directory.EnumerateFiles(LogDirectory, "*.log*"))
                {
                    var sanitizedLines = File.ReadLines(log)
                        .Select(line => SensitiveData.Replace(line, "$1$2[REDACTED]"));
                    File.WriteAllLines(
                        Path.Combine(temporaryDirectory, Path.GetFileName(log)),
                        sanitizedLines);
                }
            }
            File.Delete(destinationZip);
            ZipFile.CreateFromDirectory(temporaryDirectory, destinationZip);
            return destinationZip;
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(LogPath) || new FileInfo(LogPath).Length < _maximumBytes)
        {
            return;
        }
        var oldest = $"{LogPath}.{_archiveCount}";
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }
        for (var index = _archiveCount - 1; index >= 1; index--)
        {
            var source = $"{LogPath}.{index}";
            if (File.Exists(source))
            {
                File.Move(source, $"{LogPath}.{index + 1}");
            }
        }
        File.Move(LogPath, $"{LogPath}.1");
    }
}
