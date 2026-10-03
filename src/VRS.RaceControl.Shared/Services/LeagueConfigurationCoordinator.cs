using System.Text.Json;
using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

/// <summary>
/// Coordinates the only durable edit transaction for league configuration. The profile
/// remains the revision authority; economy settings are committed in the same operation
/// and the previous local state is restored if the second store fails.
/// </summary>
public sealed class LeagueConfigurationCoordinator
{
    private static readonly JsonSerializerOptions CloneOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly LeagueProfileStore _profiles;
    private readonly TeamEconomyStore _economy;
    private readonly SyncOutboxStore _outbox;
    private readonly object _saveGate = new();

    public LeagueConfigurationCoordinator(LeagueProfileStore profiles, TeamEconomyStore economy,
        SyncOutboxStore? outbox = null)
    {
        _profiles = profiles;
        _economy = economy;
        _outbox = outbox ?? new SyncOutboxStore();
    }

    public LeagueConfigurationSnapshot Save(
        LeagueProfile profile,
        LeagueEconomySettings economy,
        long expectedRevision,
        string updatedBy = "HOST", long? expectedCloudRevision = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(economy);
        Validate(profile);

        lock (_saveGate)
        {
            var previousProfile = _profiles.LoadAll().FirstOrDefault(item =>
                item.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase));
            var previousEconomy = Clone(_economy.LoadSettings(profile.Id, economy.SeasonId));
            var previousRevision = profile.Revision;
            var previousUpdatedAt = profile.UpdatedAtUtc;
            var previousUpdatedBy = profile.UpdatedBy;

            try
            {
                economy.LeagueId = profile.Id;
                _profiles.Save(profile, expectedRevision: expectedRevision, updatedBy: updatedBy);
                _economy.SaveSettings(economy);
                var entityId = ConfigurationEntityId(profile.Id, economy.SeasonId);
                _outbox.Enqueue(SyncEntityType.LeagueConfiguration, entityId, SyncChangeKind.Upsert,
                    Guid.NewGuid().ToString("N"), profile.Revision, expectedCloudRevision ?? expectedRevision,
                    JsonSerializer.Serialize(new LeagueConfigurationSnapshot(profile, economy, profile.Revision, profile.UpdatedAtUtc, updatedBy)));
                // The configuration bundle supersedes the two independently queued markers.
                _outbox.Remove(SyncEntityType.LeagueProfile, profile.Id);
                _outbox.Remove(SyncEntityType.EconomySettings, $"{profile.Id}:{economy.SeasonId}");
            }
            catch
            {
                if (previousProfile is not null)
                    _profiles.SyncProfile(previousProfile, acceptOlderRevision: true);
                _economy.SaveSettings(previousEconomy, enqueueForSync: false);
                profile.Revision = previousRevision;
                profile.UpdatedAtUtc = previousUpdatedAt;
                profile.UpdatedBy = previousUpdatedBy;
                throw;
            }

            return new LeagueConfigurationSnapshot(
                profile, economy, profile.Revision, profile.UpdatedAtUtc, profile.UpdatedBy);
        }
    }

    public static string ConfigurationEntityId(string leagueId, string seasonId) =>
        $"{leagueId}:{seasonId}";

    public static void Validate(LeagueProfile profile)
    {
        if (profile.SeasonRaces.Concat(profile.SeasonDefinitions.SelectMany(s => s.Races)).Any(r =>
            r.TrackFact.Length > 500 || r.CostPresetId is < 1 or > 5 || r.DurationMinutes is <= 0))
            throw new ArgumentException("Ciekawostka: maks. 500 znaków; koszt: preset 1–5; czas musi być dodatni.");
        var error = profile.Validate()
            ?? profile.LicenseDefinitions.Select(item => item.Validate()).FirstOrDefault(item => item is not null);
        if (error is null && profile.LicenseDefinitions
                .GroupBy(item => item.Code.Trim(), StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            error = "Kody licencji muszą być unikalne.";
        if (error is not null) throw new ArgumentException(error, nameof(profile));
    }

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(
        JsonSerializer.Serialize(value, CloneOptions), CloneOptions)
        ?? throw new InvalidOperationException("Nie udało się utworzyć kopii konfiguracji do wycofania zapisu.");
}
