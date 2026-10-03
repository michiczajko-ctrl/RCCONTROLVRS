using VRS.RaceControl.Shared.Models;
using VRS.RaceControl.Shared.Protocol;

public sealed partial class RelaySession
{
    private async Task HandleTrackDefinitionRequestAsync(RelayClient client, ProtocolMessage message, CancellationToken token)
    {
        if (client.Role == "driver" || (client.Role != "host" && !CanReceiveHostState(client))) return;
        var input = message.GetPayload<TrackDefinitionRequest>();
        if (input == null || input.RequestId == Guid.Empty || input.Offset is < 0 or > 1000
            || input.Checksum?.Length != 64 || !input.Checksum.All(Uri.IsHexDigit)) return;
        TrackDefinitionPage page;
        try
        {
            page = _deliveryStore?.SupportsTrackDefinitions == true
                ? await _deliveryStore.ReadTrackDefinitionAsync(Code, input, token)
                : new(input.RequestId, input.Checksum, null, null, "Shared track storage unavailable.");
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidDataException or TaskCanceledException)
        { page = new(input.RequestId, input.Checksum, null, null, "Track profile temporarily unavailable. Retry."); }
        var response = ProtocolMessage.Create(MessageType.TrackDefinitionPage, page);
        StampRelay(response, client.Id); await TrySendAsync(client, response, token);
    }
}
