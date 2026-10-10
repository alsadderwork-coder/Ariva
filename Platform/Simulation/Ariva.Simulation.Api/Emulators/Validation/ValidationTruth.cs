using Ariva.Simulation.Api.Scenarios.Engine;

namespace Ariva.Simulation.Api.Emulators.Validation;

/// <summary>The desk states an observer logs (Ariva's observed desk states, ARV-104b).</summary>
public enum ObservedDeskState
{
    Closed,
    Idle,
    Serving,
    Paused
}

/// <summary>
/// One whole person of a queue: when they crossed its entry line and its exit line, UTC to the millisecond. <see cref="ExitedUtc"/>
/// is null when the day ends before they are served.
/// </summary>
internal readonly record struct Passage(DateTime EnteredUtc, DateTime? ExitedUtc)
{
    /// <summary>
    /// The realised wait in minutes, or null: never served, or an exit stamped before the entry (two people of one minute can
    /// cross in either order; Ariva's queue engine rejects such a pair rather than give it a wait, ARV-031).
    /// </summary>
    public double? WaitMinutes => ExitedUtc is { } exited && exited >= EnteredUtc ? (exited - EnteredUtc).TotalMinutes : null;
}

/// <summary>The people who entered a queue in one UTC minute, how many of them have a realised wait, and their mean wait in minutes.</summary>
public sealed record EntrantWaits(int Entries, int Waits, double? MeanWaitMinutes);

/// <summary>A sensor outage of the scenario day laid on real time: the sensor, the queue zone it counts for, and [from, to) in UTC.</summary>
public sealed record TruthOutage(string Sensor, string Zone, string QueueZone, DateTime FromUtc, DateTime ToUtc);

/// <summary>
/// The ground truth of a scenario day laid on real time (ARV-104i): the scenario's clock minute 0 (00:00) is
/// <see cref="DayStartUtc"/>, and every later instant follows it, as the sensor emulator lays the day when it plays at speed 1
/// (<see cref="Sensors.SensorTraffic"/>). People are the whole people of the fluid model exactly as the sensor emulator pushes
/// them: person n of a queue enters when the cumulative arrivals reach n and leaves when the cumulative departures reach n
/// (FIFO), at the same fraction of the minute, so a line's true count is the crossings the sensors report and a minute's true
/// wait is what Ariva's queue engine pairs from them. Desk states are what an observer standing at the desk sees, also when
/// the scenario hides a desk from the sensors (its "unknown" state). Pure: no I/O, no clock; not thread safe (a scenario day
/// is read under the engine's lock).
/// </summary>
internal sealed class ValidationTruth
{
    private const double Epsilon = 1e-9;
    private readonly ScenarioDay _day;

    public ValidationTruth(ScenarioDay day, DateTime dayStartUtc)
    {
        ArgumentNullException.ThrowIfNull(day);
        if (dayStartUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("The day's start is a UTC instant.", nameof(dayStartUtc));
        _day = day;
        DayStartUtc = dayStartUtc;
    }

    /// <summary>The UTC instant of the scenario day's clock minute 0 (00:00).</summary>
    public DateTime DayStartUtc { get; }

    /// <summary>The scenario site the day simulates.</summary>
    public ScenarioSite Site => _day.Site;

    /// <summary>The seed of the scenario day.</summary>
    public uint Seed => _day.Seed;

    #region Time

    /// <summary>The UTC instant of a clock minute of the day (fractional), cut to the whole millisecond as the sensors stamp it.</summary>
    public DateTime At(double clockMinute)
    {
        var ticks = DayStartUtc.Ticks + (long)Math.Round(clockMinute * TimeSpan.TicksPerMinute, MidpointRounding.AwayFromZero);
        return new DateTime(ticks - (ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);
    }

    /// <summary>The clock minute of the day (fractional) of a UTC instant.</summary>
    public double ClockOf(DateTime utc) => (utc.Ticks - DayStartUtc.Ticks) / (double)TimeSpan.TicksPerMinute;

    #endregion

    #region Queues

    /// <summary>The scenario queue a zone name stands for (Ariva's demo profiles name their queue zones after the scenario's queues), or null.</summary>
    public int? QueueOf(string zoneName) => zoneName is not null && Site.QueueIndex.TryGetValue(zoneName, out var q) ? q : null;

    /// <summary>The instants people crossed the queue's entry line in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>).</summary>
    public IReadOnlyList<DateTime> Entries(int q, DateTime fromUtc, DateTime toUtc) =>
        [.. Crossings(_day.CumA[q], _day.A[q], fromUtc, toUtc).Select(c => c.Utc)];

    /// <summary>The instants people crossed the queue's exit line in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>).</summary>
    public IReadOnlyList<DateTime> Exits(int q, DateTime fromUtc, DateTime toUtc) =>
        [.. Crossings(_day.CumD[q], _day.D[q], fromUtc, toUtc).Select(c => c.Utc)];

    /// <summary>Every person who entered the queue in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>), with the instant they left.</summary>
    public IReadOnlyList<Passage> Passages(int q, DateTime fromUtc, DateTime toUtc) =>
        [.. Crossings(_day.CumA[q], _day.A[q], fromUtc, toUtc).Select(c => new Passage(c.Utc, CrossingMinute(_day.CumD[q], _day.D[q], c.Person) is { } exit ? At(exit) : null))];

    /// <summary>The people who entered the queue in the UTC minute starting at <paramref name="minuteUtc"/> and their mean realised wait.</summary>
    public EntrantWaits EntrantWaits(int q, DateTime minuteUtc)
    {
        var people = Passages(q, minuteUtc, minuteUtc.AddMinutes(1));
        var waits = people.Select(p => p.WaitMinutes).Where(w => w is not null).Select(w => w.GetValueOrDefault()).ToList();
        return new EntrantWaits(people.Count, waits.Count, waits.Count > 0 ? waits.Average() : null);
    }

    /// <summary>
    /// The whole people crossing a cumulative curve in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>): person n crosses in
    /// the minute where the curve reaches n, at the fraction of that minute the sensor emulator gives it.
    /// </summary>
    private IEnumerable<(int Person, DateTime Utc)> Crossings(double[] cumulative, double[] perMinute, DateTime fromUtc, DateTime toUtc)
    {
        if (toUtc <= fromUtc)
            yield break;
        // The minutes that can hold such an instant, one either side, within the simulated run.
        var first = Math.Max(0, (int)Math.Floor(ClockOf(fromUtc)) - 1 + ScenarioModel.Pre);
        var last = Math.Min(perMinute.Length, (int)Math.Ceiling(ClockOf(toUtc)) + 1 + ScenarioModel.Pre);
        for (var i = first; i < last; i++)
        {
            var before = Whole(cumulative[i]);
            var after = Whole(cumulative[i + 1]);
            for (var n = before + 1; n <= after; n++)
            {
                var at = At(i - ScenarioModel.Pre + Fraction(cumulative, perMinute, i, n));
                if (at >= fromUtc && at < toUtc)
                    yield return (n, at);
            }
        }
    }

    /// <summary>The clock minute (fractional) at which a cumulative curve reaches whole person <paramref name="n"/>, or null when it never does.</summary>
    private static double? CrossingMinute(double[] cumulative, double[] perMinute, int n)
    {
        if (n < 1 || Whole(cumulative[^1]) < n)
            return null;
        // The first minute index whose end holds person n (the curve never decreases).
        int lo = 0, hi = perMinute.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (Whole(cumulative[mid + 1]) < n)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo - ScenarioModel.Pre + Fraction(cumulative, perMinute, lo, n);
    }

    /// <summary>The fraction of minute index <paramref name="i"/> at which person <paramref name="n"/> crosses (SensorTraffic's rule).</summary>
    private static double Fraction(double[] cumulative, double[] perMinute, int i, int n) =>
        perMinute[i] > Epsilon ? Math.Clamp((n - cumulative[i]) / perMinute[i], 0, 0.999) : 0;

    private static int Whole(double cumulative) => (int)Math.Floor(cumulative + Epsilon);

    #endregion

    #region Desks

    /// <summary>The queue and server of a desk code (Ariva's demo desks carry the scenario's server ids, AR-08), or null.</summary>
    public (int Q, int K)? DeskOf(string code)
    {
        if (string.IsNullOrEmpty(code))
            return null;
        for (var q = 0; q < Site.NQ; q++)
        {
            var k = IndexOf(Site.Queues[q].Servers, code);
            if (k >= 0)
                return (q, k);
        }

        return null;
    }

    private static int IndexOf(IReadOnlyList<string> servers, string code)
    {
        for (var k = 0; k < servers.Count; k++)
        {
            if (string.Equals(servers[k], code, StringComparison.Ordinal))
                return k;
        }

        return -1;
    }

    /// <summary>
    /// What an observer at server <paramref name="k"/> of queue <paramref name="q"/> sees in the UTC minute starting at
    /// <paramref name="minuteUtc"/> (the state of the scenario minute holding the minute's middle), or null outside the day.
    /// </summary>
    public ObservedDeskState? DeskState(int q, int k, DateTime minuteUtc)
    {
        var minute = (int)Math.Floor(ClockOf(minuteUtc.AddSeconds(30)));
        if (minute is < 0 or >= ScenarioModel.Day)
            return null;
        var server = _day.ServerStates(q, minute)[k];
        return Observed(server.State, _day.Servers[q][k].Pause[minute + ScenarioModel.Pre] != 0, _day.L[q][minute + ScenarioModel.Pre] > 0.5);
    }

    /// <summary>
    /// A scenario server state as an observer sees it: closed and out of service are Closed; a server the scenario hides from the
    /// sensors ("unknown") is seen as it is: Paused when it pauses, otherwise Serving while people queue and Idle when none do.
    /// </summary>
    public static ObservedDeskState Observed(string scenarioState, bool pausing, bool peopleQueue) => scenarioState switch
    {
        "serving" => ObservedDeskState.Serving,
        "idle" => ObservedDeskState.Idle,
        "paused" => ObservedDeskState.Paused,
        "unknown" when pausing => ObservedDeskState.Paused,
        "unknown" => peopleQueue ? ObservedDeskState.Serving : ObservedDeskState.Idle,
        _ => ObservedDeskState.Closed
    };

    #endregion

    #region Outages

    /// <summary>The scenario's sensor outages that overlap [<paramref name="fromUtc"/>, <paramref name="toUtc"/>), laid on real time (DMO: S-17 over A-VIS, 18:20 to 18:30).</summary>
    public IReadOnlyList<TruthOutage> Outages(DateTime fromUtc, DateTime toUtc) =>
    [
        .. Site.Outages
            .Select(o => new TruthOutage(o.Sensor, o.Zone, Site.Sensor(o.Sensor)?.QueueZone ?? o.Zone, At(o.From), At(o.To)))
            .Where(o => o.FromUtc < toUtc && o.ToUtc > fromUtc)
    ];

    #endregion
}
