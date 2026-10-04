public sealed partial class RelaySession
{
    /// <summary>
    /// Always false. Only the HOST sets or records the track map, so a driver's track line is never passed on to it.
    /// Older CLIENTs may still send one; it is dropped here.
    /// </summary>
    public bool AdmitDriverTrackUpload(RelayClient client) => false;
}
