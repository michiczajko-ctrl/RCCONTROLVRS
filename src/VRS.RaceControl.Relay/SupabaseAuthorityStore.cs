using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using VRS.RaceControl.Shared.Models;

/// <summary>Server-only PostgreSQL compare-and-exchange authority storage.</summary>
public sealed partial class SupabaseAuthorityStore : IAuthorityStore
{
    public const string RequiredSchemaVersion = "r2-atomic-incident-penalty-v1";
    private readonly SemaphoreSlim _schemaProbeGate = new(1, 1);
    private long _lastSchemaProbe;
    private volatile bool _schemaReady;
    public bool SchemaReady => _schemaReady;
    public async Task<bool> CheckSchemaAsync(CancellationToken token)
    {
        await _schemaProbeGate.WaitAsync(token);
        try
        {
            if (_lastSchemaProbe > 0 && System.Diagnostics.Stopwatch.GetElapsedTime(_lastSchemaProbe) < TimeSpan.FromSeconds(5)) return _schemaReady;
            try
            {
                using var request = Request(HttpMethod.Post, "rest/v1/rpc/rc_authority_schema_version");
                request.Content = JsonContent.Create(new { });
                using var response = await _http.SendAsync(request, token);
                _schemaReady = response.IsSuccessStatusCode
                    && await response.Content.ReadFromJsonAsync<string>(Json, token) == RequiredSchemaVersion;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            { _schemaReady = false; }
            _lastSchemaProbe = System.Diagnostics.Stopwatch.GetTimestamp(); return _schemaReady;
        }
        finally { _schemaProbeGate.Release(); }
    }
    public bool SupportsOperationHistory => true;
    public bool SupportsPenaltyHistory => true;
    public async Task<bool> HasOperationAsync(string sessionId, Guid operationId, CancellationToken token)
    {
        using var request = Request(HttpMethod.Get, $"rest/v1/rc_authority_operations?session_id=eq.{Guid.Parse(sessionId):D}&operation_id=eq.{operationId:D}&select=operation_id&limit=1");
        using var response = await _http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Authority operation lookup failed.", null, response.StatusCode);
        var rows = await response.Content.ReadFromJsonAsync<JsonElement[]>(Json, token) ?? [];
        return rows.Length == 1;
    }
    public static SupabaseAuthorityStore? FromEnvironment()
    {
        var url = Environment.GetEnvironmentVariable("SUPABASE_URL");
        var key = Environment.GetEnvironmentVariable("SUPABASE_SECRET_KEY")
            ?? Environment.GetEnvironmentVariable("SUPABASE_SERVICE_ROLE_KEY");
        return Uri.TryCreate(url, UriKind.Absolute, out var endpoint) && !string.IsNullOrWhiteSpace(key)
            ? new(endpoint, key, relayWriterLease: Environment.GetEnvironmentVariable("VRS_RELAY_WRITER_LEASE_ENABLED") == "true") : null;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };

    public async Task<AuthorityPenaltyPage> ReadPenaltyPageAsync(string sessionId, string userId,
        AuthorityPenaltyCursor? cursor, CancellationToken token)
    {
        if (cursor?.IsValid == false) throw new ArgumentException("Invalid penalty cursor.", nameof(cursor));
        var path = $"rest/v1/rc_authority_penalties?session_id=eq.{Guid.Parse(sessionId):D}&account_user_id=eq.{Guid.Parse(userId):D}&select=payload,created_at,penalty_id&order=created_at.desc,penalty_id.desc&limit=21";
        if (cursor != null)
        {
            var time = cursor.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            path += "&or=" + Uri.EscapeDataString($"(created_at.lt.{time},and(created_at.eq.{time},penalty_id.lt.{cursor.PenaltyId:D}))");
        }
        using var request = Request(HttpMethod.Get, path);
        using var response = await _http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Penalty history load failed.", null, response.StatusCode);
        var rows = await response.Content.ReadFromJsonAsync<PenaltyRow[]>(Json, token) ?? [];
        var shown = rows.Take(20).ToArray();
        foreach (var row in shown) { row.Payload.Note = ""; row.Payload.IncidentCommit = null; }
        return new(shown.Select(row => row.Payload).ToArray(), rows.Length > 20
            ? new(shown[^1].CreatedAt, shown[^1].PenaltyId) : null);
    }
    private readonly Uri _url;
    public bool SupportsDeliveryHistory => true;
    public bool SupportsTelemetryEvidence => true;
    public bool SupportsTrackDefinitions => true;
    public bool SupportsAtomicIncidentPenalty => true;
    public async Task WriteTrackDefinitionAsync(string sessionId, string clockEpoch, TrackDefinition definition, CancellationToken token)
    {
        using var request = Request(HttpMethod.Post, "rest/v1/rpc/write_rc_track_definition");
        request.Content = JsonContent.Create(new { p_session_id = Guid.Parse(sessionId), p_clock_epoch = clockEpoch,
            p_checksum = VRS.RaceControl.Shared.Services.TrackDefinitionCodec.Checksum(definition), p_definition = definition }, options: Json);
        using var response = await _http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Track definition write failed.", null, response.StatusCode);
    }
    public async Task<TrackDefinitionPage> ReadTrackDefinitionAsync(string sessionId, TrackDefinitionRequest input, CancellationToken token)
    {
        using var request = Request(HttpMethod.Post, "rest/v1/rpc/read_rc_track_definition");
        request.Content = JsonContent.Create(new { p_session_id = Guid.Parse(sessionId), p_checksum = input.Checksum,
            p_offset = input.Offset, p_request_id = input.RequestId }, options: Json);
        using var response = await _http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Track definition read failed.", null, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<TrackDefinitionPage>(Json, token)
            ?? throw new InvalidDataException("Missing track definition page.");
    }
    public async Task RecoverEvidenceAsync(string sessionId, string clockEpoch, CancellationToken token)
    {
        using var request = Request(HttpMethod.Post, "rest/v1/rpc/interrupt_rc_telemetry_evidence");
        request.Content = JsonContent.Create(new { p_session_id = Guid.Parse(sessionId), p_clock_epoch = clockEpoch });
        using var response = await _http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Evidence recovery failed.", null, response.StatusCode);
    }
    public async Task WriteEvidenceAsync(string sessionId, string clockEpoch, TelemetryEvidenceClip clip, CancellationToken token)
    {
        using var request = Request(HttpMethod.Post, "rest/v1/rpc/write_rc_telemetry_evidence");
        request.Content = JsonContent.Create(new { p_session_id = Guid.Parse(sessionId), p_clock_epoch = clockEpoch, p_clip = clip }, options: Json);
        using var response = await _http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Evidence write failed.", null, response.StatusCode);
    }
    public async Task<TelemetryEvidencePage> ReadEvidenceAsync(string sessionId, TelemetryEvidenceRequest page, CancellationToken token)
    {
        using var request = Request(HttpMethod.Post, "rest/v1/rpc/read_rc_telemetry_evidence");
        request.Content = JsonContent.Create(new { p_session_id = Guid.Parse(sessionId), p_incident_id = page.IncidentId,
            p_offset = page.Offset, p_request_id = page.RequestId }, options: Json);
        using var response = await _http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Evidence read failed.", null, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<TelemetryEvidencePage>(Json, token)
            ?? throw new InvalidDataException("Missing evidence page.");
    }
    public async Task<bool> RecordEvidenceExportAsync(string sessionId, string actorId, string incidentId, string checksum, CancellationToken token)
    {
        using var request = Request(HttpMethod.Post, "rest/v1/rpc/record_rc_evidence_export");
        request.Content = JsonContent.Create(new { p_session_id = Guid.Parse(sessionId), p_actor = Guid.Parse(actorId),
            p_incident_id = incidentId, p_checksum = checksum }, options: Json);
        using var response = await _http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Evidence export registration failed.", null, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<bool>(Json, token);
    }
    public async Task<int> PurgeExportedEvidenceAsync(CancellationToken token)
    {
        using var request = Request(HttpMethod.Post, "rest/v1/rpc/purge_rc_exported_evidence");
        request.Content = JsonContent.Create(new { });
        using var response = await _http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Evidence retention failed.", null, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<int>(Json, token);
    }
    public async Task WriteDeliveryAsync(string sessionId, AuthorityDelivery delivery, CancellationToken token)
    {
        using var request = Request(HttpMethod.Post, "rest/v1/rpc/record_rc_authority_delivery");
        request.Content = JsonContent.Create(new { p_session_id = Guid.Parse(sessionId), p_delivery = delivery }, options: Json);
        using var response = await _http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Delivery history write failed.", null, response.StatusCode);
    }
    private readonly string _secret;
    private readonly HttpClient _http;

    public SupabaseAuthorityStore(Uri url, string secret, HttpClient? http = null, bool relayWriterLease = false)
    {
        if (url.Scheme != "https" && !url.IsLoopback)
            throw new ArgumentException("Server credentials require HTTPS.", nameof(url));
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        _url = new(url.AbsoluteUri.TrimEnd('/') + "/");
        _secret = secret;
        _http = http ?? new() { Timeout = TimeSpan.FromSeconds(4) };
        SupportsRelayWriterLease = relayWriterLease;
    }

    public async Task<AuthorityStoredState?> LoadAsync(string sessionId, CancellationToken token)
    {
        var id = Guid.Parse(sessionId);
        using var request = Request(HttpMethod.Get, $"rest/v1/rc_session_authority?session_id=eq.{id:D}&select=document&limit=1");
        using var response = await _http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Authority load failed.", null, response.StatusCode);
        var rows = await response.Content.ReadFromJsonAsync<Row[]>(Json, token) ?? [];
        return rows.SingleOrDefault()?.Document;
    }

    public async Task<bool> CompareExchangeAsync(AuthorityStoredState? previous, AuthorityStoredState next,
        string actorId, bool system, CancellationToken token)
    {
        using var request = Request(HttpMethod.Post, "rest/v1/rpc/compare_exchange_rc_authority");
        request.Content = JsonContent.Create(new { p_session_id = Guid.Parse(next.State.SessionId),
            p_actor = string.IsNullOrEmpty(actorId) ? (Guid?)null : Guid.Parse(actorId),
            p_system = system, p_expected_generation = previous?.State.Generation,
            p_expected_revision = previous?.State.Revision, p_expected_clock_epoch = previous?.State.ClockEpoch,
            p_document = next }, options: Json);
        using var response = await _http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Authority commit failed.", null, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<bool>(Json, token);
    }

    public async Task<AuthorityOperatorApproval[]> LoadApprovalsAsync(string sessionId, CancellationToken token)
    {
        using var request = Request(HttpMethod.Get, $"rest/v1/rc_authority_operator_approvals?session_id=eq.{Guid.Parse(sessionId):D}&select=user_id,connection_role,permissions");
        using var response = await _http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Authority approvals load failed.", null, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<AuthorityOperatorApproval[]>(Json, token) ?? [];
    }

    public async Task SaveApprovalAsync(string sessionId, string actor, string target, string role,
        int permissions, bool approved, CancellationToken token)
    {
        using var request = Request(HttpMethod.Post, "rest/v1/rpc/set_rc_authority_operator");
        request.Content = JsonContent.Create(new { p_session_id = Guid.Parse(sessionId), p_actor = Guid.Parse(actor),
            p_target = Guid.Parse(target), p_role = role, p_permissions = permissions, p_approved = approved });
        using var response = await _http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Authority approval write failed.", null, response.StatusCode);
    }

    public async Task PrepareSessionAsync(string sessionId, string authenticatedCreator, CancellationToken token)
    {
        using var request = Request(HttpMethod.Post, "rest/v1/rpc/start_rc_authority_session");
        request.Content = JsonContent.Create(new { p_session_id = Guid.Parse(sessionId), p_actor = Guid.Parse(authenticatedCreator) });
        using var response = await _http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Authority session start failed.", null, response.StatusCode);
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(_url, path));
        request.Headers.Add("apikey", _secret);
        if (_secret.StartsWith("eyJ", StringComparison.Ordinal))
            request.Headers.Authorization = new("Bearer", _secret);
        return request;
    }
    private sealed record Row(AuthorityStoredState Document);
    private sealed record PenaltyRow(PenaltyPayload Payload,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
        [property: JsonPropertyName("penalty_id")] Guid PenaltyId);
}

public sealed record AuthorityOperatorApproval(
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("connection_role")] string Role,
    [property: JsonPropertyName("permissions")] int Permissions);
