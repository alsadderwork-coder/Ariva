namespace Ariva.Core.Queueing;

/// <summary>Why the shadow nowcast has no sensor cycle time for a minute (F8, ARV-117b); it then falls back as F8 does.</summary>
public enum SensorCycleFallback
{
    /// <summary>The window holds no sensor-only desk minute of the lane.</summary>
    NoDeskMinutes,

    /// <summary>A minute of the window has no sensor-only desk minute.</summary>
    IncompleteWindow,

    /// <summary>A desk of the lane has a minute in the window without a sensor-only minute: its busy time is not known.</summary>
    DesksWithoutSensors,

    /// <summary>Unknown, missing or refused desk time is above the allowed share of the window's desk time.</summary>
    UnknownDesks,

    /// <summary>More desk minutes in the window than the cap (CWE-120).</summary>
    TooManyMinutes,

    /// <summary>The queue's exits are not known for every minute of the window.</summary>
    NoExitData,

    /// <summary>Fewer exits than the minimum in the window (zero exits, or busy time with too few exits).</summary>
    TooFewExits,

    /// <summary>The cycle time lies outside the plausible bounds.</summary>
    OutOfBounds
}

/// <summary>Settings of the sensor cycle time of the shadow nowcast (F8, ARV-117b; Proposed values, docs/product/decisions.md).</summary>
public sealed record SensorCycleSettings
{
    /// <summary>W: the window in whole minutes, ending at the desk term's minute (Proposed 10; within the desk term's 15-minute read).</summary>
    public int WindowMinutes { get; init; } = 10;

    /// <summary>Below this many queue exits in the window there is no sensor cycle time (Proposed 10).</summary>
    public long MinimumExits { get; init; } = 10;

    /// <summary>Shorter cycle times (minutes per passenger) are not plausible at a desk (Proposed 0.2, 12 seconds).</summary>
    public double MinimumCycleMinutes { get; init; } = 0.2;

    /// <summary>Longer cycle times are not plausible at a desk (Proposed 10 minutes).</summary>
    public double MaximumCycleMinutes { get; init; } = 10;

    /// <summary>Above this share of the window's desk time Unknown, missing or refused, there is no sensor cycle time (Proposed 0.05).</summary>
    public double MaxUnknownShare { get; init; } = 0.05;

    /// <summary>Sensor-only desk minutes of one lane and window at most (CWE-120; Proposed 4,000: 400 desks over 10 minutes).</summary>
    public int MaxDeskMinutes { get; init; } = 4_000;

    public IEnumerable<string> Problems()
    {
        if (WindowMinutes is < 2 or > 10)
            yield return "WindowMinutes is from 2 to 10 (the desk term reads 15 minutes and is fresh for 5).";
        if (MinimumExits is < 1 or > 10_000)
            yield return "MinimumExits is from 1 to 10,000.";
        if (!double.IsFinite(MinimumCycleMinutes) || MinimumCycleMinutes is < 0.05 or > 10)
            yield return "MinimumCycleMinutes is from 0.05 to 10.";
        if (!double.IsFinite(MaximumCycleMinutes) || MaximumCycleMinutes <= MinimumCycleMinutes || MaximumCycleMinutes > 60)
            yield return "MaximumCycleMinutes is above MinimumCycleMinutes and at most 60.";
        if (!double.IsFinite(MaxUnknownShare) || MaxUnknownShare is < 0 or > 0.5)
            yield return "MaxUnknownShare is from 0 to 0.5.";
        if (MaxDeskMinutes is < 100 or > 20_000)
            yield return "MaxDeskMinutes is from 100 to 20,000.";
    }
}

/// <summary>
/// The sensor-only busy time of a lane's desks over the window [<see cref="FromMinuteUtc"/>, <see cref="ToMinuteUtc"/>]
/// (whole minutes), as aggregates only (no desk key): the Serving seconds the zones alone proved, the desk minutes
/// counted, the Unknown seconds, the minutes refused (<see cref="LeftOut"/>), the F11 flag, and why the window gives no
/// cycle time (<see cref="Missing"/>) when it cannot.
/// </summary>
public sealed record SensorBusyWindow(DateTime FromMinuteUtc, DateTime ToMinuteUtc, double ServingSeconds, int DeskMinutes, double UnknownSeconds, int LeftOut, bool Degraded,
    SensorCycleFallback? Missing)
{
    /// <summary>The window's length in whole minutes.</summary>
    public int Minutes => (int)((ToMinuteUtc - FromMinuteUtc).Ticks / TimeSpan.TicksPerMinute) + 1;
}

/// <summary>The sensor cycle time of one lane and window, or why there is none, and its F11 flag.</summary>
public sealed record SensorCycleResult(double? CycleMinutes, SensorCycleFallback? Fallback, bool Degraded);

/// <summary>
/// The sensor cycle time of the shadow nowcast (F8, ARV-117b): c = the lane's sensor-only Serving desk minutes over the
/// queue's exits in the same W whole minutes ending at the desk term's minute. Serving (staff present and the service zone
/// occupied, F10 row 6) and not Idle time, because open time over exits is the realised throughput again: n_open over it
/// is the exit rate scaled by n_open over the window's mean open desks, so the desk term would collapse into the exit
/// term and carry the idle time of a short queue (the F8 throughput identity). Busy time over exits is the time a desk
/// takes per passenger while it serves, the capacity per desk the desk term needs. Below the minimum exits, outside the
/// bounds, or with too much Unknown desk time there is no c and the shadow falls back as F8 does (the exit term alone,
/// flagged Degraded, or no service when nothing is open). Pure: no I/O, no clock.
/// </summary>
public static class SensorCycle
{
    private const double MinuteSeconds = 60;

    // Room for the binary rounding of a minute's parts, as script 0044's total check.
    private const double Rounding = 0.001;

    /// <summary>
    /// The window of the sensor-only desk minutes ending at <paramref name="asOfMinuteUtc"/>: every desk minute of the lane
    /// in it (published desk minutes with their sensor-only minute, <see cref="DeskMinuteSample.Sensor"/>). A sensor
    /// minute that is not a number, negative, above a minute, busier than open, or whose open and Unknown time exceed a
    /// minute is left out and counted, and its minute counts as Unknown; a desk minute without a sensor-only minute means
    /// the desk's busy time is not known (<see cref="SensorCycleFallback.DesksWithoutSensors"/>); a desk missing from some
    /// minutes counts as Unknown there.
    /// </summary>
    public static SensorBusyWindow Window(IEnumerable<DeskMinuteSample> minutes, DateTime asOfMinuteUtc, SensorCycleSettings settings = null)
    {
        ArgumentNullException.ThrowIfNull(minutes);
        settings ??= new SensorCycleSettings();
        Validate(settings);
        var to = new DateTime(asOfMinuteUtc.Ticks - asOfMinuteUtc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        if (to.Ticks < (settings.WindowMinutes - 1) * TimeSpan.TicksPerMinute)
            return new SensorBusyWindow(to, to, 0, 0, 0, 0, true, SensorCycleFallback.IncompleteWindow);
        var from = to.AddMinutes(-(settings.WindowMinutes - 1));

        var inWindow = new List<DeskMinuteSample>();
        foreach (var m in minutes)
        {
            if (m?.DeskKey is null || m.MinuteUtc < from || m.MinuteUtc > to)
                continue;
            // Bounded (CWE-120): beyond the cap the window is not aggregated at all, never from an arbitrary part of it.
            if (inWindow.Count == settings.MaxDeskMinutes)
                return new SensorBusyWindow(from, to, 0, settings.MaxDeskMinutes + 1, 0, 0, true, SensorCycleFallback.TooManyMinutes);
            inWindow.Add(m);
        }

        if (inWindow.Count == 0)
            return new SensorBusyWindow(from, to, 0, 0, 0, 0, true, SensorCycleFallback.NoDeskMinutes);

        double serving = 0, unknown = 0;
        int counted = 0, leftOut = 0;
        var degraded = false;
        var withoutSensors = false;
        var seen = new HashSet<(string Desk, DateTime Minute)>();
        var desks = new HashSet<string>(StringComparer.Ordinal);
        var minutesHeard = new HashSet<DateTime>();
        foreach (var m in inWindow)
        {
            // One desk minute counts once (the first), as the table's key allows only one.
            if (!seen.Add((m.DeskKey, m.MinuteUtc)))
                continue;
            desks.Add(m.DeskKey);
            // Read into a local, not a pattern variable of the condition (ARV-069a): a mutant of the condition still compiles.
            var s = m.Sensor;
            if (s is null)
            {
                withoutSensors = true;
                continue;
            }

            minutesHeard.Add(m.MinuteUtc);
            if (!Valid(s))
            {
                leftOut++;
                continue;
            }

            counted++;
            serving += s.ServingSeconds;
            unknown += s.UnknownSeconds;
            degraded |= s.Degraded || s.UnknownSeconds > 0;
        }

        if (withoutSensors)
            return new SensorBusyWindow(from, to, serving, counted, unknown, leftOut, true, SensorCycleFallback.DesksWithoutSensors);
        if (minutesHeard.Count < settings.WindowMinutes)
            return new SensorBusyWindow(from, to, serving, counted, unknown, leftOut, true, SensorCycleFallback.IncompleteWindow);

        // Missing (a desk absent from a minute) and refused desk minutes count as Unknown for the whole minute.
        var expected = (long)desks.Count * settings.WindowMinutes;
        var missing = expected - counted - leftOut;
        var unknownSeconds = unknown + MinuteSeconds * (leftOut + missing);
        degraded |= leftOut > 0 || missing > 0;
        var share = unknownSeconds / (MinuteSeconds * expected);
        return share > settings.MaxUnknownShare + 1e-12
            ? new SensorBusyWindow(from, to, serving, counted, unknown, leftOut, true, SensorCycleFallback.UnknownDesks)
            : new SensorBusyWindow(from, to, serving, counted, unknown, leftOut, degraded, null);
    }

    /// <summary>
    /// c = Serving desk minutes over exits in the window, or why there is none. <paramref name="exits"/> must cover the
    /// same whole minutes as <paramref name="busy"/> and every one of them observed.
    /// </summary>
    public static SensorCycleResult Compute(SensorBusyWindow busy, ExitWindow exits, SensorCycleSettings settings = null)
    {
        settings ??= new SensorCycleSettings();
        Validate(settings);
        if (busy is null)
            return new SensorCycleResult(null, SensorCycleFallback.NoDeskMinutes, true);
        if (busy.Missing is { } missing)
            return new SensorCycleResult(null, missing, true);
        if (exits is null || !exits.Complete || exits.WindowMinutes != busy.Minutes || exits.Exits < 0 || exits.DegradedExits < 0)
            return new SensorCycleResult(null, SensorCycleFallback.NoExitData, true);
        if (exits.Exits < settings.MinimumExits)
            return new SensorCycleResult(null, SensorCycleFallback.TooFewExits, true);
        var cycle = busy.ServingSeconds / MinuteSeconds / exits.Exits;
        if (!double.IsFinite(cycle) || cycle < settings.MinimumCycleMinutes || cycle > settings.MaximumCycleMinutes)
            return new SensorCycleResult(null, SensorCycleFallback.OutOfBounds, true);
        return new SensorCycleResult(cycle, null, busy.Degraded || exits.DegradedExits > 0);
    }

    /// <summary>
    /// The shadow's desk term with the sensor cycle time: n_open from the zones over the sensor c, flagged when the cycle
    /// time is. Without one the term has no cycle time and keeps its own flag: <see cref="Nowcast.Compute"/> then falls
    /// back as F8 does, to the exit term alone, which it always flags Degraded, or to no service when nothing is open (the
    /// cycle time is not needed there), so the shadow's fallback is flagged exactly as the published nowcast's is.
    /// </summary>
    public static DeskTerm Apply(DeskTerm sensorOnly, SensorCycleResult cycle)
    {
        if (sensorOnly is null)
            return null;
        return cycle?.CycleMinutes is { } c
            ? sensorOnly with { CycleMinutes = c, Degraded = sensorOnly.Degraded || cycle.Degraded }
            : sensorOnly with { CycleMinutes = null };
    }

    private static bool Valid(DeskSensorSample s) =>
        double.IsFinite(s.ServingSeconds) && double.IsFinite(s.OpenSeconds) && double.IsFinite(s.UnknownSeconds) &&
        s.ServingSeconds is >= 0 and <= MinuteSeconds && s.OpenSeconds is >= 0 and <= MinuteSeconds && s.UnknownSeconds is >= 0 and <= MinuteSeconds &&
        s.ServingSeconds <= s.OpenSeconds + Rounding && s.OpenSeconds + s.UnknownSeconds <= MinuteSeconds + Rounding;

    private static void Validate(SensorCycleSettings settings)
    {
        var problems = settings.Problems().ToList();
        if (problems.Count > 0)
            throw new ArgumentException(string.Join(" ", problems), nameof(settings));
    }
}
