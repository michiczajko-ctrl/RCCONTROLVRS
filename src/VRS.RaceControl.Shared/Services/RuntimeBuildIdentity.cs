using System.Reflection;
using System.Security.Cryptography;

namespace VRS.RaceControl.Shared.Services;

public sealed record RuntimeBuildIdentity(string Component, string Version, string? InformationalVersion,
    Guid ModuleId, string? Sha256)
{
    private static readonly Lazy<RuntimeBuildIdentity> Entry = new(() => Capture(Assembly.GetEntryAssembly() ?? typeof(RuntimeBuildIdentity).Assembly));
    public static RuntimeBuildIdentity Current => Entry.Value;
    /// <summary>Hashing a self-contained executable takes a moment; do it off the UI thread before diagnostics are opened.</summary>
    public static void Warm() => _ = Task.Run(() => Entry.Value);
    /// <summary>One support-friendly line: which exact binary produced a log or a diagnostics export. Contains no secrets.</summary>
    public string ToDiagnosticLine() => $"Build: {Component} {Version} ({InformationalVersion ?? "no informational version"}); MVID {ModuleId:D}; SHA256 {Sha256 ?? "unavailable"}";
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("SingleFile", "IL3000",
        Justification = "Unbundled assemblies use Location; a bundled entry assembly hashes its process executable instead.")]
    public static RuntimeBuildIdentity Capture(Assembly assembly)
    {
        string? hash = null;
        try
        {
            var location = assembly.Location;
            if (string.IsNullOrWhiteSpace(location) && assembly == Assembly.GetEntryAssembly()) location = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(location))
            {
                using var stream = File.Open(location, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                hash = Convert.ToHexString(SHA256.HashData(stream));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { }
        return new(assembly.GetName().Name ?? "unknown", assembly.GetName().Version?.ToString() ?? "unknown",
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            assembly.ManifestModule.ModuleVersionId, hash);
    }
}
