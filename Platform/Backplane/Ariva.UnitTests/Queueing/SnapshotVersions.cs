using Ariva.Core.Queueing;

namespace Ariva.UnitTests.Queueing;

/// <summary>Snapshots as earlier builds wrote them, for the tests that restore an older zone snapshot version.</summary>
internal static class SnapshotVersions
{
    /// <summary>
    /// ARV-231: the snapshot as a build before zone snapshot version 12 wrote it: no device on the buffered events or the pending
    /// samples, the eleven step counters before DeviceShareFull, and no full-buffer minutes.
    /// </summary>
    public static ZoneProcessorState WrittenBefore12(this ZoneProcessorState state)
    {
        var before13 = state.WrittenBefore13();
        return before13 with
        {
            FullBufferMinutes = [],
            Engine = before13.Engine with
            {
                Buffer = [.. before13.Engine.Buffer.Select(b => b with { Device = null })],
                Runs = [.. before13.Engine.Runs.Select(r => r with { PendingDevice = null })],
                Counters = [.. before13.Engine.Counters.Take(11)],
                FullBufferMinutes = []
            }
        };
    }

    /// <summary>
    /// ARV-239: the snapshot as a build before zone snapshot version 13 wrote it: no pending sample marked ahead of the clock. Only
    /// for states whose runs hold no sample ahead of the clock (that build buffered those samples instead).
    /// </summary>
    public static ZoneProcessorState WrittenBefore13(this ZoneProcessorState state)
    {
        var before14 = state.WrittenBefore14();
        return before14 with
        {
            Engine = before14.Engine with { Runs = [.. before14.Engine.Runs.Select(r => r with { PendingAhead = false })] }
        };
    }

    /// <summary>
    /// ARV-259e1: the snapshot as a build before zone snapshot version 14 wrote it: no lines without coverage and no recent spells.
    /// Only for states with no Unknown bin mark (nothing made one before that build).
    /// </summary>
    public static ZoneProcessorState WrittenBefore14(this ZoneProcessorState state) => state.WrittenBefore15() with { Coverage = [], RecentUncovered = [] };

    /// <summary>The state as a build before version 15 wrote it (ARV-259e5): no device's declared lines.</summary>
    public static ZoneProcessorState WrittenBefore15(this ZoneProcessorState state) => state with
    {
        Devices = [.. state.Devices.Select(d => d with { CoveredLines = null })]
    };
}
