using Ariva.Core.Border;
using Ariva.Core.Flights;

namespace Ariva.Core.Staffing;

/// <summary>Why an interval of a lane has no recommendation (formulas F16 pilot version, ARV-181a); stored and answered by name.</summary>
public enum StaffingGap
{
    /// <summary>The interval has a recommendation.</summary>
    None,

    /// <summary>The lane's queue length now is not known: no live queue minute recently, or its length is degraded.</summary>
    NoQueueLength,

    /// <summary>The lane's throughput per open desk is neither measured nor configured as a fallback.</summary>
    NoThroughput,

    /// <summary>A flight without a passenger figure reaches the hall in this interval: its arrivals are not known (owner decision 2026-10-15).</summary>
    FlightWithoutPassengers,

    /// <summary>An earlier interval's arrivals are not known, so the backlog carried into this one is not known either.</summary>
    BacklogUnknown,

    /// <summary>The site had more arriving legs than are read: the arrivals are not complete.</summary>
    ArrivalsTruncated,

    /// <summary>The lane has no desk in service.</summary>
    NoDesks,

    /// <summary>The lane's category has no demand model in the arrival wave (it knows CIT, RES, VIS and CRW at manual desks).</summary>
    NoLaneDemand,

    /// <summary>
    /// The interval's arrivals are not a plausible number (above a million, or a minute that is not a finite, non-negative
    /// number): not used, so one bad interval never stops the site (CWE-501).
    /// </summary>
    ArrivalsOutOfRange
}

/// <summary>What set an interval's recommendation: the target wait, the busy cap, both, or nothing (no desk needed).</summary>
public enum StaffingBound
{
    None,
    Wait,
    Busy,
    Both
}

/// <summary>One interval of the plan: its start and its length in minutes (the first runs from now to the next quarter hour).</summary>
public sealed record StaffingSlot(DateTime StartUtc, int Minutes)
{
    public DateTime EndUtc => StartUtc.AddMinutes(Minutes);
}

/// <summary>
/// The arrivals A_k of a lane in one interval (F14 hall arrivals of its category with the e-gate rejects coupled, F12,
/// shared among its queue zones), or why they are not known (<see cref="Gap"/>, with <see cref="Arrivals"/> then null).
/// </summary>
public sealed record LaneDemandInterval(DateTime StartUtc, int Minutes, double? Arrivals, StaffingGap Gap = StaffingGap.None);

/// <summary>
/// What the recommendation of one lane needs (formulas F16 pilot version): Q_0 the queue now, A_k per interval, r the
/// throughput per open desk per minute, the desks open now (the desk-state count, F8 n_open), N the desks the lane has, T
/// the target wait in minutes and u the busy cap. Null means not known.
/// </summary>
public sealed record LaneStaffingInput(
    double? QueueNow,
    IReadOnlyList<LaneDemandInterval> Intervals,
    double? DeskRatePerMinute,
    int? OpenNow,
    int Desks,
    double TargetMinutes,
    double BusyCap);

/// <summary>
/// One interval's result: the arrivals, the recommended open desks and what set them (<see cref="Bound"/>), whether the lane
/// is at its desk limit and by how many desks it falls short (null at the limit when no number of desks reaches the target),
/// the predicted wait with the recommendation and with the desks open now (minutes, capped at
/// <see cref="StaffingRecommendation.WaitCapMinutes"/>; null when not known or, for open now, when nothing is open while
/// people wait: <see cref="NoServiceOpenNow"/>), the backlog left with the recommendation, and why there is none.
/// </summary>
public sealed record StaffingIntervalResult(
    DateTime StartUtc,
    int Minutes,
    double? Arrivals,
    int? Recommended,
    StaffingBound Bound,
    bool AtLimit,
    int? ShortfallDesks,
    double? WaitRecommendedMinutes,
    double? WaitOpenNowMinutes,
    bool NoServiceOpenNow,
    double? BacklogEnd,
    StaffingGap Gap);

/// <summary>A lane's recommendation: the lane-wide reason there is none (<see cref="StaffingGap.None"/> otherwise) and its intervals.</summary>
public sealed record LaneStaffing(StaffingGap Gap, IReadOnlyList<StaffingIntervalResult> Intervals);

/// <summary>
/// The indicative staffing recommendation (formulas F16 pilot version, ARV-181a): per lane and interval, the fewest open
/// desks n_k that keep the predicted wait at or below the target and each desk busy at most the cap's share of the
/// interval with its arrivals, decided forward in time because n_k changes the backlog carried into k+1 (F9):
/// <code>
/// Q_(k+1)(n) = max(0, Q_k + A_k - n r delta_k)
/// W_k(n)     = max(Q_k, Q_(k+1)(n)) / (n r)          (W_k(0) = 0 when Q_k = A_k = 0)
/// n_k        = the smallest n in [0, N] with W_k(n) &lt;= T and A_k &lt;= u n r delta_k
/// </code>
/// The wait is the longest joining wait of the interval in the fluid model, not F9's (Q_k + A_k / 2) / mu_k, which leaves
/// out what the desks serve during the interval and cannot express F16's own test. No variability (F15 is v1), no roster,
/// breaks or shared officers. Pure: no I/O, no clock.
/// </summary>
public static class StaffingRecommendation
{
    /// <summary>delta: the interval length in minutes.</summary>
    public const int IntervalMinutes = 15;

    /// <summary>Rows of a plan: the current quarter hour from now and 12 more, reaching now plus 3 hours (decision of 2026-10-15).</summary>
    public const int Intervals = 13;

    /// <summary>N at most per lane (as AlertRuleValues caps desks).</summary>
    public const int MaxDesks = 1_000;

    /// <summary>The largest target wait in minutes.</summary>
    public const double MaxTargetMinutes = 120;

    /// <summary>Waits are capped here (F8 and F9's reference cap); the screens show "over 120 min".</summary>
    public const double WaitCapMinutes = 120;

    /// <summary>Queue lengths and arrivals per interval above this are not plausible (CWE-501).</summary>
    public const double MaxPassengers = 1_000_000;

    /// <summary>A shortfall above this is reported without a number (the target cannot be met with any plausible count).</summary>
    public const int MaxShortfall = 1_000_000;

    /// <summary>The smallest throughput per desk the engine takes, per minute (below it no count of desks is plausible).</summary>
    public const double MinRatePerMinute = 1e-6;

    /// <summary>
    /// The plan's 13 intervals from the minute of <paramref name="nowUtc"/>: that minute to the next quarter hour (1 to 15
    /// minutes; UTC quarter hours are local quarter hours in every time zone), then 12 quarter hours.
    /// </summary>
    public static IReadOnlyList<StaffingSlot> Plan(DateTime nowUtc)
    {
        if (nowUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("The time is UTC.", nameof(nowUtc));
        var minute = new DateTime(nowUtc.Ticks - nowUtc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        var quarter = TimeSpan.TicksPerMinute * IntervalMinutes;
        var next = new DateTime(minute.Ticks - minute.Ticks % quarter + quarter, DateTimeKind.Utc);
        var slots = new List<StaffingSlot>(Intervals) { new(minute, (int)((next - minute).Ticks / TimeSpan.TicksPerMinute)) };
        for (var k = 1; k < Intervals; k++)
            slots.Add(new StaffingSlot(next.AddMinutes((k - 1) * IntervalMinutes), IntervalMinutes));
        return slots;
    }

    /// <summary>
    /// A lane category's arrivals per interval of <paramref name="plan"/> (F14 hall arrivals per minute, rejects already
    /// coupled into <paramref name="minutes"/>, divided by <paramref name="sharing"/>, the arrivals-hall queue zones of the
    /// category). No guess: legs left unread (<paramref name="truncated"/>) give <see cref="StaffingGap.ArrivalsTruncated"/>
    /// everywhere; a category the wave has no demand for gives <see cref="StaffingGap.NoLaneDemand"/>; a flight without a
    /// passenger figure gives <see cref="StaffingGap.FlightWithoutPassengers"/> to every interval holding one of its hall
    /// minutes (owner decision 2026-10-15); an interval whose sum is not a plausible number (a minute not finite or negative,
    /// or above <see cref="MaxPassengers"/> in all) gives <see cref="StaffingGap.ArrivalsOutOfRange"/>. Minutes the
    /// projection does not hold count none (it holds every minute from now to beyond the plan's end).
    /// </summary>
    public static IReadOnlyList<LaneDemandInterval> LaneArrivals(IReadOnlyList<StaffingSlot> plan, IEnumerable<MinuteDemand> minutes, IEnumerable<FlightWave> flights,
        string laneCategory, int sharing, bool truncated)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(minutes);
        ArgumentNullException.ThrowIfNull(flights);
        ArgumentOutOfRangeException.ThrowIfLessThan(sharing, 1);
        if (truncated)
            return [.. plan.Select(s => new LaneDemandInterval(s.StartUtc, s.Minutes, null, StaffingGap.ArrivalsTruncated))];
        if (laneCategory is null || !ImmigrationRules.Lanes.Contains(laneCategory))
            return [.. plan.Select(s => new LaneDemandInterval(s.StartUtc, s.Minutes, null, StaffingGap.NoLaneDemand))];

        var byMinute = new Dictionary<DateTime, double>();
        foreach (var minute in minutes)
        {
            if (minute?.Lanes is null)
                continue;
            var value = minute.Lanes.Of(laneCategory);
            // A minute that is not a finite, non-negative number poisons its interval (NaN), never the site.
            byMinute[minute.MinuteUtc] = double.IsFinite(value) && value >= 0 ? byMinute.GetValueOrDefault(minute.MinuteUtc) + value : double.NaN;
        }

        var unknown = flights.Where(f => f is not null && f.Passengers is null).Select(f => (f.HallFirstUtc, f.HallLastUtc)).ToList();
        var result = new List<LaneDemandInterval>(plan.Count);
        foreach (var slot in plan)
        {
            // A flight's hall minutes are [first, last], each the start of a whole minute.
            if (unknown.Any(f => f.HallFirstUtc < slot.EndUtc && f.HallLastUtc >= slot.StartUtc))
            {
                result.Add(new LaneDemandInterval(slot.StartUtc, slot.Minutes, null, StaffingGap.FlightWithoutPassengers));
                continue;
            }

            double sum = 0;
            for (var m = 0; m < slot.Minutes; m++)
                sum += byMinute.GetValueOrDefault(slot.StartUtc.AddMinutes(m));
            var arrivals = sum / sharing;
            result.Add(double.IsFinite(arrivals) && arrivals <= MaxPassengers
                ? new LaneDemandInterval(slot.StartUtc, slot.Minutes, arrivals)
                : new LaneDemandInterval(slot.StartUtc, slot.Minutes, null, StaffingGap.ArrivalsOutOfRange));
        }

        return result;
    }

    /// <summary>
    /// The recommendation of one lane (F16 pilot version). A lane without desks (<see cref="StaffingGap.NoDesks"/>), without
    /// a throughput (<see cref="StaffingGap.NoThroughput"/>) or without a queue length now
    /// (<see cref="StaffingGap.NoQueueLength"/>) has none in any interval, its arrivals still given. An interval whose
    /// arrivals are not known keeps its reason, and every later one has <see cref="StaffingGap.BacklogUnknown"/>: the
    /// backlog carried into it is not known, and a number there would be a guess.
    /// </summary>
    public static LaneStaffing Recommend(LaneStaffingInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Validate(input);
        var laneGap = input.Desks == 0 ? StaffingGap.NoDesks
            : input.DeskRatePerMinute is null ? StaffingGap.NoThroughput
            : input.QueueNow is null ? StaffingGap.NoQueueLength
            : StaffingGap.None;
        if (laneGap != StaffingGap.None)
            return new LaneStaffing(laneGap, [.. input.Intervals.Select(i => Empty(i, i.Gap == StaffingGap.None ? laneGap : i.Gap))]);

        var r = input.DeskRatePerMinute.Value;
        var q = input.QueueNow.Value;
        var qOpen = q;
        var broken = false;
        var results = new List<StaffingIntervalResult>(input.Intervals.Count);
        foreach (var interval in input.Intervals)
        {
            if (interval.Gap != StaffingGap.None || broken)
            {
                results.Add(Empty(interval, interval.Gap != StaffingGap.None ? interval.Gap : StaffingGap.BacklogUnknown));
                broken = true;
                continue;
            }

            var a = interval.Arrivals.Value;
            var d = interval.Minutes;
            var (n, bound, atLimit, shortfall) = Choose(q, a, d, r, input.TargetMinutes, input.BusyCap, input.Desks);
            var (wait, backlog) = Step(q, a, n, r, d);

            double? waitOpen = null;
            var noService = false;
            if (input.OpenNow is { } open)
            {
                var (w, next) = Step(qOpen, a, open, r, d);
                noService = w is null;
                waitOpen = w;
                qOpen = next;
            }

            results.Add(new StaffingIntervalResult(interval.StartUtc, d, a, n, bound, atLimit, shortfall, wait, waitOpen, noService, backlog, StaffingGap.None));
            q = backlog;
        }

        return new LaneStaffing(StaffingGap.None, results);
    }

    /// <summary>
    /// The fewest desks for one interval (closed form of the smallest n with W_k(n) &lt;= T and A_k &lt;= u n r delta_k):
    /// <code>
    /// n_wait = max(ceil(Q_k / (r T)), ceil((Q_k + A_k) / (r (T + delta_k))))   (T = 0: ceil(A_k / (r delta_k)) when Q_k = 0, unreachable otherwise)
    /// n_busy = ceil(A_k / (u r delta_k))
    /// </code>
    /// The larger, capped at N: above it the lane is at its limit and short by the difference (no number when unreachable or
    /// above <see cref="MaxShortfall"/>). n_wait is at least 1 while anyone waits or arrives (no desk is no service). Ceilings
    /// as formulas.md's conventions say (rounded to 9 decimals first).
    /// </summary>
    public static (int Desks, StaffingBound Bound, bool AtLimit, int? ShortfallDesks) Choose(double queue, double arrivals, int minutes, double ratePerMinute, double targetMinutes,
        double busyCap, int desks)
    {
        double? wait;
        if (queue <= 0 && arrivals <= 0)
            wait = 0;
        else if (targetMinutes <= 0)
            wait = queue > 0 ? null : Math.Max(1, Ceiling(arrivals / (ratePerMinute * minutes)));
        else
            // At least one desk while anyone waits or arrives: with none there is no service (W_k(0) is not defined), however
            // small the fluid amounts that round away.
            wait = Math.Max(1, Math.Max(Ceiling(queue / (ratePerMinute * targetMinutes)), Ceiling((queue + arrivals) / (ratePerMinute * (targetMinutes + minutes)))));
        var busy = arrivals <= 0 ? 0 : Ceiling(arrivals / (busyCap * ratePerMinute * minutes));

        var need = wait is { } w ? Math.Max(w, busy) : (double?)null;
        var bound = need is 0d ? StaffingBound.None
            : wait is not { } waitNeed ? StaffingBound.Wait
            : waitNeed == busy ? StaffingBound.Both
            : waitNeed > busy ? StaffingBound.Wait
            : StaffingBound.Busy;
        if (need is { } fits && fits <= desks)
            return ((int)fits, bound, false, null);
        int? shortfall = need is { } over && over - desks <= MaxShortfall ? (int)(over - desks) : null;
        return (desks, bound, true, shortfall);
    }

    /// <summary>
    /// One interval of the F9 recursion with <paramref name="desks"/> open: the predicted wait max(Q_k, Q_(k+1)) / (n r)
    /// (capped; 0 when nobody waits or arrives; null with nothing open while people wait or arrive: no service) and the
    /// backlog carried forward.
    /// </summary>
    public static (double? WaitMinutes, double Backlog) Step(double queue, double arrivals, int desks, double ratePerMinute, int minutes)
    {
        if (desks <= 0)
            return queue <= 0 && arrivals <= 0 ? (0, 0) : (null, queue + arrivals);
        var capacity = desks * ratePerMinute * minutes;
        var backlog = Math.Max(0, queue + arrivals - capacity);
        return (Math.Min(WaitCapMinutes, Math.Max(queue, backlog) / (desks * ratePerMinute)), backlog);
    }

    private static StaffingIntervalResult Empty(LaneDemandInterval interval, StaffingGap gap) =>
        new(interval.StartUtc, interval.Minutes, interval.Arrivals, null, StaffingBound.None, false, null, null, null, false, null, gap);

    // ceil(x) with x rounded to 9 decimals first (formulas.md, Conventions); a value beyond any desk count stays a double.
    private static double Ceiling(double value) => Math.Ceiling(Math.Round(value, 9));

    private static void Validate(LaneStaffingInput input)
    {
        if (input.Intervals is null || input.Intervals.Count is < 1 or > 100)
            throw new ArgumentException("A lane has 1 to 100 intervals.", nameof(input));
        if (input.QueueNow is { } q && (!double.IsFinite(q) || q < 0 || q > MaxPassengers))
            throw new ArgumentException("The queue now is finite, not negative and at most a million.", nameof(input));
        if (input.DeskRatePerMinute is { } r && (!double.IsFinite(r) || r < MinRatePerMinute || r > 1_000))
            throw new ArgumentException("The throughput per desk is 0.000001 to 1,000 a minute.", nameof(input));
        if (input.OpenNow is < 0 or > MaxDesks || input.Desks is < 0 or > MaxDesks)
            throw new ArgumentException($"Desks are 0 to {MaxDesks}.", nameof(input));
        if (!double.IsFinite(input.TargetMinutes) || input.TargetMinutes is < 0 or > MaxTargetMinutes)
            throw new ArgumentException("The target wait is 0 to 120 minutes.", nameof(input));
        if (!double.IsFinite(input.BusyCap) || input.BusyCap is <= 0 or > 1)
            throw new ArgumentException("The busy cap is above 0 and at most 1.", nameof(input));
        DateTime? expected = null;
        foreach (var interval in input.Intervals)
        {
            if (interval is null || interval.Minutes is < 1 or > IntervalMinutes || interval.StartUtc.Kind != DateTimeKind.Utc ||
                (expected is { } start && interval.StartUtc != start))
                throw new ArgumentException("Intervals are 1 to 15 minutes, UTC, each starting where the one before ends.", nameof(input));
            if (interval.Gap == StaffingGap.None
                    ? interval.Arrivals is not { } a || !double.IsFinite(a) || a < 0 || a > MaxPassengers
                    : interval.Arrivals is { } known && (!double.IsFinite(known) || known < 0 || known > MaxPassengers))
                throw new ArgumentException("An interval's arrivals are finite, not negative and at most a million, and known unless it has a gap.", nameof(input));
            expected = interval.StartUtc.AddMinutes(interval.Minutes);
        }
    }
}
