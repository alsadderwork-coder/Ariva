namespace Ariva.Core.Queueing;

/// <summary>Why there is no nowcast (F8 edge cases): the display shows a neutral message, never infinity or zero.</summary>
public enum NoServiceReason
{
    /// <summary>No desk, gate or lane is staffed and unpaused.</summary>
    NothingOpen,

    /// <summary>Open, but the throughput is below the minimum rate (reference 0.01 passengers per minute).</summary>
    ThroughputTooLow,

    /// <summary>Neither a desk term nor an exit rate is known.</summary>
    NoThroughputData,

    /// <summary>The queue length is not known.</summary>
    NoQueueLength,

    /// <summary>The inputs give no finite, plausible wait (for example a cycle time too short to be real).</summary>
    Implausible
}

/// <summary>Which terms the throughput came from.</summary>
public enum ThroughputSource
{
    /// <summary>beta x desks / cycle time plus (1 - beta) x the exit rate.</summary>
    Blend,

    /// <summary>Desks over cycle time only (no exit rate known).</summary>
    Desks,

    /// <summary>The exit rate only (no desk state or cycle time known); Degraded.</summary>
    Exits
}

/// <summary>
/// What the nowcast of one queue needs at a moment (F8). Unknown inputs are null. <see cref="MergeShare"/> is the
/// standard lane's share of desks shared with a fast track; <see cref="RejectRate"/> applies to e-gates (F12).
/// </summary>
public sealed record NowcastInput
{
    /// <summary>Q(t): people in the queue zone and its overflow bands.</summary>
    public int? QueueLength { get; init; }

    /// <summary>n_open(t): desks Idle or Serving (staffed, not paused); Unknown desks do not count.</summary>
    public int? OpenServers { get; init; }

    /// <summary>c(t): recent mean cycle time per desk (or gate), in minutes.</summary>
    public double? CycleMinutes { get; init; }

    /// <summary>Exits counted over the last <see cref="ExitWindowMinutes"/> minutes.</summary>
    public long? ExitsInWindow { get; init; }

    /// <summary>m: the exit-rate window (Proposed 5 minutes).</summary>
    public double ExitWindowMinutes { get; init; } = 5;

    /// <summary>r_merge: the standard lane's share of desks shared with a fast track (1 when not shared).</summary>
    public double MergeShare { get; init; } = 1;

    /// <summary>r: the e-gate reject rate (0 for desks).</summary>
    public double RejectRate { get; init; }

    /// <summary>The zone is degraded (a sensor outage, an Unknown desk, a stale feed): the result is a band.</summary>
    public bool Degraded { get; init; }
}

/// <summary>A nowcast: the wait in minutes for someone joining now, or why there is none, and how far it can be trusted.</summary>
public sealed record NowcastResult(double? Minutes, double? Throughput, ThroughputSource? Source, NoServiceReason? NoService, bool Degraded);

/// <summary>Settings of the nowcast (F8; Proposed values to tune in the pilot).</summary>
public sealed record NowcastSettings
{
    /// <summary>beta: the weight of the desk term in the blend.</summary>
    public double Beta { get; init; } = 0.5;

    /// <summary>Below this throughput (passengers per minute) there is no service (reference 0.01).</summary>
    public double MinimumRate { get; init; } = 0.01;

    /// <summary>Cycle times below this (minutes) are not real and count as unknown (3 seconds).</summary>
    public double MinimumCycleMinutes { get; init; } = 0.05;

    /// <summary>Above this throughput (passengers per minute) the inputs are not real (a corrupt count): no number.</summary>
    public double MaximumRate { get; init; } = 1000;

    public IEnumerable<string> Problems()
    {
        if (!double.IsFinite(MinimumCycleMinutes) || MinimumCycleMinutes is <= 0 or > 10)
            yield return "MinimumCycleMinutes is above 0 and at most 10.";
        if (!double.IsFinite(Beta) || Beta is < 0 or > 1)
            yield return "Beta is from 0 to 1.";
        if (!double.IsFinite(MinimumRate) || MinimumRate is <= 0 or > 10)
            yield return "MinimumRate is above 0 and at most 10 passengers a minute.";
        if (!double.IsFinite(MaximumRate) || MaximumRate <= MinimumRate || MaximumRate > 100_000)
            yield return "MaximumRate is above MinimumRate and at most 100,000 passengers a minute.";
    }
}

/// <summary>
/// The nowcast of formula F8: W_now = (Q + 1) / mu, with mu = beta x n_open / c + (1 - beta) x exits in the last m
/// minutes / m; the desk term reacts the moment a desk opens or closes, the exit term damps noise (Little's law for the
/// person at the back of the queue). Variants: a fast-track merge scales mu by the standard lane's share, e-gates by
/// (1 - reject rate) (F12). No service (nothing open, or mu below the minimum rate) gives no number with a reason.
/// Pure.
/// </summary>
public static class Nowcast
{
    public static NowcastResult Compute(NowcastInput input, NowcastSettings settings = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        settings ??= new NowcastSettings();
        var problems = settings.Problems().ToList();
        if (problems.Count > 0)
            throw new ArgumentException(string.Join(" ", problems), nameof(settings));

        if (input.QueueLength is not { } q || q < 0)
            return new NowcastResult(null, null, null, NoServiceReason.NoQueueLength, input.Degraded);

        double? desks = null;
        if (input.OpenServers is { } open)
        {
            if (open <= 0)
                return new NowcastResult(null, 0, ThroughputSource.Desks, NoServiceReason.NothingOpen, input.Degraded);
            if (input.CycleMinutes is { } c && double.IsFinite(c) && c >= settings.MinimumCycleMinutes)
                desks = open / c;
        }

        // The exit window is whole minutes from 1 to 60, as ExitRate keeps them.
        double? exits = input.ExitsInWindow is { } x && x >= 0 && double.IsFinite(input.ExitWindowMinutes) && input.ExitWindowMinutes is >= 1 and <= 60
            ? x / input.ExitWindowMinutes
            : null;

        double mu;
        ThroughputSource source;
        var degraded = input.Degraded;
        switch (desks, exits)
        {
            case ({ } d, { } e):
                mu = settings.Beta * d + (1 - settings.Beta) * e;
                source = ThroughputSource.Blend;
                break;
            case ({ } d, null):
                mu = d;
                source = ThroughputSource.Desks;
                break;
            case (null, { } e):
                // Without desk state the estimate lags every desk change: flagged (F8 sensor-outage fallback, F11).
                mu = e;
                source = ThroughputSource.Exits;
                degraded = true;
                break;
            default:
                return new NowcastResult(null, null, null, NoServiceReason.NoThroughputData, degraded);
        }

        // An unknown share or reject rate is not assumed to be the optimistic value silently: it is flagged.
        if (!double.IsFinite(input.MergeShare) || !double.IsFinite(input.RejectRate))
            degraded = true;
        mu *= Math.Clamp(double.IsFinite(input.MergeShare) ? input.MergeShare : 1, 0, 1);
        mu *= 1 - Math.Clamp(double.IsFinite(input.RejectRate) ? input.RejectRate : 0, 0, 1);
        if (!double.IsFinite(mu) || mu > settings.MaximumRate)
            return new NowcastResult(null, null, source, NoServiceReason.Implausible, degraded);
        if (mu < settings.MinimumRate)
            return new NowcastResult(null, mu, source, NoServiceReason.ThroughputTooLow, degraded);
        var minutes = (q + 1.0) / mu;
        return double.IsFinite(minutes) && minutes >= 0
            ? new NowcastResult(minutes, mu, source, null, degraded)
            : new NowcastResult(null, mu, source, NoServiceReason.Implausible, degraded);
    }

    /// <summary>E-gate throughput (F12): gates in service over the gate cycle time, less the rejects.</summary>
    public static double? EgateRate(int gatesInService, double cycleMinutes, double rejectRate, double minimumCycleMinutes = 0.05)
    {
        if (gatesInService <= 0 || !double.IsFinite(cycleMinutes) || !double.IsFinite(rejectRate) ||
            !double.IsFinite(minimumCycleMinutes) || minimumCycleMinutes <= 0 || cycleMinutes < minimumCycleMinutes)
            return null;
        var rate = gatesInService / cycleMinutes * (1 - Math.Clamp(rejectRate, 0, 1));
        return double.IsFinite(rate) ? rate : null;
    }
}

/// <summary>What a passenger screen shows (F8 display rules).</summary>
public enum NowcastDisplayKind
{
    /// <summary>A neutral message: stale data or no service.</summary>
    Neutral,

    /// <summary>"Under 5 min".</summary>
    UnderFive,

    /// <summary>A 5-minute band, with hysteresis.</summary>
    Band,

    /// <summary>The wider band of a degraded zone.</summary>
    DegradedBand,

    /// <summary>Above the display ceiling ("over 120 min"): beyond it a number means nothing to a passenger.</summary>
    AboveCeiling
}

/// <summary>A displayed nowcast: the kind and its minutes ([From, To) for bands; the ceiling in From for AboveCeiling).</summary>
public sealed record NowcastDisplay(NowcastDisplayKind Kind, int? FromMinutes, int? ToMinutes);

/// <summary>
/// The display state of one screen or zone between updates: the reference value set when the displayed band last
/// changed (hysteresis), and what is shown.
/// </summary>
public sealed record NowcastDisplayState(double? ReferenceMinutes, NowcastDisplay Shown)
{
    public static NowcastDisplayState Initial { get; } = new(null, new NowcastDisplay(NowcastDisplayKind.Neutral, null, null));
}

/// <summary>
/// Passenger-screen display of the nowcast (F8): 5-minute bands that move only when the nowcast moves a full band,
/// never a realised number. Precedence (first match wins): stale data or no service shows the neutral message; a
/// W at or above the ceiling (120 minutes, the reference cap) shows "over 120 min", degraded or not; a degraded zone shows
/// the degraded band [5 floor(0.75 W / 5), 5 ceil(1.25 W / 5)], at least 10 minutes wide; W under 5 shows "under 5 min"; otherwise
/// band(W) = [5 floor(W / 5), + 5). Hysteresis (Proposed, extended here to the under-5 boundary and the degraded band so
/// that nothing flaps at an edge): what is shown changes only when |W - W_ref| >= 5 or its kind changes between
/// degraded and not, then W_ref = W.
/// </summary>
public static class NowcastDisplays
{
    private const int Width = 5;

    /// <summary>The display ceiling in minutes (the reference simulator's cap).</summary>
    public const int Ceiling = 120;

    public static NowcastDisplayState Next(NowcastDisplayState previous, NowcastResult nowcast, bool stale)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(nowcast);
        if (stale || nowcast.NoService is not null || nowcast.Minutes is not { } w || !double.IsFinite(w) || w < 0)
            return new NowcastDisplayState(null, new NowcastDisplay(NowcastDisplayKind.Neutral, null, null));

        var shownDegraded = previous.Shown.Kind == NowcastDisplayKind.DegradedBand;
        var shownLive = previous.Shown.Kind is NowcastDisplayKind.Band or NowcastDisplayKind.UnderFive;
        var shownCeiling = previous.Shown.Kind == NowcastDisplayKind.AboveCeiling;
        if (((nowcast.Degraded && shownDegraded) || (!nowcast.Degraded && shownLive) || shownCeiling) &&
            previous.ReferenceMinutes is { } reference && double.IsFinite(reference) && Math.Abs(w - reference) < Width)
            return previous;

        if (nowcast.Degraded && w < Ceiling)
        {
            var (from, to) = DegradedBand(w);
            return new NowcastDisplayState(w, new NowcastDisplay(NowcastDisplayKind.DegradedBand, from, to));
        }

        // At or beyond the ceiling a band would exclude the estimate: "over 120 min", degraded or not.
        if (w >= Ceiling)
            return new NowcastDisplayState(w, new NowcastDisplay(NowcastDisplayKind.AboveCeiling, Ceiling, null));
        if (w < Width)
            return new NowcastDisplayState(w, new NowcastDisplay(NowcastDisplayKind.UnderFive, 0, Width));
        var start = (int)Math.Floor(Math.Round(w, 9) / Width) * Width;
        return new NowcastDisplayState(w, new NowcastDisplay(NowcastDisplayKind.Band, start, start + Width));
    }

    /// <summary>
    /// The degraded band (reference): 0.75 W down and 1.25 W up to whole 5-minute steps, at least 10 minutes wide, and
    /// never beyond the ceiling.
    /// </summary>
    public static (int From, int To) DegradedBand(double minutes)
    {
        var w = Math.Clamp(double.IsFinite(minutes) ? minutes : Ceiling, 0, Ceiling);
        var from = (int)Math.Floor(Math.Round(0.75 * w / Width, 9)) * Width;
        var to = (int)Math.Ceiling(Math.Round(1.25 * w / Width, 9)) * Width;
        to = Math.Min(to, Ceiling);
        if (to - from < 2 * Width)
        {
            to = Math.Min(from + 2 * Width, Ceiling);
            from = to - 2 * Width;
        }

        return (from, to);
    }
}

/// <summary>Exits over a window: how many, how many minutes of it were observed, and how many exits came from degraded input.</summary>
public sealed record ExitWindow(long Exits, int ObservedMinutes, int WindowMinutes, long DegradedExits)
{
    /// <summary>Every minute of the window was observed: zero exits mean nobody left, not no data.</summary>
    public bool Complete => ObservedMinutes == WindowMinutes;
}

/// <summary>
/// The exit term of the throughput (F8): exits counted per minute from the queue engine's movements, summed over the
/// last m whole minutes before a time. A minute is observed when the engine reported it (with or without exits), so
/// that a gap after a restart or an outage reads as unknown rather than as zero exits. Keeps one hour of minutes,
/// with saturating counts.
/// </summary>
public sealed class ExitRate
{
    private const int KeptMinutes = 60;
    private readonly SortedDictionary<DateTime, (long Exits, long Degraded)> _minutes = [];

    /// <summary>Adds a step's movements (minutes already present are added to).</summary>
    public void Add(IEnumerable<MovementCount> movements)
    {
        ArgumentNullException.ThrowIfNull(movements);
        foreach (var m in movements)
        {
            if (m is null)
                continue;
            var current = _minutes.GetValueOrDefault(m.MinuteUtc);
            _minutes[m.MinuteUtc] = (Saturate(current.Exits, m.Exits), Saturate(current.Degraded, m.DegradedExits));
        }

        while (_minutes.Count > KeptMinutes)
            _minutes.Remove(_minutes.First().Key);
    }

    /// <summary>Marks minutes as observed without movements (the engine processed them and nobody crossed).</summary>
    public void Observed(DateTime fromUtc, DateTime toUtc)
    {
        var minute = new DateTime(fromUtc.Ticks - fromUtc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        // The latest hour of the span is what the window reads; earlier minutes would be forgotten at once.
        var last = new DateTime(toUtc.Ticks - toUtc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        if (last.Ticks - minute.Ticks > KeptMinutes * TimeSpan.TicksPerMinute)
            minute = last.AddMinutes(-KeptMinutes);
        for (var k = 0; minute < toUtc && k < KeptMinutes; k++)
        {
            _minutes.TryAdd(minute, (0, 0));
            if (DateTime.MaxValue.Ticks - minute.Ticks < TimeSpan.TicksPerMinute)
                break;
            minute = minute.AddMinutes(1);
        }
        while (_minutes.Count > KeptMinutes)
            _minutes.Remove(_minutes.First().Key);
    }

    private static long Saturate(long a, long b) => b > 0 && a > long.MaxValue - b ? long.MaxValue : a + Math.Max(0, b);

    /// <summary>The exits in the <paramref name="minutes"/> whole minutes before the minute of <paramref name="atUtc"/>.</summary>
    public ExitWindow Window(DateTime atUtc, int minutes)
    {
        if (minutes is < 1 or > KeptMinutes)
            throw new ArgumentOutOfRangeException(nameof(minutes), $"The window is from 1 to {KeptMinutes} minutes.");
        var end = new DateTime(atUtc.Ticks - atUtc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        var start = end.Ticks > minutes * TimeSpan.TicksPerMinute ? end.AddMinutes(-minutes) : DateTime.MinValue;
        long sum = 0, degraded = 0;
        var observed = 0;
        foreach (var (minute, counts) in _minutes)
        {
            if (minute < start || minute >= end)
                continue;
            sum = Saturate(sum, counts.Exits);
            degraded = Saturate(degraded, counts.Degraded);
            observed++;
        }

        return new ExitWindow(sum, observed, minutes, degraded);
    }

    /// <summary>The exits in the window (see <see cref="Window"/> for coverage and quality).</summary>
    public long ExitsBefore(DateTime atUtc, int minutes) => Window(atUtc, minutes).Exits;
}
