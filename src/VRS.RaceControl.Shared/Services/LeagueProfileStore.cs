using System.Text.Json;
using System.Text.Json.Serialization;
using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

public sealed class LeagueProfileStore
{
    public const int CurrentSchemaVersion = 4;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _sync = new();
    private readonly string _filePath;
    private readonly SyncOutboxStore _outbox;

    public LeagueProfileStore(string? filePath = null, SyncOutboxStore? outbox = null)
    {
        var configuredRoot = Environment.GetEnvironmentVariable("VRS_RACE_CONTROL_DATA_ROOT");
        _filePath = filePath ?? (!string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(configuredRoot, "config", "league-profiles.json")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VRSRaceControl",
                "config",
                "league-profiles.json"));
        _outbox = outbox ?? new SyncOutboxStore(filePath == null
            ? null
            : Path.Combine(Path.GetDirectoryName(_filePath) ?? string.Empty, "sync-outbox.json"));
    }

    public LeagueProfile LoadActive()
    {
        lock (_sync)
        {
            var configuration = LoadConfigurationUnsafe();
            return configuration.Profiles.FirstOrDefault(profile =>
                       profile.Id.Equals(configuration.ActiveProfileId, StringComparison.OrdinalIgnoreCase))
                   ?? configuration.Profiles[0];
        }
    }

    public IReadOnlyList<LeagueProfile> LoadAll()
    {
        lock (_sync)
        {
            return LoadConfigurationUnsafe().Profiles;
        }
    }

    /// <summary>
    /// Returns the three profiles available in the HOST selector. Existing
    /// legacy/custom records are left in the file untouched, but cannot be
    /// selected by the race-control UI.
    /// </summary>
    public IReadOnlyList<LeagueProfile> LoadSupportedProfiles()
    {
        lock (_sync)
        {
            var configuration = LoadConfigurationUnsafe();
            var changed = false;
            var supported = new List<LeagueProfile>();

            foreach (var template in LeagueProfile.CreateSupportedProfiles())
            {
                var profile = configuration.Profiles.FirstOrDefault(item =>
                    item.Id.Equals(template.Id, StringComparison.OrdinalIgnoreCase));
                if (profile == null)
                {
                    profile = template;
                    configuration.Profiles.Add(profile);
                    changed = true;
                }

                var actionCount = profile.EnabledStandardFlags.Count;
                LeagueProfile.EnsureRequiredRaceControlActions(profile);
                changed |= actionCount != profile.EnabledStandardFlags.Count;
                supported.Add(profile);
            }

            if (!LeagueProfile.IsSupportedProfileId(configuration.ActiveProfileId))
            {
                configuration.ActiveProfileId = LeagueProfile.DefaultId;
                changed = true;
            }

            if (changed)
            {
                SaveConfigurationUnsafe(configuration);
            }
            return supported;
        }
    }

    /// <summary>
    /// Returns every stored league profile, seeding the three built-in starter
    /// profiles (VRS/IVS/VVS-E) if they don't exist yet. Unlike <see cref="LoadSupportedProfiles"/>,
    /// custom profiles created via the HOST UI are included and selectable.
    /// </summary>
    public IReadOnlyList<LeagueProfile> LoadForHost()
    {
        lock (_sync)
        {
            var configuration = LoadConfigurationUnsafe();
            var changed = false;

            foreach (var template in LeagueProfile.CreateSupportedProfiles())
            {
                var profile = configuration.Profiles.FirstOrDefault(item =>
                    item.Id.Equals(template.Id, StringComparison.OrdinalIgnoreCase));
                if (profile == null)
                {
                    profile = template;
                    configuration.Profiles.Add(profile);
                    changed = true;
                }

                var actionCount = profile.EnabledStandardFlags.Count;
                LeagueProfile.EnsureRequiredRaceControlActions(profile);
                changed |= actionCount != profile.EnabledStandardFlags.Count;
            }

            if (!configuration.Profiles.Any(profile =>
                    profile.Id.Equals(configuration.ActiveProfileId, StringComparison.OrdinalIgnoreCase)))
            {
                configuration.ActiveProfileId = LeagueProfile.DefaultId;
                changed = true;
            }

            foreach (var profile in configuration.Profiles)
            {
                if (TeamIdentityMigration.EnsureTeamIdentities(profile, out _))
                {
                    changed = true;
                }
            }

            if (changed)
            {
                SaveConfigurationUnsafe(configuration);
            }
            return configuration.Profiles;
        }
    }

    public void Activate(string profileId)
    {
        lock (_sync)
        {
            var configuration = LoadConfigurationUnsafe();
            if (!configuration.Profiles.Any(p => p.Id == profileId)) return;
            configuration.ActiveProfileId = profileId;
            SaveConfigurationUnsafe(configuration);
        }
    }

    public void Save(LeagueProfile profile, bool makeActive = true, long? expectedRevision = null,
        string updatedBy = "HOST")
    {
        var validationError = profile.Validate();
        if (validationError != null)
        {
            throw new ArgumentException(validationError, nameof(profile));
        }

        lock (_sync)
        {
            var configuration = LoadConfigurationUnsafe();
            var index = configuration.Profiles.FindIndex(item =>
                item.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase));
            var currentRevision = index >= 0 ? configuration.Profiles[index].Revision : 0;
            if (expectedRevision.HasValue && expectedRevision.Value != currentRevision)
            {
                throw new LeagueConfigurationConflictException(expectedRevision.Value, currentRevision);
            }
            profile.Revision = Math.Max(currentRevision, profile.Revision) + 1;
            profile.UpdatedAtUtc = DateTime.UtcNow;
            profile.UpdatedBy = string.IsNullOrWhiteSpace(updatedBy) ? "HOST" : updatedBy.Trim();
            if (index >= 0)
            {
                configuration.Profiles[index] = profile;
            }
            else
            {
                configuration.Profiles.Add(profile);
            }
            if (makeActive)
            {
                configuration.ActiveProfileId = profile.Id;
            }
            SaveConfigurationUnsafe(configuration);
            _outbox.Enqueue(SyncEntityType.LeagueProfile, profile.Id, SyncChangeKind.Upsert);
        }
    }

    /// <summary>
    /// Merges a cloud-origin profile into local storage without queuing it back
    /// onto the sync outbox — the mirror-image of <see cref="Save"/>, used by the
    /// cloud-sync pull loop so applying a pull can't re-trigger a push of the
    /// same record it just pulled.
    /// </summary>
    public void SyncProfile(LeagueProfile remoteProfile, bool acceptOlderRevision = false)
    {
        if (remoteProfile == null)
        {
            return;
        }

        lock (_sync)
        {
            var configuration = LoadConfigurationUnsafe();
            var index = configuration.Profiles.FindIndex(item =>
                item.Id.Equals(remoteProfile.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                if (!acceptOlderRevision && remoteProfile.Revision < configuration.Profiles[index].Revision)
                {
                    return;
                }
                configuration.Profiles[index] = remoteProfile;
            }
            else
            {
                configuration.Profiles.Add(remoteProfile);
            }
            SaveConfigurationUnsafe(configuration);
        }
    }

    public LeagueProfile Import(string sourcePath, bool makeActive = true)
    {
        var json = File.ReadAllText(sourcePath);
        var profile = JsonSerializer.Deserialize<LeagueProfile>(json, JsonOptions)
            ?? throw new InvalidDataException("Plik nie zawiera profilu ligi.");
        Save(profile, makeActive);
        return profile;
    }

    public void Export(LeagueProfile profile, string destinationPath)
    {
        var validationError = profile.Validate();
        if (validationError != null)
        {
            throw new ArgumentException(validationError, nameof(profile));
        }
        File.WriteAllText(destinationPath, JsonSerializer.Serialize(profile, JsonOptions));
    }

    public LeagueProfile RestoreDefault()
    {
        var profile = LeagueProfile.CreateDefault();
        Save(profile);
        return profile;
    }

    private LeagueProfileConfiguration LoadConfigurationUnsafe()
    {
        if (!File.Exists(_filePath))
        {
            return LeagueProfileConfiguration.Defaults();
        }

        try
        {
            var configuration = JsonSerializer.Deserialize<LeagueProfileConfiguration>(
                File.ReadAllText(_filePath),
                JsonOptions);
            if (configuration == null || configuration.Profiles.Count == 0)
            {
                throw new InvalidDataException("League profile file does not contain any profiles.");
            }
            var migrated = false;
            foreach (var profile in configuration.Profiles)
            {
                if (profile.LicenseDefinitions.Count == 0 && profile.Licenses.Count > 0)
                {
                    profile.LicenseDefinitions = profile.Licenses
                        .Where(code => !string.IsNullOrWhiteSpace(code))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Select(code => new LeagueLicenseDefinition { Code = code.Trim(), Name = code.Trim() })
                        .ToList();
                    migrated = true;
                }
                else if (profile.LicenseDefinitions.Count > 0)
                {
                    profile.Licenses = profile.LicenseDefinitions.Select(item => item.Code).ToList();
                }
                profile.SeasonDefinitions ??= new List<LeagueSeasonDefinition>();
                var seasons = profile.Seasons.Count > 0 ? profile.Seasons : new List<string> { "default" };
                var definitions = profile.SeasonDefinitions
                    .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                    .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                if (profile.SeasonDefinitions.Count == 0 || seasons.Any(season => !definitions.ContainsKey(season)))
                {
                    var hadDefinitions = definitions.Count > 0;
                    profile.SeasonDefinitions = seasons.Select((season, index) => definitions.TryGetValue(season, out var definition)
                        ? definition
                        : new LeagueSeasonDefinition
                        {
                            Id = season,
                            Name = season,
                            Races = !hadDefinitions && index == 0 ? profile.SeasonRaces.ToList() : new List<LeagueRace>()
                        }).ToList();
                    profile.Seasons = seasons;
                    migrated = true;
                }
            }
            if (configuration.SchemaVersion < CurrentSchemaVersion)
            {
                var backupPath = _filePath + $".backup-v{configuration.SchemaVersion}";
                if (!File.Exists(backupPath)) File.Copy(_filePath, backupPath);
                configuration.SchemaVersion = CurrentSchemaVersion;
                SaveConfigurationUnsafe(configuration);
            }
            else if (migrated)
            {
                SaveConfigurationUnsafe(configuration);
            }
            return configuration;
        }
        catch (JsonException exception)
        {
            var invalidPath = _filePath + $".invalid-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Copy(_filePath, invalidPath, overwrite: false);
            throw new InvalidDataException(
                $"League profiles could not be read. The original file was preserved and a diagnostic copy was written to '{invalidPath}'.",
                exception);
        }
    }

    private void SaveConfigurationUnsafe(LeagueProfileConfiguration configuration)
    {
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("League profile path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = _filePath + ".tmp";
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(configuration, JsonOptions));
        File.Move(temporaryPath, _filePath, overwrite: true);
    }

    private sealed class LeagueProfileConfiguration
    {
        public int SchemaVersion { get; set; }
        public string ActiveProfileId { get; set; } = LeagueProfile.DefaultId;
        public List<LeagueProfile> Profiles { get; set; } = new();
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }

        public static LeagueProfileConfiguration Defaults() => new()
        {
            SchemaVersion = CurrentSchemaVersion,
            Profiles = new List<LeagueProfile> { LeagueProfile.CreateDefault() }
        };
    }
}
