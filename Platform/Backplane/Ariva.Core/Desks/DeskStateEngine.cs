namespace Ariva.Core.Desks;

/// <summary>A desk changed state (the <c>DeskStateChanged</c> event); no identities.</summary>
public sealed record DeskTransition(string DeskCode, string Lane, DeskStatus From, DeskStatus To, DateTime AtUtc, bool SensorDerived, bool PresentNotProcessing, bool Degraded);

/// <summary>
/// One desk over one minute: how long it spent in each state, the transactions completed, how long the state was only
/// sensor-derived or present-not-processing, and whether any of it was degraded. Per-desk minute aggregates only: no
/// officer, traveller or document identifiers.
/// </summary>
public sealed record DeskMinute(
    string DeskCode,
    string Lane,
    DateTime MinuteUtc,
    TimeSpan Closed,
    TimeSpan Idle,
    TimeSpan Serving,
    TimeSpan Paused,
    TimeSpan Unknown,
    int Transactions,
    TimeSpan SensorDerived,
    TimeSpan PresentNotProcessing,
    bool Degraded)
{
    /// <summary>tau (F9): the time the desk was open for throughput (Idle or Serving).</summary>
    public TimeSpan Open => Idle + Serving;
}

/// <summary>What one <see cref="DeskStateEngine.Advance"/> produced.</summary>
public sealed record DeskStep(DateTime WatermarkUtc, IReadOnlyList<DeskTransition> Transitions, IReadOnlyList<DeskMinute> Minutes, bool More);

/// <summary>Counts of what the engine did with the signals offered (for health and the bins' quality).</summary>
public sealed record DeskEngineCounters(
    long Accepted,
    long Late,
    long Superseded,
    long Future,
    long Invalid,
    long UnknownDesk,
    long BufferFull,
    long TooLate = 0,
    long SkippedMinutes = 0);

/// <summary>The desks of one lane at a moment: how many are open for throughput (F8 n_open) and whether any is Unknown.</summary>
public sealed record LaneDesks(string Lane, DateTime AtUtc, int Open, int Serving, int Idle, int Paused, int Closed, int Unknown, bool Degraded, int SensorDerived = 0)
{
    /// <summary>n_open of F8: desks Idle or Serving.</summary>
    public int OpenServers => Open;
}

/// <summary>
/// The desk state engine (F10): many desks, signals in event time reordered within a lateness allowance, the state of
/// each desk evaluated by <see cref="DeskRule"/> between signals and at the moments a threshold passes, so that the time
/// spent in each state is exact. Emits state transitions and closed per-desk minutes. Late signals (behind the
/// watermark) are applied at the desk's current time and do not revise minutes already emitted; signals older than
/// the latest of their kind are superseded (counted, not applied) so that a late logout cannot undo a newer login.
/// Bounded: desks, buffered signals and records per step. Not thread-safe; one engine per stream partition.
/// </summary>
public sealed partial class DeskStateEngine
{
    private readonly DeskStateSettings _settings;
    private readonly Dictionary<string, Desk> _desks = new(StringComparer.Ordinal);
    private readonly List<Desk> _order = [];
    private DateTime _watermark;
    private int _stepLimit;
    private long _sequence;
    private int _buffered;
    private long _accepted, _late, _superseded, _future, _invalid, _unknownDesk, _bufferFull, _tooLate, _skippedMinutes;

    private sealed class Desk(DeskProfile profile)
    {
        public DeskProfile Profile { get; } = profile;
        public DeskMemory Memory { get; set; } = DeskMemory.Empty;
        public DateTime Cursor { get; set; }
        public DeskEvaluation Current { get; set; }
        public PriorityQueue<(DeskSignal Signal, bool Ahead), (long Ticks, long Sequence)> Pending { get; } = new();
        public int Ahead { get; set; }
        public long[] Ticks { get; } = new long[5];
        public long SensorTicks { get; set; }
        public long PresentTicks { get; set; }
        public bool MinuteDegraded { get; set; }
        public int Transactions { get; set; }
    }

    public DeskStateEngine(IEnumerable<DeskProfile> desks, DateTime startUtc, DeskStateSettings settings = null)
    {
        ArgumentNullException.ThrowIfNull(desks);
        _settings = settings ?? new DeskStateSettings();
        var problems = _settings.Problems().ToList();
        if (problems.Count > 0)
            throw new ArgumentException(string.Join(" ", problems), nameof(settings));
        if (startUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("The start time is UTC.", nameof(startUtc));
        var start = Floor(startUtc);
        foreach (var profile in desks)
        {
            if (profile is null || string.IsNullOrWhiteSpace(profile.DeskCode) || profile.DeskCode.Length > 64 ||
                string.IsNullOrWhiteSpace(profile.Lane) || profile.Lane.Length > 64)
                throw new ArgumentException("Every desk has a code and a lane of at most 64 characters.", nameof(desks));
            if (!(profile.HasTransactions || profile.HasSession || profile.HasStaffZone || profile.HasServiceZone))
                throw new ArgumentException($"Desk {profile.DeskCode} has no signal source.", nameof(desks));
            if (_desks.Count >= _settings.MaxDesks)
                throw new ArgumentException($"At most {_settings.MaxDesks} desks.", nameof(desks));
            var desk = new Desk(profile) { Cursor = start };
            desk.Current = DeskRule.Evaluate(profile, desk.Memory, start, _settings);
            if (!_desks.TryAdd(profile.DeskCode, desk))
                throw new ArgumentException($"Desk {profile.DeskCode} is listed twice.", nameof(desks));
            _order.Add(desk);
        }

        _watermark = start;
    }

    /// <summary>The time up to which signals have been applied in order.</summary>
    public DateTime WatermarkUtc => _watermark;

    public DeskEngineCounters Counters => new(_accepted, _late, _superseded, _future, _invalid, _unknownDesk, _bufferFull, _tooLate, _skippedMinutes);

    /// <summary>
    /// Offers one signal. <paramref name="referenceUtc"/> is Ariva's clock when it arrived; signals further ahead than
    /// <see cref="DeskStateSettings.MaxAhead"/> are refused, and signals behind the watermark are late.
    /// </summary>
    public void Offer(DeskSignal signal, DateTime referenceUtc)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (referenceUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("The reference time is UTC.", nameof(referenceUtc));
        if (signal.DeskCode is null || !_desks.TryGetValue(signal.DeskCode, out var desk))
        {
            _unknownDesk++;
            return;
        }

        if (signal.TimeUtc.Kind != DateTimeKind.Utc || !Valid(signal, desk.Profile))
        {
            _invalid++;
            return;
        }

        if (signal.TimeUtc > Plus(referenceUtc, _settings.MaxAhead))
        {
            _future++;
            return;
        }

        if (signal.TimeUtc < Minus(_watermark, _settings.MaxLate))
        {
            _tooLate++;
            return;
        }

        // Fairness: a desk holds at most its own cap, a tenth of it ahead of the reference clock, and once the engine's
        // buffer is half full no more than an equal share of it.
        var ahead = signal.TimeUtc > referenceUtc;
        var perDesk = Math.Min(_settings.MaxBufferedSignalsPerDesk, _settings.MaxBufferedSignals);
        var share = Math.Max(10, _settings.MaxBufferedSignals / 2 / Math.Max(1, _order.Count));
        if (_buffered >= _settings.MaxBufferedSignals || desk.Pending.Count >= perDesk ||
            (ahead && desk.Ahead >= Math.Max(1, perDesk / 10)) ||
            (_buffered >= _settings.MaxBufferedSignals / 2 && desk.Pending.Count >= share))
        {
            _bufferFull++;
            return;
        }

        if (signal.TimeUtc < _watermark)
            _late++;
        desk.Pending.Enqueue((signal, ahead), (signal.TimeUtc.Ticks, _sequence++));
        if (ahead)
            desk.Ahead++;
        _buffered++;
        _accepted++;
    }

    private bool Valid(DeskSignal signal, DeskProfile profile) => signal switch
    {
        DeskZoneReading z => z.Zone is DeskSource.StaffZone or DeskSource.ServiceZone && profile.Has(z.Zone) &&
                             z.Count >= 0 && z.Count <= _settings.MaxZoneCount,
        DeskTransactionsCompleted c => profile.HasTransactions && c.Count >= 0 && c.Count <= _settings.MaxTransactionsPerSignal,
        DeskSessionChangedSignal s => profile.HasSession && Enum.IsDefined(s.State),
        DeskHeartbeat h => Enum.IsDefined(h.Of) && profile.Has(h.Of),
        DeskTransactionStarted or DeskTransactionEnded => profile.HasTransactions,
        _ => false
    };

    /// <summary>
    /// Applies the signals up to the reference time less the lateness allowance and moves every desk there, returning
    /// the transitions and the minutes closed. When the step reaches <see cref="DeskStateSettings.MaxStepRecords"/> it
    /// ends early with <see cref="DeskStep.More"/>; call again to continue.
    /// </summary>
    public DeskStep Advance(DateTime referenceUtc) => Advance(referenceUtc, _settings.MaxStepRecords);

    /// <summary>
    /// As <see cref="Advance(DateTime)"/>, with a step of at most <paramref name="maxRecords"/> records (transitions and
    /// minutes together, never more than <see cref="DeskStateSettings.MaxStepRecords"/>), so that a caller with its own
    /// budget (the desk feed's minutes per read, CWE-120) takes exactly what the budget leaves; the rest follows on the
    /// next call (<see cref="DeskStep.More"/>).
    /// </summary>
    public DeskStep Advance(DateTime referenceUtc, int maxRecords)
    {
        if (referenceUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("The reference time is UTC.", nameof(referenceUtc));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRecords, 1);
        _stepLimit = Math.Min(maxRecords, _settings.MaxStepRecords);
        var target = Minus(referenceUtc, _settings.Lateness);
        if (target > _watermark)
            _watermark = target;
        var transitions = new List<DeskTransition>();
        var minutes = new List<DeskMinute>();
        var more = false;
        foreach (var desk in _order)
        {
            if (!Run(desk, _watermark, transitions, minutes))
            {
                more = true;
                break;
            }
        }

        return new DeskStep(_watermark, transitions, minutes, more);
    }

    /// <summary>The state of a desk at its current time, or null for an unknown desk code.</summary>
    public DeskEvaluation Status(string deskCode) =>
        deskCode is not null && _desks.TryGetValue(deskCode, out var desk) ? desk.Current : null;

    /// <summary>The desks of a lane at the engine's current time (F8 n_open; any Unknown desk makes the lane Degraded).</summary>
    public LaneDesks Lane(string lane)
    {
        int open = 0, serving = 0, idle = 0, paused = 0, closed = 0, unknown = 0, sensorDerived = 0;
        var degraded = false;
        var at = _watermark;
        foreach (var desk in _order)
        {
            if (!string.Equals(desk.Profile.Lane, lane, StringComparison.Ordinal))
                continue;
            if (desk.Cursor < at)
                at = desk.Cursor;
            var e = desk.Current;
            // A sensor-derived state on healthy sensors is not degraded (F11); it is counted.
            degraded |= e.Degraded;
            if (e.SensorDerived)
                sensorDerived++;
            switch (e.Status)
            {
                case DeskStatus.Serving: serving++; open++; break;
                case DeskStatus.Idle: idle++; open++; break;
                case DeskStatus.Paused: paused++; break;
                case DeskStatus.Closed: closed++; break;
                default: unknown++; degraded = true; break;
            }
        }

        return new LaneDesks(lane, at, open, serving, idle, paused, closed, unknown, degraded, sensorDerived);
    }

    // Applies a desk's due signals in order and moves it to the target; false when the step is full.
    private bool Run(Desk desk, DateTime target, List<DeskTransition> transitions, List<DeskMinute> minutes)
    {
        // Far behind (a stopped host, a clock jump): skip the gap, counted, instead of emitting every minute of it. The
        // partial minute is dropped; the sources are stale by then, so the desk resumes Unknown until it is heard.
        var resume = Floor(Minus(target, _settings.MaxCatchUp));
        if (resume > desk.Cursor)
        {
            _skippedMinutes += (resume - Floor(desk.Cursor)).Ticks / TimeSpan.TicksPerMinute;
            Array.Clear(desk.Ticks);
            desk.SensorTicks = desk.PresentTicks = 0;
            desk.MinuteDegraded = false;
            desk.Transactions = 0;
            desk.Cursor = resume;
        }

        while (desk.Pending.TryPeek(out var item, out _) && item.Signal.TimeUtc <= target)
        {
            var signal = item.Signal;
            if (signal.TimeUtc > desk.Cursor && !Move(desk, signal.TimeUtc, transitions, minutes))
                return false;
            if (transitions.Count + minutes.Count >= _stepLimit)
                return false;

            // Every signal of the same moment is applied before the state is read, so that no zero-length state shows.
            var moment = signal.TimeUtc;
            while (desk.Pending.TryPeek(out var same, out _) && same.Signal.TimeUtc <= moment)
            {
                desk.Pending.Dequeue();
                _buffered--;
                if (same.Ahead)
                    desk.Ahead--;
                Apply(desk, same.Signal);
            }

            // A drained queue gives its memory back, so that a past flood is not held for good.
            if (desk.Pending.Count == 0)
                desk.Pending.TrimExcess();
            Refresh(desk, transitions);
        }

        return Move(desk, target, transitions, minutes);
    }

    // Moves a desk's time forward, splitting at minute boundaries and at the moments its state can change by itself.
    private bool Move(Desk desk, DateTime to, List<DeskTransition> transitions, List<DeskMinute> minutes)
    {
        while (desk.Cursor < to)
        {
            if (transitions.Count + minutes.Count >= _stepLimit)
                return false;
            Refresh(desk, transitions);
            var floor = Floor(desk.Cursor);
            var minuteEnd = floor.Ticks <= DateTime.MaxValue.Ticks - TimeSpan.TicksPerMinute ? floor.AddMinutes(1) : DateTime.MaxValue;
            var next = minuteEnd < to ? minuteEnd : to;
            if (DeskRule.NextChange(desk.Profile, desk.Memory, desk.Cursor, _settings) is { } change && change < next)
                next = change;
            var span = (next - desk.Cursor).Ticks;
            var e = desk.Current;
            desk.Ticks[(int)e.Status] += span;
            if (e.SensorDerived)
                desk.SensorTicks += span;
            if (e.PresentNotProcessing)
                desk.PresentTicks += span;
            desk.MinuteDegraded |= e.Degraded || e.Status == DeskStatus.Unknown;
            desk.Cursor = next;
            if (next == minuteEnd)
                minutes.Add(CloseMinute(desk, floor));
        }

        Refresh(desk, transitions);
        return true;
    }

    private void Refresh(Desk desk, List<DeskTransition> transitions)
    {
        var e = DeskRule.Evaluate(desk.Profile, desk.Memory, desk.Cursor, _settings);
        if (e.Status != desk.Current.Status)
            transitions.Add(new DeskTransition(desk.Profile.DeskCode, desk.Profile.Lane, desk.Current.Status, e.Status, desk.Cursor, e.SensorDerived, e.PresentNotProcessing, e.Degraded));
        desk.Current = e;
    }

    private static DeskMinute CloseMinute(Desk desk, DateTime minute)
    {
        var t = desk.Ticks;
        var record = new DeskMinute(desk.Profile.DeskCode, desk.Profile.Lane, minute,
            new TimeSpan(t[(int)DeskStatus.Closed]), new TimeSpan(t[(int)DeskStatus.Idle]), new TimeSpan(t[(int)DeskStatus.Serving]),
            new TimeSpan(t[(int)DeskStatus.Paused]), new TimeSpan(t[(int)DeskStatus.Unknown]), desk.Transactions,
            new TimeSpan(desk.SensorTicks), new TimeSpan(desk.PresentTicks), desk.MinuteDegraded);
        Array.Clear(t);
        desk.SensorTicks = desk.PresentTicks = 0;
        desk.MinuteDegraded = false;
        desk.Transactions = 0;
        return record;
    }

    // Applies one signal at its own time (a late one is no older than what it would supersede).
    private void Apply(Desk desk, DeskSignal signal)
    {
        var m = desk.Memory;
        var t = signal.TimeUtc;
        var staffWasStale = m.StaffHeard is not { } sh || t - sh >= _settings.StaleAfter;
        switch (signal)
        {
            case DeskHeartbeat:
                break;
            case DeskSessionChangedSignal s:
                if (m.SessionSignalAt is { } at && at > t)
                {
                    _superseded++;
                    break;
                }

                m = m with { SessionSignalAt = t };
                if (m.Session != s.State)
                    m = m with { Session = s.State, SessionSince = t };
                if (s.State == DeskSessionSignal.Closed)
                    m = m with { TransactionSince = null };
                break;
            case DeskTransactionStarted:
                m = m with { LastTransaction = Later(m.LastTransaction, t) };
                if (m.TransactionSignalAt is { } ts && ts > t)
                {
                    _superseded++;
                    break;
                }

                m = m with { TransactionSince = t, TransactionSignalAt = t };
                break;
            case DeskTransactionEnded:
                desk.Transactions = desk.Transactions == int.MaxValue ? int.MaxValue : desk.Transactions + 1;
                m = m with { LastTransaction = Later(m.LastTransaction, t) };
                if (m.TransactionSignalAt is { } te && te > t)
                {
                    _superseded++;
                    break;
                }

                m = m with { TransactionSince = null, TransactionSignalAt = t };
                break;
            case DeskTransactionsCompleted c:
                desk.Transactions = (int)Math.Min(int.MaxValue, (long)desk.Transactions + c.Count);
                if (c.Count > 0)
                    m = m with { LastTransaction = Later(m.LastTransaction, t) };
                break;
            case DeskZoneReading { Zone: DeskSource.StaffZone } z:
                if (m.StaffReadAt is { } sr && sr > t)
                {
                    _superseded++;
                    break;
                }

                // Emptiness is counted from the first empty reading after an occupied, unknown or stale stretch; the staff
                // left only when it follows a fresh occupied reading.
                var continuing = !staffWasStale && m.StaffCount == 0 && m.StaffEmptySince is not null;
                var left = z.Count == 0 && (continuing ? m.StaffLeft : !staffWasStale && m.StaffCount > 0);
                var emptySince = z.Count > 0 ? null : continuing ? m.StaffEmptySince : t;
                m = m with { StaffCount = z.Count, StaffReadAt = t, StaffEmptySince = emptySince, StaffLeft = left, StaffDegraded = z.Degraded };
                break;
            case DeskZoneReading z:
                if (m.ServiceReadAt is { } vr && vr > t)
                {
                    _superseded++;
                    break;
                }

                m = m with { ServiceCount = z.Count, ServiceReadAt = t, ServiceDegraded = z.Degraded };
                break;
        }

        desk.Memory = m.WithHeard(signal.Source, t);
    }

    private static DateTime? Later(DateTime? a, DateTime b) => a is { } x && x > b ? x : b;

    private static DateTime Floor(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);

    private static DateTime Plus(DateTime t, TimeSpan d) => t > DateTime.MaxValue - d ? DateTime.MaxValue : t + d;

    private static DateTime Minus(DateTime t, TimeSpan d) => t < DateTime.MinValue + d ? DateTime.MinValue : t - d;
}
