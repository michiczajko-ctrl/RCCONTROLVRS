using System.Text.Json;
using System.Text.Json.Serialization;

namespace VRS.RaceControl.Shared.Models;

/// <summary>
/// Registered driver account. Passwords are represented only by a salted
/// password hash. The legacy plaintext property is read only for migration and
/// is cleared before the account is persisted again.
/// </summary>
public class UserAccount
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("driverName")]
    public string DriverName { get; set; } = string.Empty;

    [JsonPropertyName("login")]
    public string Login { get; set; } = string.Empty;

    [JsonPropertyName("driverNumber")]
    public string DriverNumber { get; set; } = string.Empty;

    [JsonPropertyName("leagueId")]
    public string LeagueId { get; set; } = "vrs";

    [JsonPropertyName("seasonId")]
    public string SeasonId { get; set; } = "default";

    [JsonPropertyName("team")]
    public string Team { get; set; } = string.Empty;

    [JsonPropertyName("affiliation")]
    public string Affiliation { get; set; } = string.Empty;

    [JsonPropertyName("licenseCategory")]
    public string LicenseCategory { get; set; } = string.Empty;

    [JsonPropertyName("safetyRating")]
    public double SafetyRating { get; set; }

    [JsonPropertyName("seasonRaceCount")]
    public int SeasonRaceCount { get; set; }

    [JsonPropertyName("seasonRaceLimit")]
    public int SeasonRaceLimit { get; set; }

    [JsonPropertyName("penaltyPoints")]
    public int PenaltyPoints { get; set; }

    [JsonPropertyName("penaltyPointsLimit")]
    public int PenaltyPointsLimit { get; set; } = 12;

    [JsonPropertyName("championshipPosition")]
    public int? ChampionshipPosition { get; set; }

    [JsonPropertyName("status")]
    public AccountStatus Status { get; set; } = AccountStatus.Active;

    [JsonPropertyName("lastLoginAt")]
    public DateTime? LastLoginAt { get; set; }

    [JsonPropertyName("lastActivityAt")]
    public DateTime? LastActivityAt { get; set; }

    [JsonPropertyName("administrativeNote")]
    public string AdministrativeNote { get; set; } = string.Empty;

    [JsonPropertyName("avatarPath")]
    public string? AvatarPath { get; set; }

    [JsonPropertyName("changeHistory")]
    public List<AccountChangeEntry> ChangeHistory { get; set; } = new();

    [JsonPropertyName("passwordHash")]
    public string PasswordHash { get; set; } = string.Empty;

    [JsonPropertyName("passwordSalt")]
    public string PasswordSalt { get; set; } = string.Empty;

    [JsonPropertyName("passwordIterations")]
    public int PasswordIterations { get; set; }

    /// <summary>
    /// Backward-compatible input for pre-BETA files. Never set for new
    /// accounts and removed during migration.
    /// </summary>
    [JsonPropertyName("password")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyPassword { get; set; }

    // Fallback/alias for callers that used the old e-mail member.
    [JsonIgnore]
    public string Email
    {
        get => DriverNumber;
        set => DriverNumber = value;
    }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Secret bound to this account for authenticating a Team Hub channel connection (see
    /// TeamHubAuthService, MessageType.TeamHubAuth) — the Team Hub channel's join code is
    /// deterministically derivable from the public league id, so a connection's
    /// self-declared driver name must never be trusted as identity on its own.
    /// DELIBERATELY NEVER copied into DriverProfilePayload/AccountSyncPayload (both are
    /// broadcast to every connected driver) — see EconomyPayloadScopeTests-style
    /// regression coverage. Generated on Create/ResetPassword; UserAccountStore backfills
    /// it for pre-existing accounts on load.
    /// </summary>
    [JsonPropertyName("teamHubToken")]
    public string TeamHubToken { get; set; } = string.Empty;
}

public sealed class AccountChangeEntry
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string Actor { get; set; } = "HOST";
    public string Action { get; set; } = string.Empty;
}

[JsonConverter(typeof(SafeAccountStatusConverter))]
public enum AccountStatus
{
    Active,
    Inactive,
    Suspended,
    PendingReset
}

public class SafeAccountStatusConverter : JsonConverter<AccountStatus>
{
    public override AccountStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            if (Enum.TryParse<AccountStatus>(value, true, out var status))
            {
                return status;
            }
        }
        // W razie błędu parsowania lub wartości "PendingApproval" przywróć status Active
        return AccountStatus.Active;
    }

    public override void Write(Utf8JsonWriter writer, AccountStatus value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}
