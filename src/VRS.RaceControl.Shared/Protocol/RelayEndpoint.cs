namespace VRS.RaceControl.Shared.Protocol;

/// <summary>
/// Normalizes a public relay address to the WebSocket endpoint consumed by
/// VRS applications. A Render HTTPS URL is accepted for convenience and is
/// converted to its secure WebSocket counterpart.
/// </summary>
public static class RelayEndpoint
{
    public static bool TryNormalize(string? value, out Uri? endpoint)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var source)
            || string.IsNullOrWhiteSpace(source.Host))
        {
            return false;
        }

        var scheme = source.Scheme.ToLowerInvariant() switch
        {
            "http" => "ws",
            "https" => "wss",
            "ws" or "wss" => source.Scheme.ToLowerInvariant(),
            _ => string.Empty
        };
        if (string.IsNullOrEmpty(scheme))
        {
            return false;
        }

        var builder = new UriBuilder(source)
        {
            Scheme = scheme,
            Path = source.AbsolutePath is "" or "/" ? "/vrs/" : source.AbsolutePath
        };
        endpoint = builder.Uri;
        return true;
    }
}
