using System.Text.Json;
using System.Text.Json.Serialization;
using VRS.RaceControl.Shared.Models;
using VRS.RaceControl.Shared.Security;

namespace VRS.RaceControl.Shared.Services;

/// <summary>
/// Shared account repository used by HOST and CLIENT. It stores account data
/// below LocalApplicationData so both applications see the same records.
/// Existing users.json files are imported into that canonical store on first use.
/// </summary>
public sealed class UserAccountStore
{
    private static readonly object s_gate = new();
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new SafeAccountStatusConverter(), new JsonStringEnumConverter() }
    };

    private readonly SyncOutboxStore _outbox;

    public UserAccountStore(string? path = null, IEnumerable<string>? legacyPaths = null, SyncOutboxStore? outbox = null)
    {
        FilePath = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VRSRaceControl",
            "data",
            "users.json");
        LegacyPaths = legacyPaths?.ToArray()
            ?? GetDefaultCandidatePaths();
        _outbox = outbox ?? new SyncOutboxStore(path == null
            ? null
            : Path.Combine(Path.GetDirectoryName(FilePath) ?? string.Empty, "sync-outbox.json"));
    }

    public string FilePath { get; }
    public IReadOnlyList<string> LegacyPaths { get; }

    private static string[] GetDefaultCandidatePaths()
    {
        var paths = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "users.json"),
            Path.Combine(AppContext.BaseDirectory, "data", "users.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "Host", "users.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "publish", "Host", "users.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VRSRaceControl", "data", "users.json")
        };
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyList<UserAccount> Load()
    {
        lock (s_gate)
        {
            EnsureMigratedLocked();
            return LoadFromPathLocked(FilePath);
        }
    }

    /// <param name="enqueueForSync">
    /// True for a genuine local-origin change (e.g. a driver's self-registration
    /// pushed in over the network) that should reach Supabase. The cloud-sync
    /// pull loop passes false when applying a cloud-origin record locally, so
    /// applying a pull doesn't re-queue the very record it just pulled.
    /// </param>
    public bool SyncAccount(UserAccount remoteAccount, bool enqueueForSync = true)
    {
        if (remoteAccount == null) return false;
        lock (s_gate)
        {
            EnsureMigratedLocked();
            var accounts = LoadFromPathLocked(FilePath).ToList();
            var index = accounts.FindIndex(a =>
                (!string.IsNullOrWhiteSpace(remoteAccount.Id) && string.Equals(a.Id, remoteAccount.Id, StringComparison.Ordinal))
                || string.Equals(a.Login, remoteAccount.Login, StringComparison.OrdinalIgnoreCase)
                || string.Equals(a.DriverName, remoteAccount.DriverName, StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
            {
                var existing = accounts[index];
                if (string.IsNullOrEmpty(remoteAccount.PasswordHash) && !string.IsNullOrEmpty(existing.PasswordHash))
                {
                    remoteAccount.PasswordHash = existing.PasswordHash;
                    remoteAccount.PasswordSalt = existing.PasswordSalt;
                    remoteAccount.PasswordIterations = existing.PasswordIterations;
                }
                // Same reasoning as PasswordHash above (Faza 10): a remote row synced before
                // the team_hub_token column existed, or pulled mid-race with a not-yet-pushed
                // local backfill, must never silently wipe a good local token — that would
                // break both the Team Hub channel auth AND the new Supabase-direct read.
                if (string.IsNullOrEmpty(remoteAccount.TeamHubToken) && !string.IsNullOrEmpty(existing.TeamHubToken))
                {
                    remoteAccount.TeamHubToken = existing.TeamHubToken;
                }
                accounts[index] = remoteAccount;
            }
            else
            {
                accounts.Add(remoteAccount);
            }

            SaveLocked(accounts);
            if (enqueueForSync)
            {
                _outbox.Enqueue(SyncEntityType.Account, remoteAccount.Id, SyncChangeKind.Upsert);
            }
            return true;
        }
    }

    public void SyncAccounts(IEnumerable<UserAccount> remoteAccounts, bool enqueueForSync = true)
    {
        if (remoteAccounts == null) return;
        foreach (var acc in remoteAccounts)
        {
            SyncAccount(acc, enqueueForSync);
        }
    }

    public AuthenticationResult Authenticate(string loginOrName, string password)
    {
        if (string.IsNullOrWhiteSpace(loginOrName) || string.IsNullOrEmpty(password))
        {
            return AuthenticationResult.Failed(AuthenticationFailure.InvalidCredentials);
        }

        lock (s_gate)
        {
            EnsureMigratedLocked();
            var accounts = LoadFromPathLocked(FilePath).ToList();
            var account = accounts.FirstOrDefault(a =>
                string.Equals(a.Login, loginOrName.Trim(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(a.DriverName, loginOrName.Trim(), StringComparison.OrdinalIgnoreCase));

            if (account == null)
            {
                return AuthenticationResult.Failed(AuthenticationFailure.AccountNotFound);
            }

            if (account.Status != AccountStatus.Active)
            {
                return AuthenticationResult.Failed(AuthenticationFailure.AccountInactive);
            }

            if (!PasswordHasher.Verify(
                    password,
                    account.PasswordHash,
                    account.PasswordSalt,
                    account.PasswordIterations))
            {
                return AuthenticationResult.Failed(AuthenticationFailure.InvalidCredentials);
            }

            account.LastLoginAt = DateTime.UtcNow;
            account.LastActivityAt = account.LastLoginAt;
            SaveLocked(accounts);
            return AuthenticationResult.Succeeded(account);
        }
    }

    /// <summary>Import a server-confirmed identity by ID only, without a local password.</summary>
    public AccountOperationResult ImportOnlineProfile(OnlineAccountProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.Login)
            || string.IsNullOrWhiteSpace(profile.DriverName)) return AccountOperationResult.Failed("Invalid online profile");
        lock (s_gate)
        {
            EnsureMigratedLocked();
            var accounts = LoadFromPathLocked(FilePath).ToList();
            var account = accounts.FirstOrDefault(a => a.Id == profile.Id);
            if (accounts.Any(a => a.Id != profile.Id &&
                (string.Equals(a.Login, profile.Login, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(a.DriverName, profile.DriverName, StringComparison.OrdinalIgnoreCase))))
                return AccountOperationResult.Failed("Local identity conflict");
            if (account == null) { account = new UserAccount { Id = profile.Id, LeagueId = profile.LeagueId }; accounts.Add(account); }
            account.Login = profile.Login;
            account.DriverName = profile.DriverName;
            account.DriverNumber = profile.DriverNumber;
            SaveLocked(accounts);
            _outbox.Enqueue(SyncEntityType.Account, account.Id, SyncChangeKind.Upsert);
            return AccountOperationResult.Succeeded(account);
        }
    }

    public AccountOperationResult Create(
        string driverName,
        string driverNumber,
        string password,
        string? login = null,
        string leagueId = "vrs",
        string seasonId = "default")
    {
        var validation = ValidateNewAccount(driverName, driverNumber, password, login);
        if (!validation.IsSuccess)
        {
            return validation;
        }

        lock (s_gate)
        {
            EnsureMigratedLocked();
            var accounts = LoadFromPathLocked(FilePath).ToList();
            var normalizedName = driverName.Trim();
            var normalizedLogin = string.IsNullOrWhiteSpace(login) ? normalizedName : login.Trim();
            var normalizedNumber = driverNumber.Trim();
            var normalizedLeague = NormalizeScope(leagueId, "vrs");
            var normalizedSeason = NormalizeScope(seasonId, "default");

            if (accounts.Any(a =>
                    string.Equals(a.Login, normalizedLogin, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(a.DriverName, normalizedName, StringComparison.OrdinalIgnoreCase)))
            {
                return AccountOperationResult.Failed("Konto z takim loginem lub nazwą już istnieje.");
            }

            if (accounts.Any(a =>
                    string.Equals(a.DriverNumber, normalizedNumber, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(a.LeagueId, normalizedLeague, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(a.SeasonId, normalizedSeason, StringComparison.OrdinalIgnoreCase)))
            {
                return AccountOperationResult.Failed("Ten numer startowy jest już zajęty w wybranej lidze i sezonie.");
            }

            var passwordHash = PasswordHasher.Hash(password);
            var account = new UserAccount
            {
                DriverName = normalizedName,
                Login = normalizedLogin,
                DriverNumber = normalizedNumber,
                LeagueId = normalizedLeague,
                SeasonId = normalizedSeason,
                PasswordHash = passwordHash.Hash,
                PasswordSalt = passwordHash.Salt,
                PasswordIterations = passwordHash.Iterations,
                CreatedAt = DateTime.UtcNow,
                Status = AccountStatus.Active,
                TeamHubToken = GenerateTeamHubToken()
            };
            accounts.Add(account);
            SaveLocked(accounts);
            _outbox.Enqueue(SyncEntityType.Account, account.Id, SyncChangeKind.Upsert);
            return AccountOperationResult.Succeeded(account);
        }
    }

    public AccountOperationResult Update(UserAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (string.IsNullOrWhiteSpace(account.Id)
            || string.IsNullOrWhiteSpace(account.DriverName)
            || string.IsNullOrWhiteSpace(account.Login)
            || !IsValidDriverNumber(account.DriverNumber))
        {
            return AccountOperationResult.Failed("Wymagane dane kierowcy są nieprawidłowe.");
        }

        account.SafetyRating = Math.Clamp(account.SafetyRating, 0, 100);
        account.PenaltyPoints = Math.Max(0, account.PenaltyPoints);
        account.PenaltyPointsLimit = Math.Max(0, account.PenaltyPointsLimit);
        account.SeasonRaceCount = Math.Max(0, account.SeasonRaceCount);
        account.SeasonRaceLimit = Math.Max(0, account.SeasonRaceLimit);

        lock (s_gate)
        {
            EnsureMigratedLocked();
            var accounts = LoadFromPathLocked(FilePath).ToList();
            var index = accounts.FindIndex(a => string.Equals(a.Id, account.Id, StringComparison.Ordinal));
            if (index < 0)
            {
                return AccountOperationResult.Failed("Nie znaleziono konta.");
            }

            if (accounts.Any(a => a.Id != account.Id
                && string.Equals(a.Login, account.Login, StringComparison.OrdinalIgnoreCase)))
            {
                return AccountOperationResult.Failed("Ten login jest już zajęty.");
            }

            if (accounts.Any(a => a.Id != account.Id
                && string.Equals(a.DriverNumber, account.DriverNumber, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.LeagueId, account.LeagueId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.SeasonId, account.SeasonId, StringComparison.OrdinalIgnoreCase)))
            {
                return AccountOperationResult.Failed("Ten numer startowy jest już zajęty w wybranej lidze i sezonie.");
            }

            account.LegacyPassword = null;
            accounts[index] = account;
            SaveLocked(accounts);
            _outbox.Enqueue(SyncEntityType.Account, account.Id, SyncChangeKind.Upsert);
            return AccountOperationResult.Succeeded(account);
        }
    }

    public AccountOperationResult ResetPassword(string accountId, string newPassword)
    {
        if (!IsValidPassword(newPassword))
        {
            return AccountOperationResult.Failed(
                "Hasło nie może być puste.");
        }

        lock (s_gate)
        {
            EnsureMigratedLocked();
            var accounts = LoadFromPathLocked(FilePath).ToList();
            var account = accounts.FirstOrDefault(a => a.Id == accountId);
            if (account == null)
            {
                return AccountOperationResult.Failed("Nie znaleziono konta.");
            }

            var hash = PasswordHasher.Hash(newPassword);
            account.PasswordHash = hash.Hash;
            account.PasswordSalt = hash.Salt;
            account.PasswordIterations = hash.Iterations;
            account.LegacyPassword = null;
            account.Status = AccountStatus.Active;
            account.TeamHubToken = GenerateTeamHubToken();
            SaveLocked(accounts);
            _outbox.Enqueue(SyncEntityType.Account, account.Id, SyncChangeKind.Upsert);
            return AccountOperationResult.Succeeded(account);
        }
    }

    public bool Delete(string accountId)
    {
        lock (s_gate)
        {
            EnsureMigratedLocked();
            var accounts = LoadFromPathLocked(FilePath).ToList();
            var removed = accounts.RemoveAll(a => a.Id == accountId) > 0;
            if (removed)
            {
                SaveLocked(accounts);
                _outbox.Enqueue(SyncEntityType.Account, accountId, SyncChangeKind.Delete);
            }
            return removed;
        }
    }

    public static AccountOperationResult ValidateNewAccount(
        string driverName,
        string driverNumber,
        string password,
        string? login = null)
    {
        if (string.IsNullOrWhiteSpace(driverName)
            || driverName.Trim().Length is < 2 or > 80)
        {
            return AccountOperationResult.Failed("Nazwa kierowcy musi mieć od 2 do 80 znaków.");
        }

        if (!string.IsNullOrWhiteSpace(login) && login.Trim().Length is < 2 or > 40)
        {
            return AccountOperationResult.Failed("Login musi mieć od 2 do 40 znaków.");
        }

        if (!IsValidDriverNumber(driverNumber))
        {
            return AccountOperationResult.Failed("Numer kierowcy musi być liczbą od 0 do 9999.");
        }

        if (!IsValidPassword(password))
        {
            return AccountOperationResult.Failed(
                "Hasło nie może być puste.");
        }

        return AccountOperationResult.Succeeded();
    }

    private static bool IsValidDriverNumber(string value) =>
        int.TryParse(value, out var number) && number is >= 0 and <= 9999;

    private static bool IsValidPassword(string value) =>
        !string.IsNullOrWhiteSpace(value);

    private void EnsureMigratedLocked()
    {
        var accountsMap = new Dictionary<string, UserAccount>(StringComparer.OrdinalIgnoreCase);

        if (File.Exists(FilePath))
        {
            foreach (var acc in LoadFromPathLocked(FilePath))
            {
                var key = !string.IsNullOrWhiteSpace(acc.Id) ? acc.Id : acc.Login;
                if (!string.IsNullOrWhiteSpace(key))
                {
                    accountsMap[key] = acc;
                }
            }
        }

        foreach (var legacyPath in LegacyPaths)
        {
            if (File.Exists(legacyPath))
            {
                foreach (var acc in LoadFromPathLocked(legacyPath))
                {
                    var key = !string.IsNullOrWhiteSpace(acc.Id) ? acc.Id : acc.Login;
                    if (!string.IsNullOrWhiteSpace(key) && !accountsMap.ContainsKey(key))
                    {
                        accountsMap[key] = acc;
                    }
                    else if (!string.IsNullOrWhiteSpace(acc.Login) && !accountsMap.Values.Any(a => string.Equals(a.Login, acc.Login, StringComparison.OrdinalIgnoreCase)))
                    {
                        accountsMap[acc.Id ?? Guid.NewGuid().ToString("N")] = acc;
                    }
                }
            }
        }

        var list = accountsMap.Values.ToList();
        var changed = NormalizeAndMigratePasswords(list);
        if (changed || !File.Exists(FilePath) || list.Count > 0)
        {
            SaveLocked(list);
        }
    }

    private bool NormalizeAndMigratePasswords(List<UserAccount> accounts)
    {
        var changed = false;
        foreach (var account in accounts)
        {
            if (string.IsNullOrWhiteSpace(account.Id))
            {
                account.Id = Guid.NewGuid().ToString("N");
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(account.Login))
            {
                account.Login = account.DriverName;
                changed = true;
            }

            account.LeagueId = NormalizeScope(account.LeagueId, "vrs");
            account.SeasonId = NormalizeScope(account.SeasonId, "default");

            if (string.IsNullOrEmpty(account.TeamHubToken))
            {
                account.TeamHubToken = GenerateTeamHubToken();
                changed = true;
                // Faza 10: without this, a token backfilled here never reaches Supabase —
                // TeamHubToken was never in the push/pull column list until now, so any
                // account migrated before this enqueue existed silently kept its token local.
                _outbox.Enqueue(SyncEntityType.Account, account.Id, SyncChangeKind.Upsert);
            }

            if (!string.IsNullOrEmpty(account.LegacyPassword))
            {
                var hash = PasswordHasher.Hash(account.LegacyPassword);
                account.PasswordHash = hash.Hash;
                account.PasswordSalt = hash.Salt;
                account.PasswordIterations = hash.Iterations;
                account.LegacyPassword = null;
                changed = true;
            }
        }
        return changed;
    }

    private static string NormalizeScope(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();

    private static string GenerateTeamHubToken() =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    private static List<UserAccount> LoadFromPathLocked(string path)
    {
        if (!File.Exists(path))
        {
            return new List<UserAccount>();
        }

        try
        {
            var json = File.ReadAllText(path);
            var accounts = JsonSerializer.Deserialize<List<UserAccount>>(json, s_jsonOptions)
                ?? new List<UserAccount>();
            foreach (var account in accounts)
            {
                account.Id ??= Guid.NewGuid().ToString("N");
                account.DriverName ??= string.Empty;
                account.Login ??= string.IsNullOrWhiteSpace(account.DriverName) ? "driver" : account.DriverName;
                account.DriverNumber ??= string.Empty;
                account.Team ??= string.Empty;
                account.Affiliation ??= string.Empty;
                account.LicenseCategory ??= string.Empty;
                account.AdministrativeNote ??= string.Empty;
                account.PasswordHash ??= string.Empty;
                account.PasswordSalt ??= string.Empty;
                account.LeagueId ??= "vrs";
                account.SeasonId ??= "default";
                account.ChangeHistory ??= new List<AccountChangeEntry>();
            }
            return accounts;
        }
        catch (JsonException)
        {
            // Same self-healing pattern as LeagueProfileStore/IncidentReportStore: quarantine
            // the corrupt file instead of leaving every login blocked by an unhandled throw.
            var invalidPath = path + $".invalid-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Move(path, invalidPath, overwrite: false);
            return new List<UserAccount>();
        }
    }

    private void SaveLocked(IEnumerable<UserAccount> accounts)
    {
        var directory = Path.GetDirectoryName(FilePath)
            ?? throw new InvalidOperationException("Nieprawidłowa ścieżka magazynu kont.");
        Directory.CreateDirectory(directory);

        var sanitized = accounts.Select(a =>
        {
            a.LegacyPassword = null;
            return a;
        }).ToList();
        var json = JsonSerializer.Serialize(sanitized, s_jsonOptions);
        var temporaryPath = FilePath + ".tmp";
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, FilePath, true);

        // Legacy paths are read-only migration inputs. Mirroring credentials back into an
        // executable or publish directory would put users.json inside release packages and
        // could expose password hashes, salts and Team Hub tokens.
    }
}

public enum AuthenticationFailure
{
    None,
    InvalidCredentials,
    AccountNotFound,
    AccountInactive
}

public sealed record AuthenticationResult(
    bool IsSuccess,
    UserAccount? Account,
    AuthenticationFailure Failure)
{
    public static AuthenticationResult Succeeded(UserAccount account) =>
        new(true, account, AuthenticationFailure.None);

    public static AuthenticationResult Failed(AuthenticationFailure failure) =>
        new(false, null, failure);
}

public sealed record AccountOperationResult(bool IsSuccess, string Error, UserAccount? Account)
{
    public static AccountOperationResult Succeeded(UserAccount? account = null) =>
        new(true, string.Empty, account);

    public static AccountOperationResult Failed(string error) =>
        new(false, error, null);
}
