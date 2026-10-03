using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using VRS.RaceControl.Shared.Models;

public sealed record DurableRelaySession(
    string SessionId,
    string OwnerUserId,
    long Generation,
    long Revision,
    JsonElement Snapshot,
    string? MainUserId = null,
    string AuthorityMode = "legacy");

public sealed record DurableSessionOperationResult(
    string Result,
    long Generation,
    long Revision)
{
    public bool IsApplied => string.Equals(Result, "applied", StringComparison.Ordinal);
    public bool IsDuplicate => string.Equals(Result, "duplicate", StringComparison.Ordinal);
    public bool IsConflict => string.Equals(Result, "conflict", StringComparison.Ordinal);
}

public sealed record DurableOwnershipResult(long Generation, long Revision);

public interface IDurableSessionRepository
{
    bool IsEnabled { get; }
    string Status { get; }
    Task<DurableRelaySession?> LoadAsync(string sessionId, CancellationToken cancellationToken);
    Task<DurableSessionOperationResult> ApplyOperationAsync(
        string authenticatedActorId,
        SessionOperationEnvelope envelope,
        CancellationToken cancellationToken);
    Task<DurableOwnershipResult> TakeOwnershipAsync(
        string sessionId,
        string authenticatedActorId,
        long expectedGeneration,
        string reason,
        CancellationToken cancellationToken);
}

public sealed class DisabledDurableSessionRepository : IDurableSessionRepository
{
    public static DisabledDurableSessionRepository Instance { get; } = new();
    public bool IsEnabled => false;
    public string Status => "disabled";
    public Task<DurableRelaySession?> LoadAsync(string sessionId, CancellationToken cancellationToken) =>
        Task.FromResult<DurableRelaySession?>(null);

    public Task<DurableSessionOperationResult> ApplyOperationAsync(
        string authenticatedActorId,
        SessionOperationEnvelope envelope,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Durable session storage is not configured.");

    public Task<DurableOwnershipResult> TakeOwnershipAsync(
        string sessionId,
        string authenticatedActorId,
        long expectedGeneration,
        string reason,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Durable session storage is not configured.");
}

public sealed class SupabaseDurableSessionRepository : IDurableSessionRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Uri _projectUri;
    private readonly string _secretKey;
    private readonly HttpClient _httpClient;

    public SupabaseDurableSessionRepository(Uri projectUri, string secretKey, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(projectUri);
        if (projectUri.Scheme is not ("https" or "http"))
            throw new ArgumentException("Supabase URL must use HTTP or HTTPS.", nameof(projectUri));
        if (string.IsNullOrWhiteSpace(secretKey))
            throw new ArgumentException("Supabase server secret is required.", nameof(secretKey));

        _projectUri = new Uri(projectUri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        _secretKey = secretKey.Trim();
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    }

    public bool IsEnabled => true;
    public string Status => "supabase";

    public static IDurableSessionRepository FromEnvironment(HttpClient? httpClient = null)
    {
        var url = Environment.GetEnvironmentVariable("SUPABASE_URL");
        var key = Environment.GetEnvironmentVariable("SUPABASE_SECRET_KEY")
            ?? Environment.GetEnvironmentVariable("SUPABASE_SERVICE_ROLE_KEY");
        return Uri.TryCreate(url, UriKind.Absolute, out var projectUri)
            && !string.IsNullOrWhiteSpace(key)
            ? new SupabaseDurableSessionRepository(projectUri, key, httpClient)
            : DisabledDurableSessionRepository.Instance;
    }

    public async Task<DurableRelaySession?> LoadAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var normalizedSessionId = RequiredUuid(sessionId, nameof(sessionId));
        using var request = CreateRequest(HttpMethod.Get,
            $"rest/v1/rc_session_state?session_id=eq.{normalizedSessionId:D}" +
            "&select=session_id,owner_user_id,generation,revision,snapshot&limit=1");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var rows = await response.Content.ReadFromJsonAsync<SessionStateRow[]>(JsonOptions, cancellationToken)
            ?? [];
        var row = rows.FirstOrDefault();
        if (row == null) return null;
        using var mainRequest = CreateRequest(HttpMethod.Get,
            $"rest/v1/rc_sessions?id=eq.{normalizedSessionId:D}&select=created_by,authority_mode&limit=1");
        using var mainResponse = await _httpClient.SendAsync(mainRequest, cancellationToken);
        await EnsureSuccessAsync(mainResponse, cancellationToken);
        var mainRows = await mainResponse.Content.ReadFromJsonAsync<MainHostRow[]>(JsonOptions,
            cancellationToken) ?? [];
        var main = mainRows.SingleOrDefault()
            ?? throw new DurableSessionStoreException("Session creator is missing.");
        return new DurableRelaySession(row.SessionId.ToString("D"), row.OwnerUserId.ToString("D"),
            row.Generation, row.Revision, row.Snapshot.Clone(), main.CreatedBy.ToString("D"), main.AuthorityMode ?? "legacy");
    }

    public async Task<DurableSessionOperationResult> ApplyOperationAsync(
        string authenticatedActorId,
        SessionOperationEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var operation = envelope.Operation;
        var body = new
        {
            p_session_id = RequiredUuid(operation.ControlSessionId, nameof(operation.ControlSessionId)),
            p_actor = RequiredUuid(authenticatedActorId, nameof(authenticatedActorId)),
            p_operation_id = operation.OperationId,
            p_expected_generation = operation.Generation,
            p_expected_revision = operation.ExpectedRevision,
            p_operation_kind = operation.Kind,
            p_snapshot = envelope.Snapshot
        };
        using var request = CreateRequest(HttpMethod.Post, "rest/v1/rpc/apply_rc_session_operation");
        request.Content = JsonContent.Create(body, options: JsonOptions);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var rows = await response.Content.ReadFromJsonAsync<OperationResultRow[]>(JsonOptions, cancellationToken)
            ?? [];
        var row = rows.SingleOrDefault()
            ?? throw new DurableSessionStoreException("Supabase returned no session-operation result.");
        return new DurableSessionOperationResult(row.Result, row.Generation, row.Revision);
    }

    public async Task<DurableOwnershipResult> TakeOwnershipAsync(
        string sessionId,
        string authenticatedActorId,
        long expectedGeneration,
        string reason,
        CancellationToken cancellationToken)
    {
        var body = new
        {
            p_session_id = RequiredUuid(sessionId, nameof(sessionId)),
            p_actor = RequiredUuid(authenticatedActorId, nameof(authenticatedActorId)),
            p_expected_generation = expectedGeneration,
            p_reason = (reason ?? string.Empty).Trim()[..Math.Min((reason ?? string.Empty).Trim().Length, 500)]
        };
        using var request = CreateRequest(HttpMethod.Post, "rest/v1/rpc/take_rc_session_ownership");
        request.Content = JsonContent.Create(body, options: JsonOptions);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var rows = await response.Content.ReadFromJsonAsync<OwnershipResultRow[]>(JsonOptions, cancellationToken)
            ?? [];
        var row = rows.SingleOrDefault()
            ?? throw new DurableSessionStoreException("Supabase returned no ownership result.");
        return new DurableOwnershipResult(row.Generation, row.Revision);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string relativeUri)
    {
        var request = new HttpRequestMessage(method, new Uri(_projectUri, relativeUri));
        request.Headers.TryAddWithoutValidation("apikey", _secretKey);
        // Legacy service_role keys are JWTs and must also be sent as the bearer token.
        // New sb_secret keys are authenticated by the API gateway through `apikey`.
        if (_secretKey.Count(character => character == '.') == 2)
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_secretKey}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        return request;
    }

    private static Guid RequiredUuid(string value, string parameterName) =>
        Guid.TryParse(value, out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw new ArgumentException("A non-empty UUID is required.", parameterName);

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        if (detail.Length > 512) detail = detail[..512];
        throw new DurableSessionStoreException(
            $"Supabase session storage returned HTTP {(int)response.StatusCode}: {detail}");
    }

    private sealed record SessionStateRow(
        [property: JsonPropertyName("session_id")] Guid SessionId,
        [property: JsonPropertyName("owner_user_id")] Guid OwnerUserId,
        [property: JsonPropertyName("generation")] long Generation,
        [property: JsonPropertyName("revision")] long Revision,
        [property: JsonPropertyName("snapshot")] JsonElement Snapshot);

    private sealed record MainHostRow(
        [property: JsonPropertyName("created_by")] Guid CreatedBy,
        [property: JsonPropertyName("authority_mode")] string? AuthorityMode);

    private sealed record OperationResultRow(
        [property: JsonPropertyName("result")] string Result,
        [property: JsonPropertyName("generation")] long Generation,
        [property: JsonPropertyName("revision")] long Revision);

    private sealed record OwnershipResultRow(
        [property: JsonPropertyName("generation")] long Generation,
        [property: JsonPropertyName("revision")] long Revision);
}

public sealed class DurableSessionStoreException(string message) : Exception(message);
