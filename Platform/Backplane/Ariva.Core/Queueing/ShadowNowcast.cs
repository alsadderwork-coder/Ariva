namespace Ariva.Core.Queueing;

/// <summary>
/// The shadow nowcast of a queue minute (ARV-117): the F8 nowcast computed without any AMAN input, stored in
/// <c>queue_minute_shadow</c> (ARV-117a, in the same checkpoint transaction as the published row) for the pilot's
/// ground-truth proof (the validation comparison, ARV-104f). It is never shown to staff or passengers, never an alert
/// input and never in a report or the live snapshot: it is written by the stream, which cannot read it back (the runtime
/// role has no SELECT on its values), and read only by that comparison. <see cref="Minutes"/> is null with a <see cref="NoService"/> reason, as
/// for the published nowcast. <see cref="CycleMinutes"/> (ARV-117b) is the sensor cycle time its desk term took
/// (<see cref="SensorCycle"/>), null when it had none and fell back as F8 does.
/// </summary>
public sealed record ShadowNowcast(double? Minutes, NoServiceReason? NoService, bool Degraded, double? CycleMinutes = null)
{
    public static ShadowNowcast From(NowcastResult result, double? cycleMinutes = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new ShadowNowcast(result.Minutes, result.NoService, result.Degraded, cycleMinutes);
    }
}

/// <summary>
/// The inputs of the published and the shadow nowcast of one queue minute (F8, ARV-117). Both start from the same
/// queue-side input (the queue length, the exits in the window, the window and the queue's own flags) and take a desk
/// term: the published nowcast the desks' term as the stream reads it (n_open with AMAN's sessions, c from AMAN's interval
/// statistics or transactions), the shadow its <see cref="DeskTerm.SensorOnly"/> part (n_open from the desks' zones alone)
/// with the sensor cycle time (ARV-117b, <see cref="SensorCycle"/>: the zones' Serving desk minutes over the queue's exits
/// in the same window), or none, falling back as F8 does. The two inputs therefore differ only in n_open, c and the desk
/// term's flag; the fast-track share and the reject rate keep their defaults in both (the queue nowcast takes no AMAN
/// reject rate). Pure.
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

    /// <summary>
    /// The published input (the desk term as given) and the shadow input (its sensor-only part with the sensor cycle time
    /// from its busy window and <paramref name="cycleExits"/>, the queue's exits over the same minutes), with the sensor
    /// cycle time the shadow took. The published input never depends on the sensor cycle time: it is
    /// <see cref="WithDesks"/> of the term as given, whatever the busy window, the exits or the settings.
    /// </summary>
    public static (NowcastInput Published, NowcastInput Shadow, SensorCycleResult Cycle) Inputs(NowcastInput queue, DeskTerm desks, ExitWindow cycleExits,
        SensorCycleSettings settings = null)
    {
        var published = WithDesks(queue, desks);
        if (desks?.SensorOnly is not { } sensorOnly)
            return (published, WithDesks(queue, null), null);
        var cycle = SensorCycle.Compute(sensorOnly.SensorBusy, cycleExits, settings);
        return (published, WithDesks(queue, SensorCycle.Apply(sensorOnly, cycle)), cycle);
    }

    /// <summary>
    /// The published input and the shadow input without exits for the sensor cycle time: the shadow's term then has no
    /// cycle time and falls back as F8 does (flagged while a desk is open).
    /// </summary>
    public static (NowcastInput Published, NowcastInput Shadow) Inputs(NowcastInput queue, DeskTerm desks)
    {
        var (published, shadow, _) = Inputs(queue, desks, null);
        return (published, shadow);
    }
}
