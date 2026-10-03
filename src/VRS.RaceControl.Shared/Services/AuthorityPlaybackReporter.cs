using System.Threading.Channels;
using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

public static class AuthorityPlaybackReporter
{
    public static async Task RunAsync(ChannelReader<AuthorityPlaybackReceipt> reader,
        Func<AuthorityPlaybackReceipt, Task> send, Action<Exception> failed, CancellationToken token)
    {
        try
        {
            await foreach (var receipt in reader.ReadAllAsync(token))
            {
                try { await send(receipt); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception ex) { failed(ex); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
