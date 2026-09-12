using System.Text.Json;
using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

public sealed class LeagueProfileStore
{
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
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VRSRaceControl",
            "config",
            "league-profiles.json");
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

    public void Save(LeagueProfile profile, bool makeActive = true)
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
    public void SyncProfile(LeagueProfile remoteProfile)
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
                return LeagueProfileConfiguration.Defaults();
            }
            return configuration;
        }
        catch (JsonException)
        {
            var invalidPath = _filePath + $".invalid-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Move(_filePath, invalidPath, overwrite: false);
            return LeagueProfileConfiguration.Defaults();
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
        public string ActiveProfileId { get; set; } = LeagueProfile.DefaultId;
        public List<LeagueProfile> Profiles { get; set; } = new();

        public static LeagueProfileConfiguration Defaults() => new()
        {
            Profiles = new List<LeagueProfile> { LeagueProfile.CreateDefault() }
        };
    }
}
