using Ariva.Core.Queueing;

namespace Ariva.UnitTests.Queueing;

/// <summary>Snapshots as earlier builds wrote them, for the tests that restore an older zone snapshot version.</summary>
internal static class SnapshotVersions
{
    /// <summary>
    /// ARV-231: the snapshot as a build before zone snapshot version 12 wrote it: no device on the buffered events or the pending
    /// samples, the eleven step counters before DeviceShareFull, and no full-buffer minutes.
    /// </summary>
    public static ZoneProcessorState WrittenBefore12(this ZoneProcessorState state) => state with
    {
        FullBufferMinutes = [],
        Engine = state.Engine with
        {
            Buffer = [.. state.Engine.Buffer.Select(b => b with { Device = null })],
            Runs = [.. state.Engine.Runs.Select(r => r with { PendingDevice = null })],
            Counters = [.. state.Engine.Counters.Take(11)],
            FullBufferMinutes = []
        }
    };
}
