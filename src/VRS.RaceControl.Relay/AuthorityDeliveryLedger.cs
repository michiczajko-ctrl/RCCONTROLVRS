using System.Collections.Concurrent;
using System.Threading.Channels;
using VRS.RaceControl.Shared.Models;
using VRS.RaceControl.Shared.Protocol;

public sealed partial class RelaySession
{
    private readonly ConcurrentDictionary<(string Message, string Connection), AuthorityDelivery> _authorityDeliveries = new();
    private readonly Channel<AuthorityDelivery> _deliveryWrites = Channel.CreateBounded<AuthorityDelivery>(new BoundedChannelOptions(512)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private IAuthorityStore? _deliveryStore;
    private long _deliveryDrops;
    private volatile bool _deliveryPersistenceDegraded;
    public AuthorityDeliveryStatus DeliveryStatus => new(_authorityDeliveries.Values.OrderByDescending(d => d.SentAt).Take(256).ToArray(),
        _deliveryPersistenceDegraded, Interlocked.Read(ref _deliveryDrops));

    private void TrackAuthorityDelivery(RelayClient client, ProtocolMessage message)
    {
        if (_authority == null || message.AuthorityGeneration == null || message.Sequence == null || message.ClockEpoch == null) return;
        if (message.Type == MessageType.Flag && message.GetPayload<FlagPayload>()?.PanelState is { } panel)
            TrackPanelAudioDelivery(client, panel);
        var delivery = new AuthorityDelivery(message.Id, client.Id, client.UserId,
            message.AuthorityGeneration.Value, message.Sequence.Value, message.ClockEpoch, _authority.Now,
            AudioExpected: message.Type is MessageType.CustomFlag or MessageType.TextMessage or MessageType.Penalty
                || (message.Type == MessageType.Flag && message.GetPayload<FlagPayload>()?.PanelTransitionOwnsAudio != true));
        // A retry must retain its original timing and cannot overwrite an acknowledgement.
        if (_authorityDeliveries.TryAdd((message.Id, client.Id), delivery)) QueueDeliveryWrite(delivery);
        if (_authorityDeliveries.Count > 4096)
            foreach (var old in _authorityDeliveries.OrderBy(pair => pair.Value.SentAt).Take(512))
                _authorityDeliveries.TryRemove(old.Key, out _);
    }

    private void TrackPanelAudioDelivery(RelayClient client, SessionSnapshot snapshot)
        => TrackPanelAudioDelivery(client, snapshot.Panel);

    private void TrackPanelAudioDelivery(RelayClient client, RaceControlPanelStatePayload panel)
    {
        if (client.Role != "driver" || panel.Transition?.AudioAnnouncement is not { } audio) return;
        var delivery = new AuthorityDelivery(audio.EventId, client.Id, client.UserId, audio.Generation,
            audio.Revision, audio.ClockEpoch, _authority!.Now, AudioExpected: true);
        if (_authorityDeliveries.TryAdd((audio.EventId, client.Id), delivery)) QueueDeliveryWrite(delivery);
    }

    private void RecordAuthorityReceipt(RelayClient client, AuthorityReceipt receipt)
    {
        if (_authority == null || receipt.Result is not ("received" or "stateApplied" or "rejected")) return;
        var key = (receipt.MessageId, client.Id);
        while (_authorityDeliveries.TryGetValue(key, out var sent))
        {
        if (sent.UserId != client.UserId
            || sent.Generation != receipt.Generation || sent.Revision != receipt.Revision || sent.ClockEpoch != receipt.ClockEpoch
            || sent.Result != "pending") return;
        var now = _authority.Now;
        var updated = sent with { Result = receipt.Result, ReceivedAt = now,
            RoundTripMs = Math.Max(0, (now - sent.SentAt).TotalMilliseconds) };
        if (_authorityDeliveries.TryUpdate(key, updated, sent)) { QueueDeliveryWrite(updated); return; }
        }
    }

    private void RecordAuthorityPlayback(RelayClient client, AuthorityPlaybackReceipt receipt)
    {
        if (receipt.Status is not ("queued" or "started" or "completed" or "muted" or "failed" or "cancelled" or "expired")
            || receipt.Reason?.Length > 300) return;
        var key = (receipt.MessageId, client.Id);
        while (_authorityDeliveries.TryGetValue(key, out var sent))
        {
            if (!sent.AudioExpected || sent.UserId != client.UserId || sent.Generation != receipt.Generation || sent.Revision != receipt.Revision
                || sent.ClockEpoch != receipt.ClockEpoch || sent.AudioStatus is "completed" or "muted" or "failed" or "cancelled" or "expired"
                || (sent.AudioStatus == "started" && receipt.Status == "queued")) return;
            var updated = sent with { AudioStatus = receipt.Status, AudioReason = receipt.Reason };
            if (_authorityDeliveries.TryUpdate(key, updated, sent)) { QueueDeliveryWrite(updated); return; }
        }
    }

    private void QueueDeliveryWrite(AuthorityDelivery delivery)
    {
        if (_deliveryStore?.SupportsDeliveryHistory != true) return;
        if (!_deliveryWrites.Writer.TryWrite(delivery))
        { Interlocked.Increment(ref _deliveryDrops); _deliveryPersistenceDegraded = true; }
    }

    private async Task RunAuthorityDeliveryWriterAsync(CancellationToken token)
    {
        try
        {
            await foreach (var delivery in _deliveryWrites.Reader.ReadAllAsync(token))
            {
                var saved = false;
                for (var attempt = 0; attempt < 3 && !saved; attempt++)
                {
                    try { await _deliveryStore!.WriteDeliveryAsync(Code, delivery, token); saved = true; }
                    catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException)
                    { _deliveryPersistenceDegraded = true; await Task.Delay(200 * (attempt + 1), token); }
                }
                if (!saved) Interlocked.Increment(ref _deliveryDrops);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private double ObservedAuthorityDeliveryP99()
    {
        if (_authority == null) return 0;
        var samples = _authorityDeliveries.Values.Where(d => d.Result != "rejected" && d.RoundTripMs != null
            && _authority.Now - d.SentAt < TimeSpan.FromSeconds(60)).Select(d => d.RoundTripMs!.Value).Order().ToArray();
        return samples.Length == 0 ? 0 : samples[(int)Math.Ceiling(samples.Length * .99) - 1];
    }
}
