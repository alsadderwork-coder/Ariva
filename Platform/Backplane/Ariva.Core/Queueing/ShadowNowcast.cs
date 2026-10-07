namespace Ariva.Core.Queueing;

/// <summary>
/// The shadow nowcast of a queue minute (ARV-117): the F8 nowcast computed without any AMAN input, stored beside the
/// published one in <c>queue_minute</c> for the pilot's ground-truth proof (the validation comparison, ARV-104f). It is
/// never shown to staff or passengers, never an alert input and never in a report or the live snapshot: it is written by
/// the stream and read only by that comparison. <see cref="Minutes"/> is null with a <see cref="NoService"/> reason, as
/// for the published nowcast.
/// </summary>
public sealed record ShadowNowcast(double? Minutes, NoServiceReason? NoService, bool Degraded)
{
    public static ShadowNowcast From(NowcastResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new ShadowNowcast(result.Minutes, result.NoService, result.Degraded);
    }
}

/// <summary>
/// The inputs of the published and the shadow nowcast of one queue minute (F8, ARV-117). Both start from the same
/// queue-side input (the queue length, the exits in the window, the window and the queue's own flags) and take a desk
/// term: the published nowcast the desks' term as the stream reads it (n_open with AMAN's sessions, c from AMAN's interval
/// statistics or transactions), the shadow its <see cref="DeskTerm.SensorOnly"/> part (n_open from the desks' zones alone,
/// no cycle time). The two inputs therefore differ only in n_open, c and the desk term's flag; the fast-track share and
/// the reject rate keep their defaults in both (the queue nowcast takes no AMAN reject rate). Pure.
/// </summary>
public static class ShadowNowcasts
{
    /// <summary>The queue-side input with a desk term's n_open, c and flag (no term: the exit term alone, as before).</summary>
    public static NowcastInput WithDesks(NowcastInput queue, DeskTerm desks)
    {
        ArgumentNullException.ThrowIfNull(queue);
        return queue with
        {
            OpenServers = desks?.OpenServers,
            CycleMinutes = desks?.CycleMinutes,
            Degraded = queue.Degraded || desks?.Degraded == true
        };
    }

    /// <summary>The published input (the desk term as given) and the shadow input (its sensor-only part).</summary>
    public static (NowcastInput Published, NowcastInput Shadow) Inputs(NowcastInput queue, DeskTerm desks) =>
        (WithDesks(queue, desks), WithDesks(queue, desks?.SensorOnly));
}
