using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using VRS.RaceControl.Shared.Models;

public sealed record DurableIncidentState(long Generation, long Revision,
    IReadOnlyList<IncidentReport> Reports);

public sealed record DurableIncidentResult(string Result, long Generation, long Revision,
    IncidentReport? Report)
{
    public bool Applied => Result == "applied";
    public bool Duplicate => Result == "duplicate";
    public bool Conflict => Result == "conflict";
}

public interface IDurableIncidentRepository
{
    bool SupportsRuleReports => false;
    Task<DurableIncidentResult> ApplyRuleReportAsync(string sessionId, string clockEpoch, IncidentReport report, CancellationToken token)
        => throw new NotSupportedException("Telemetry report persistence is unavailable.");
    bool IsEnabled { get; }
    Task<DurableIncidentState?> LoadAsync(string sessionId, CancellationToken cancellationToken);
    Task<DurableIncidentResult> ApplyAsync(string sessionId, string actorId, Guid operationId,
        long expectedGeneration, long expectedRevision, string caseId, string kind,
        JsonElement payload, CancellationToken cancellationToken);
}

public sealed class DisabledDurableIncidentRepository : IDurableIncidentRepository
{
    public static DisabledDurableIncidentRepository Instance { get; } = new();
    public bool IsEnabled => false;
    public Task<DurableIncidentState?> LoadAsync(string sessionId, CancellationToken cancellationToken)
        => Task.FromResult<DurableIncidentState?>(null);
    public Task<DurableIncidentResult> ApplyAsync(string sessionId, string actorId, Guid operationId,
        long expectedGeneration, long expectedRevision, string caseId, string kind,
        JsonElement payload, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Durable incident storage is unavailable.");
}

public sealed class SupabaseDurableIncidentRepository : IDurableIncidentRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly Uri _url;
    private readonly string _key;
    private readonly HttpClient _http;

    public SupabaseDurableIncidentRepository(Uri url, string key, HttpClient? http = null)
    {
        _url = new Uri(url.AbsoluteUri.TrimEnd('/') + "/");
        _key = key;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    }

    public bool IsEnabled => true;
    public bool SupportsRuleReports => true;
    public async Task<DurableIncidentResult> ApplyRuleReportAsync(string sessionId, string clockEpoch, IncidentReport report, CancellationToken token)
    {
        using var request = Request("rest/v1/rpc/apply_rc_rule_report", new
        { p_session_id = RequiredId(sessionId), p_clock_epoch = clockEpoch, p_report = report });
        using var response = await _http.SendAsync(request, token);
        await EnsureSuccessAsync(response, token);
        var rows = await response.Content.ReadFromJsonAsync<WriteRow[]>(Json, token) ?? [];
        var row = rows.SingleOrDefault() ?? throw new InvalidDataException("Missing rule report result.");
        return new(row.Result, row.Generation, row.Revision,
            row.Report is { ValueKind: JsonValueKind.Object } value ? value.Deserialize<IncidentReport>(Json) : null);
    }

    public static IDurableIncidentRepository FromEnvironment(HttpClient? http = null)
    {
        var key = Environment.GetEnvironmentVariable("SUPABASE_SECRET_KEY")
            ?? Environment.GetEnvironmentVariable("SUPABASE_SERVICE_ROLE_KEY");
        return Uri.TryCreate(Environment.GetEnvironmentVariable("SUPABASE_URL"), UriKind.Absolute,
                   out var url) && !string.IsNullOrWhiteSpace(key)
            ? new SupabaseDurableIncidentRepository(url, key, http)
            : DisabledDurableIncidentRepository.Instance;
    }

    public async Task<DurableIncidentState?> LoadAsync(string sessionId, CancellationToken cancellationToken)
    {
        using var request = Request("rest/v1/rpc/read_rc_incidents", new
        {
            p_session_id = RequiredId(sessionId)
        });
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var rows = await response.Content.ReadFromJsonAsync<ReadRow[]>(Json, cancellationToken) ?? [];
        var row = rows.SingleOrDefault();
        return row == null ? null : new DurableIncidentState(row.Generation, row.Revision,
            row.Reports.Deserialize<List<IncidentReport>>(Json) ?? []);
    }

    public async Task<DurableIncidentResult> ApplyAsync(string sessionId, string actorId,
        Guid operationId, long expectedGeneration, long expectedRevision, string caseId,
        string kind, JsonElement payload, CancellationToken cancellationToken)
    {
        using var request = Request("rest/v1/rpc/apply_rc_incident_case", new
        {
            p_session_id = RequiredId(sessionId),
            p_actor = RequiredId(actorId),
            p_operation_id = operationId,
            p_expected_generation = expectedGeneration,
            p_expected_revision = expectedRevision,
            p_case_id = caseId,
            p_kind = kind,
            p_payload = payload
        });
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var rows = await response.Content.ReadFromJsonAsync<WriteRow[]>(Json, cancellationToken) ?? [];
        var row = rows.SingleOrDefault()
            ?? throw new InvalidDataException("Missing durable incident result.");
        return new DurableIncidentResult(row.Result, row.Generation, row.Revision,
            row.Report is { ValueKind: JsonValueKind.Object } report
                ? report.Deserialize<IncidentReport>(Json) : null);
    }

    private HttpRequestMessage Request(string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_url, path));
        request.Headers.TryAddWithoutValidation("apikey", _key);
        if (_key.Count(character => character == '.') == 2)
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_key}");
        request.Content = JsonContent.Create(body, options: Json);
        return request;
    }

    private static Guid RequiredId(string value) => Guid.TryParse(value, out var id)
        ? id : throw new ArgumentException("A UUID session or actor ID is required.");

    private static async Task EnsureSuccessAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException($"Durable incident store returned {(int)response.StatusCode}: "
            + detail[..Math.Min(detail.Length, 300)]);
    }

    private sealed record ReadRow(long Generation, long Revision, JsonElement Reports);
    private sealed record WriteRow(string Result, long Generation, long Revision,
        JsonElement? Report);
}
