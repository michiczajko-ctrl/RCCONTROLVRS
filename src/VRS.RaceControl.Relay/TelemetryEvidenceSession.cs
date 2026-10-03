using System.Collections.Concurrent;
using VRS.RaceControl.Shared.Models;
using VRS.RaceControl.Shared.Protocol;
using VRS.RaceControl.Shared.Services;

public sealed partial class RelaySession
{
    private readonly TelemetryEvidenceBuffer _evidenceBuffer = new();
    private readonly ConcurrentDictionary<string, TelemetryEvidenceClip> _pendingEvidence = new();
    private TelemetryBatch? _ruleEvidenceSource;
    private volatile bool _evidenceStorageDegraded;
    private bool _needsEvidenceRecovery = true;
    public int PendingEvidenceClips => _pendingEvidence.Count;
    public bool EvidenceStorageDegraded => _evidenceStorageDegraded;

    private void RetainEvidence(IEnumerable<TelemetryEvidenceClip> clips)
    {
        foreach (var clip in clips) _pendingEvidence.AddOrUpdate(clip.Manifest.Id, clip,
            (_, old) => old.Manifest.State == "collecting" ? clip : old);
    }

    public async Task RunTelemetryEvidenceWriterAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                if (_authority == null || _deliveryStore?.SupportsTelemetryEvidence != true) return;
                if (_needsEvidenceRecovery)
                {
                    try { await _deliveryStore.RecoverEvidenceAsync(Code, _authority.ClockEpoch, token); _needsEvidenceRecovery = false; }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
                    { _evidenceStorageDegraded = true; continue; }
                }
                foreach (var clip in _pendingEvidence.Values.OrderBy(c => c.Manifest.IncidentAt).Take(16))
                {
                    try
                    {
                        await _deliveryStore.WriteEvidenceAsync(Code, _authority.ClockEpoch, clip, token);
                        ((ICollection<KeyValuePair<string, TelemetryEvidenceClip>>)_pendingEvidence).Remove(new(clip.Manifest.Id, clip));
                        _evidenceStorageDegraded = false;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
                    { _evidenceStorageDegraded = true; }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task HandleEvidenceRequestAsync(RelayClient client, ProtocolMessage message, CancellationToken token)
    {
        if (!CanReceiveIncidentState(client)) return;
        var request = message.GetPayload<TelemetryEvidenceRequest>();
        if (request == null || request.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(request.IncidentId) || request.IncidentId.Length > 80
            || request.Offset is < 0 or > 160
            || (request.ExportChecksum != null && (request.Offset != 0 || request.ExportChecksum.Length != 64 || !request.ExportChecksum.All(Uri.IsHexDigit)))) return;
        TelemetryEvidencePage page;
        try
        {
            page = _deliveryStore?.SupportsTelemetryEvidence == true
                ? await _deliveryStore.ReadEvidenceAsync(Code, request, token)
                : new(request.RequestId, null, [], null, "Evidence storage unavailable.");
            if (request.ExportChecksum != null && _deliveryStore != null)
                page = page with { ExportRecorded = await _deliveryStore.RecordEvidenceExportAsync(Code, client.UserId,
                    request.IncidentId, request.ExportChecksum, token) };
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
        { page = new(request.RequestId, null, [], null, "Evidence is temporarily unavailable. Retry."); }
        var response = ProtocolMessage.Create(MessageType.EvidencePage, page);
        StampRelay(response, client.Id);
        await TrySendAsync(client, response, token);
    }
}
