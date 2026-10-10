namespace Ariva.Core.Desks;

/// <summary>
/// The sensor-only desk engine of one site (ARV-117a, F10 rows 1 and 4 to 7 without a login or transaction source): a
/// <see cref="DeskStateEngine"/> whose desks have their staff and service zones as their only sources, run beside the
/// published engine in the desk feed. While AMAN's session is live the published engine takes AMAN's rank, so its
/// minutes record no sensor-derived time for a desk with an AMAN code; this engine never sees AMAN, so its minutes are
/// what the zones alone say, AMAN or not. Its minutes feed only the shadow nowcast's desk term
/// (<c>DeskTerms.SensorOnly</c>, ARV-117) and the pilot's validation comparison (ARV-104f); they are never shown, never an
/// alert input and never in a report.
/// <para>
/// Fed only by staff and service zone readings (CWE-501): <see cref="Offer"/> applies a <see cref="DeskZoneReading"/> and
/// refuses, counted, any other signal (an AMAN session, transaction, statistic or heartbeat), so no AMAN input can change
/// its output. Bounded (CWE-120): at most <see cref="MaxDesks"/> desks per site (beyond, desks are left out in key order,
/// counted), each holding one open minute, and the inner engine's caps on buffered readings (per desk and per engine)
/// and on the records of one step. A restored snapshot is checked as the inner engine checks it, and also refused when
/// it holds any AMAN memory (a session, a transaction, a transaction count). Pure: no I/O and no clock.
/// </para>
/// </summary>
public sealed class SensorOnlyDeskEngine
{
    /// <summary>Desks one site's sensor-only engine holds at most (Proposed; 2,000 desks is far beyond any airport's checkpoint).</summary>
    public const int MaxDesks = 2_000;

    private readonly DeskStateEngine _engine;
    private long _refused;

    private SensorOnlyDeskEngine(DeskStateEngine engine, int desks, int leftOut)
    {
        _engine = engine;
        Desks = desks;
        LeftOut = leftOut;
    }

    /// <summary>The desks the engine holds.</summary>
    public int Desks { get; }

    /// <summary>The desks with zones left out because the site has more than <see cref="MaxDesks"/>.</summary>
    public int LeftOut { get; }

    /// <summary>Signals offered that are not zone readings (never applied).</summary>
    public long Refused => _refused;

    /// <summary>The inner engine's counters (accepted, late, buffer full and the rest).</summary>
    public DeskEngineCounters Counters => _engine.Counters;

    public DateTime WatermarkUtc => _engine.WatermarkUtc;

    /// <summary>
    /// The sensor-only profiles of a site's desks: every desk with a staff or service zone, with those zones as its only
    /// sources (no session, no transactions), ordered by key, at most <paramref name="maxDesks"/>; the rest are counted.
    /// </summary>
    public static (IReadOnlyList<DeskProfile> Desks, int LeftOut) Profiles(IEnumerable<DeskProfile> desks, int maxDesks = MaxDesks)
    {
        ArgumentNullException.ThrowIfNull(desks);
        if (maxDesks is < 1 or > MaxDesks)
            throw new ArgumentOutOfRangeException(nameof(maxDesks), $"A sensor-only engine holds 1 to {MaxDesks} desks.");
        var zoned = desks.Where(d => d is not null && (d.HasStaffZone || d.HasServiceZone) && DeskKeys.Fits(d.DeskCode))
            .GroupBy(d => d.DeskCode, StringComparer.Ordinal).Select(g => g.First())
            .OrderBy(d => d.DeskCode, StringComparer.Ordinal)
            .Select(d => new DeskProfile(d.DeskCode, d.Lane, HasTransactions: false, HasSession: false, HasStaffZone: d.HasStaffZone, HasServiceZone: d.HasServiceZone))
            .ToList();
        return zoned.Count <= maxDesks ? (zoned, 0) : (zoned.Take(maxDesks).ToList(), zoned.Count - maxDesks);
    }

    /// <summary>The engine's settings: the given ones, with at most <see cref="MaxDesks"/> desks.</summary>
    public static DeskStateSettings Bounded(DeskStateSettings settings)
    {
        var s = settings ?? new DeskStateSettings();
        return s.MaxDesks <= MaxDesks ? s : s with { MaxDesks = MaxDesks };
    }

    /// <summary>A fresh engine for the site's desks (any desk profile; <see cref="Profiles"/> makes them sensor-only) at <paramref name="startUtc"/>.</summary>
    public static SensorOnlyDeskEngine Start(IEnumerable<DeskProfile> desks, DateTime startUtc, DeskStateSettings settings)
    {
        var bounded = Bounded(settings);
        var (profiles, leftOut) = Profiles(desks, bounded.MaxDesks);
        return new SensorOnlyDeskEngine(new DeskStateEngine(profiles, startUtc, bounded), profiles.Count, leftOut);
    }

    /// <summary>
    /// The engine in a captured state. Refused (<see cref="InvalidDataException"/>) when the inner engine refuses it (a
    /// version, a watermark, more desks or buffered readings than the caps, a reading the engine would refuse) or when a
    /// desk holds any AMAN memory, or an open state (or open time in its minute) that is not sensor-derived: the caller then
    /// rebuilds the engine by replaying the stored readings.
    /// </summary>
    public static SensorOnlyDeskEngine Restore(IEnumerable<DeskProfile> desks, DeskStateSettings settings, DeskEngineState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var bounded = Bounded(settings);
        if ((state.Desks?.Count ?? 0) > bounded.MaxDesks)
            throw new InvalidDataException("The sensor-only desk snapshot holds more desks than the engine keeps.");
        foreach (var desk in state.Desks ?? [])
        {
            var m = desk?.Memory;
            if (m is null)
                continue;
            if (m.Session is not null || m.SessionSince is not null || m.SessionSignalAt is not null || m.SessionHeard is not null ||
                m.TransactionSince is not null || m.TransactionSignalAt is not null || m.LastTransaction is not null || m.TransactionsHeard is not null ||
                desk.Transactions != 0 || desk.PresentTicks != 0 || desk.Current is { PresentNotProcessing: true })
                throw new InvalidDataException("The sensor-only desk snapshot holds AMAN memory.");
            // Without a login or transaction source every open state (Serving, Idle) is sensor-derived (F10 rows 6 and 7), so
            // an open state or open time that is not comes from a source this engine never has.
            if (desk.Current is { Status: DeskStatus.Serving or DeskStatus.Idle, SensorDerived: false } ||
                (desk.Ticks is { Count: 5 } t && t[(int)DeskStatus.Serving] + t[(int)DeskStatus.Idle] > desk.SensorTicks))
                throw new InvalidDataException("The sensor-only desk snapshot holds an open state that is not sensor-derived.");
        }

        var (profiles, leftOut) = Profiles(desks, bounded.MaxDesks);
        return new SensorOnlyDeskEngine(DeskStateEngine.Restore(profiles, bounded, state), profiles.Count, leftOut);
    }

    /// <summary>
    /// Offers a signal: a staff or service zone reading goes to the engine (which checks it as for any desk); anything
    /// else (an AMAN session, transaction, statistic or heartbeat) is refused and counted.
    /// </summary>
    public void Offer(DeskSignal signal, DateTime referenceUtc)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (signal is DeskZoneReading reading)
            _engine.Offer(reading, referenceUtc);
        else
            _refused++;
    }

    /// <summary>Applies the readings up to the reference time less the lateness allowance (see <see cref="DeskStateEngine.Advance"/>).</summary>
    public DeskStep Advance(DateTime referenceUtc) => _engine.Advance(referenceUtc);

    /// <summary>As <see cref="Advance(DateTime)"/>, with at most <paramref name="maxRecords"/> records in the step (see <see cref="DeskStateEngine.Advance(DateTime, int)"/>).</summary>
    public DeskStep Advance(DateTime referenceUtc, int maxRecords) => _engine.Advance(referenceUtc, maxRecords);

    /// <summary>The state of one desk at the engine's current time, or null for a desk it does not hold.</summary>
    public DeskEvaluation Status(string deskCode) => _engine.Status(deskCode);

    public DeskEngineState Capture() => _engine.Capture();
}
