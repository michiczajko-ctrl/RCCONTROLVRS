using System.Net.Http.Json;

public sealed partial class SupabaseAuthorityStore
{
    public bool SupportsRelayWriterLease { get; }
    public Task<bool> AcquireRelayWriterAsync(string sessionId, string clockEpoch, CancellationToken token) =>
        WriteRelayLeaseAsync(sessionId, clockEpoch, true, token);
    public Task<bool> RenewRelayWriterAsync(string sessionId, string clockEpoch, CancellationToken token) =>
        WriteRelayLeaseAsync(sessionId, clockEpoch, false, token);
    private async Task<bool> WriteRelayLeaseAsync(string sessionId, string clockEpoch, bool acquire, CancellationToken token)
    {
        using var request = Request(HttpMethod.Post, "rest/v1/rpc/lease_rc_relay_writer");
        request.Content = JsonContent.Create(new { p_session_id = Guid.Parse(sessionId), p_clock_epoch = clockEpoch, p_acquire = acquire });
        using var response = await _http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Relay writer lease unavailable.", null, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<bool>(Json, token);
    }
}
