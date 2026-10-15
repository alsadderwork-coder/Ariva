namespace Ariva.Core.Staffing;

/// <summary>Where a lane's throughput per open desk comes from (formulas F16 pilot version).</summary>
public enum DeskThroughputSource
{
    /// <summary>Measured from the lane's own recent exits per open desk-minute.</summary>
    Measured,

    /// <summary>The cycle time configured for the lane's category at commissioning.</summary>
    Fallback
}

/// <summary>Why a lane's throughput was not measured (it then takes the configured fallback, or has none).</summary>
public enum ThroughputMiss
{
    /// <summary>Fewer open desk-minutes than the minimum in the window's busy minutes.</summary>
    TooFewOpenDeskMinutes,

    /// <summary>Fewer exits than the minimum in the window's busy minutes.</summary>
    TooFewExits,

    /// <summary>The cycle time per passenger lies outside the plausible bounds.</summary>
    OutOfBounds
}

/// <summary>
/// One desk's closed minute as the desk state engine wrote it (<c>desk_minute</c>, F10): its key (site/checkpoint/desk),
/// its Idle, Serving and Unknown seconds and its F11 flag. The staffing recommendation's own type: it never carries the
/// sensor-only minute of the shadow nowcast (ARV-117a), which stays with the desk term.
/// </summary>
public sealed record StaffingDeskMinute(string DeskKey, DateTime MinuteUtc, double IdleSeconds, double ServingSeconds, double UnknownSeconds, bool Degraded);

/// <summary>One minute of a lane's queue as the stream stored it (<c>queue_minute</c>): exits, the length and its F11 flag.</summary>
public sealed record QueueMinuteSample(DateTime MinuteUtc, long? Exits, int? QueueLength, bool LengthDegraded);

/// <summary>
/// A lane's throughput per open desk: r in passengers per open desk-minute (null when neither measured nor configured), its
/// source, why it was not measured (<see cref="Miss"/>), and the window's tallies (exits and open desk-minutes of the busy
/// minutes used, minutes used, and minutes left out because a value was missing, degraded or out of its bounds).
/// </summary>
public sealed record LaneThroughputResult(double? RatePerDeskMinute, DeskThroughputSource? Source, ThroughputMiss? Miss, long Exits, double OpenDeskMinutes, int MinutesUsed,
    int MinutesLeftOut)
{
    /// <summary>The cycle time per passenger at a desk in minutes (1 / r), or null.</summary>
    public double? CycleMinutes => RatePerDeskMinute is { } r ? 1 / r : null;
}

/// <summary>
/// The measured throughput per open desk of a lane (formulas F16 pilot version, ARV-181a, Proposed values):
/// <code>
/// r = (sum of X_m) / (sum of O_m) over the busy minutes m of the window
/// </code>
/// X_m the lane queue's exits in minute m, O_m the open (Idle plus Serving, F10) desk-minutes of the lane's desks in it. A
/// minute is used when its queue minute has a length that is not degraded, every desk of the lane heard in the window has its
/// minute (none degraded, none Unknown for half of it, each within its bounds; a desk never heard in the window cannot have
/// been open and is not waited for), some desk was open, and it was busy: at least one person
/// waiting per open desk (queue length at least ceil(O_m)), so the desks were not idle for want of passengers; open time
/// over exits otherwise measures the realised throughput, not the capacity (F8's sensor cycle note). Measured when the busy
/// minutes hold the minimum open desk-minutes and exits and 1 / r lies within the plausible cycle times; otherwise the
/// fallback configured for the lane's category (60 / its cycle seconds), or none. Pure: no I/O, no clock.
/// </summary>
public static class LaneThroughput
{
    private const double HalfMinuteSeconds = 30;
    private const double MinuteSeconds = 60;

    // Room for the binary rounding of a minute's parts, as script 0018's checks.
    private const double Rounding = 0.001;

    // A minute's exits above this are not plausible (CWE-501): the minute is left out, and the window's sum cannot overflow.
    private const long MaxExitsPerMinute = (long)StaffingRecommendation.MaxPassengers;

    /// <summary>
    /// The lane's throughput over the <see cref="StaffingSettings.ThroughputWindowMinutes"/> whole minutes ending at
    /// <paramref name="asOfMinuteUtc"/> (the lane queue's latest live minute). <paramref name="deskKeys"/> are the lane's
    /// desks; <paramref name="fallbackCycleSeconds"/> the configured cycle time of its category, or null.
    /// </summary>
    public static LaneThroughputResult PerOpenDesk(IReadOnlyCollection<string> deskKeys, IEnumerable<StaffingDeskMinute> deskMinutes, IEnumerable<QueueMinuteSample> queueMinutes,
        DateTime asOfMinuteUtc, StaffingSettings settings, double? fallbackCycleSeconds)
    {
        ArgumentNullException.ThrowIfNull(deskKeys);
        ArgumentNullException.ThrowIfNull(deskMinutes);
        ArgumentNullException.ThrowIfNull(queueMinutes);
        ArgumentNullException.ThrowIfNull(settings);
        var to = new DateTime(asOfMinuteUtc.Ticks - asOfMinuteUtc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        var from = to.AddMinutes(-(settings.ThroughputWindowMinutes - 1));
        var desks = deskKeys.Where(k => k is not null).ToHashSet(StringComparer.Ordinal);

        var queue = new Dictionary<DateTime, QueueMinuteSample>();
        foreach (var sample in queueMinutes)
        {
            if (sample is not null && sample.MinuteUtc >= from && sample.MinuteUtc <= to)
                queue.TryAdd(sample.MinuteUtc, sample);
        }

        var byMinute = new Dictionary<DateTime, Dictionary<string, StaffingDeskMinute>>();
        foreach (var sample in deskMinutes)
        {
            if (sample?.DeskKey is null || !desks.Contains(sample.DeskKey) || sample.MinuteUtc < from || sample.MinuteUtc > to)
                continue;
            if (!byMinute.TryGetValue(sample.MinuteUtc, out var minute))
                byMinute[sample.MinuteUtc] = minute = new Dictionary<string, StaffingDeskMinute>(StringComparer.Ordinal);
            minute.TryAdd(sample.DeskKey, sample);
        }

        // The desks heard in the window are the lane's working desks; a desk never heard in it (out of service, no signal
        // source) cannot have been open, so it does not hold every minute back.
        var heard = byMinute.Values.SelectMany(m => m.Keys).ToHashSet(StringComparer.Ordinal);
        long exits = 0;
        double open = 0;
        int used = 0, leftOut = 0;
        for (var m = from; m <= to; m = m.AddMinutes(1))
        {
            if (!queue.TryGetValue(m, out var q) || q.QueueLength is not { } length || length < 0 || q.LengthDegraded || q.Exits is not { } x || x is < 0 or > MaxExitsPerMinute ||
                heard.Count == 0 || !byMinute.TryGetValue(m, out var minute) || minute.Count < heard.Count || !minute.Values.All(Usable))
            {
                leftOut++;
                continue;
            }

            var openMinutes = minute.Values.Sum(s => s.IdleSeconds + s.ServingSeconds) / MinuteSeconds;
            // Not busy: nobody waited for some open desk, so its idle time says nothing about how fast it serves.
            if (openMinutes <= 0 || length < Math.Ceiling(Math.Round(openMinutes, 9)))
                continue;
            exits += x;
            open += openMinutes;
            used++;
        }

        ThroughputMiss? miss = null;
        if (open < settings.MinOpenDeskMinutes)
            miss = ThroughputMiss.TooFewOpenDeskMinutes;
        else if (exits < settings.MinExits)
            miss = ThroughputMiss.TooFewExits;
        else if (open / exits is var cycle && (cycle < settings.CycleMinMinutes || cycle > settings.CycleMaxMinutes))
            miss = ThroughputMiss.OutOfBounds;
        if (miss is null)
            return new LaneThroughputResult(exits / open, DeskThroughputSource.Measured, null, exits, open, used, leftOut);
        if (fallbackCycleSeconds is { } seconds && double.IsFinite(seconds) && seconds is >= 3 and <= 600)
            return new LaneThroughputResult(MinuteSeconds / seconds, DeskThroughputSource.Fallback, miss, exits, open, used, leftOut);
        return new LaneThroughputResult(null, null, miss, exits, open, used, leftOut);
    }

    // A desk minute within its bounds (each state's seconds 0 to 60, together at most a minute; CWE-501), not degraded, not
    // Unknown for half of it.
    private static bool Usable(StaffingDeskMinute s) =>
        double.IsFinite(s.IdleSeconds) && double.IsFinite(s.ServingSeconds) && double.IsFinite(s.UnknownSeconds) &&
        s.IdleSeconds is >= 0 and <= MinuteSeconds && s.ServingSeconds is >= 0 and <= MinuteSeconds && s.UnknownSeconds is >= 0 and <= MinuteSeconds &&
        s.IdleSeconds + s.ServingSeconds + s.UnknownSeconds <= MinuteSeconds + Rounding && !s.Degraded && s.UnknownSeconds < HalfMinuteSeconds;
}
