using Ariva.Core.Domain.Enums;
using Ariva.Core.Sensing;

namespace Ariva.Core.Queueing;

/// <summary>
/// Settings of the queue state engine. Lateness, censoring and hand-over values are Proposed in formulas F5 and F6 and
/// tuned per site; the bounds keep one zone's state, its work per event and each step's output finite whatever the
/// input, including input that passed Ingest's checks from a faulty or compromised device.
/// </summary>
public sealed record QueueEngineSettings
{
    /// <summary>
    /// How far behind the zone's clock an event may be and still be processed in order (D5 lateness allowance, ARV-148):
    /// the watermark is the zone's clock less this. At least the sensors' push interval, since a push carries events up to
    /// that old; by default the one-minute push interval plus the 30 seconds allowed before for transport and clocks.
    /// Tuned per site (<c>Stream:Sites</c>).
    /// </summary>
    public TimeSpan Lateness { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>
    /// The sensors' push interval (ARV-148): a push covers this long before its receive time. The zone's clock never falls
    /// further than this behind Ariva's receive clock, so a zone whose devices send no events still closes its minutes;
    /// and a device's outage ends at the start of the minute its first push after it covers (F11). Zero makes the zone's
    /// clock the receive clock (the rule before ARV-148).
    /// </summary>
    public TimeSpan PushInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>T_censor: an entrant still unresolved this long after entry is censored (F6, Proposed 120 min).</summary>
    public TimeSpan CensorAfter { get; init; } = TimeSpan.FromMinutes(120);

    /// <summary>A track with position samples not seen for this long is fragmented (F6, To confirm).</summary>
    public TimeSpan HandoverWindow { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Events further ahead of the reference clock than this are refused (the 5 minutes of Ingest).</summary>
    public TimeSpan MaxAhead { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Occupancy readings older than this (behind the watermark) no longer give the queue length.</summary>
    public TimeSpan OccupancyFreshFor { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>A re-anchor dropping more than this many people flags the FIFO waits that follow as Degraded (F5).</summary>
    public int ResidualTolerance { get; init; } = 2;

    /// <summary>People held at most per zone; beyond it the earliest is censored.</summary>
    public int MaxOpenEntrants { get; init; } = 20_000;

    /// <summary>Events held at most for reordering; a tenth of it at most for events ahead of the reference clock.</summary>
    public int MaxBufferedEvents { get; init; } = 100_000;

    /// <summary>Recently resolved tracks remembered to recognise duplicates.</summary>
    public int MaxRememberedTracks { get; init; } = 20_000;

    /// <summary>
    /// ARV-230: tracks whose position samples are coalesced at most (the engine's remarks); the samples of further tracks are
    /// buffered one place each. The pilot hall's busiest zone (20 T3 sensors at 30 people) holds about 850.
    /// </summary>
    public int MaxSightingRuns { get; init; } = 20_000;

    /// <summary>Late events older than this behind the reference clock are beyond recomputation (the 3 days the sensing topics and the archive keep).</summary>
    public TimeSpan LateHorizon { get; init; } = TimeSpan.FromDays(3);

    /// <summary>Waits, resolutions and minutes one step holds at most; beyond it the step ends early (<see cref="QueueStep.More"/>).</summary>
    public int MaxStepRecords { get; init; } = 100_000;

    /// <summary>People one interval reading may count in or out; more is implausible for one line in one interval.</summary>
    public int MaxIntervalPeople { get; init; } = 5_000;

    /// <summary>Devices whose tracks a zone keeps apart; the tracks of further devices count as anonymous.</summary>
    public int MaxDevicesPerZone { get; init; } = 64;

    /// <summary>
    /// ARV-230: each track's position samples are coalesced in runs (the engine's remarks). Off only in tests, which compare the
    /// engine with one that applies every sample, as before ARV-230.
    /// </summary>
    internal bool CoalescePositions { get; init; } = true;

    /// <summary>The problems with these settings, empty when valid.</summary>
    public IEnumerable<string> Problems()
    {
        if (Lateness < TimeSpan.Zero || Lateness > TimeSpan.FromMinutes(30))
            yield return "Lateness is from 0 to 30 minutes.";
        if (PushInterval < TimeSpan.Zero || PushInterval > TimeSpan.FromMinutes(10))
            yield return "PushInterval is from 0 to 10 minutes.";
        if (CensorAfter < TimeSpan.FromMinutes(1) || CensorAfter > TimeSpan.FromHours(24))
            yield return "CensorAfter is from 1 minute to 24 hours.";
        if (HandoverWindow < TimeSpan.Zero || HandoverWindow > TimeSpan.FromMinutes(30))
            yield return "HandoverWindow is from 0 to 30 minutes.";
        if (MaxAhead < TimeSpan.Zero || MaxAhead > TimeSpan.FromHours(1))
            yield return "MaxAhead is from 0 to 1 hour.";
        if (OccupancyFreshFor <= TimeSpan.Zero || OccupancyFreshFor > TimeSpan.FromHours(1))
            yield return "OccupancyFreshFor is from 1 tick to 1 hour.";
        if (ResidualTolerance is < 0 or > 1000)
            yield return "ResidualTolerance is from 0 to 1000.";
        if (MaxOpenEntrants is < 1 or > 100_000)
            yield return "MaxOpenEntrants is from 1 to 100,000.";
        if (MaxBufferedEvents is < 10 or > 1_000_000)
            yield return "MaxBufferedEvents is from 10 to 1,000,000.";
        if (MaxRememberedTracks is < 0 or > 100_000)
            yield return "MaxRememberedTracks is from 0 to 100,000.";
        if (MaxSightingRuns is < 0 or > 1_000_000)
            yield return "MaxSightingRuns is from 0 to 1,000,000.";
        if (MaxStepRecords is < 1_000 or > 1_000_000)
            yield return "MaxStepRecords is from 1,000 to 1,000,000.";
        if (MaxIntervalPeople is < 1 or > CanonicalEventRules.MaxIntervalCount)
            yield return $"MaxIntervalPeople is from 1 to {CanonicalEventRules.MaxIntervalCount}.";
        if (MaxDevicesPerZone is < 1 or > 1024)
            yield return "MaxDevicesPerZone is from 1 to 1024.";
        if (LateHorizon < TimeSpan.FromMinutes(1) || LateHorizon > TimeSpan.FromDays(7))
            yield return "LateHorizon is from 1 minute to 7 days.";
    }
}

/// <summary>
/// The queue state of one queue zone (ARV-030): a pure engine, no I/O, time passed in, bounded state and work.
/// <para>
/// Events are offered as they arrive and held in a reorder buffer; <see cref="Advance(DateTime)"/> moves the watermark to the
/// zone's clock minus the lateness allowance and processes every held event up to it in event-time order. The zone's
/// clock runs on event time (ARV-148): it is the latest time of the events accepted, each counted at most up to the
/// reference time it arrived with (CWE-501: a device whose clock runs ahead cannot carry the watermark past Ariva's
/// receive clock and close other devices' minutes early), and once the zone has had its first event the clock never
/// falls more than the push interval behind the reference time, so a zone whose devices go quiet still moves on. Events of one instant are applied in a fixed order of
/// their content (interval readings of an entry line first, by the zone's geometry: ARV-173), then arrival, so the batches
/// and topics that carried them never change the result. Events that
/// arrive at or behind the watermark are late and counted, not applied: the minutes they touch are already processed,
/// and a recomputation decides (F6 revisions). The watermark never passes the reference time minus the lateness,
/// whatever the buffer holds.
/// </para>
/// <para>
/// Entries are inward crossings of an entry line (or of an overflow band's entry line, for a tracked person: overflow
/// time counts as wait, F5) and the "in" counts of interval readings; exits are outward crossings of an exit line and
/// "out" counts. A tracked exit closes its own track (T3, the primary method); any other exit pairs with the earliest
/// entrant still held (FIFO; with interval counts spread over their interval this is the cumulative-curve method, T1),
/// except that a tracked exit never takes a person the same device is tracking. An interval reading ending at T is spread
/// over the part of it after the last event processed before T's instant and after the watermark, so it never reaches into
/// minutes already processed and no event of its own instant narrows it: readings that end together (the entry and exit
/// lines of one T1 push) each spread over their own interval (ARV-173). A counted exit whose FIFO partner entered after it
/// is unmatched, as in time order: nobody counted in was waiting then. Leaving backwards
/// over an entry line abandons (the tracked person, or the latest anonymous entrant); entrants not resolved within
/// T_censor are censored; a tracked person with position samples not seen within the hand-over window is fragmented;
/// when every zone of the queue reports zero occupancy the FIFO sequence is re-anchored (F5), checked once every event of
/// the reading's instant has been applied, so the order in which the zones' readings of one instant arrive never matters;
/// when a full step ends inside that instant the check stays pending in the engine's state (and its snapshot) until the
/// rest of the instant has been applied (ARV-114d).
/// </para>
/// Anonymous crossings of an overflow band's entry line are not counted: without a track the same person crosses the
/// queue's entry line later, and counting both would double the entries.
/// </summary>
public sealed partial class QueueStateEngine
{
    private const string Anonymous = "";
    private const string Unnamespaced = "/";

    private sealed class Entrant(DateTime entryUtc, string trackKey, string group, bool degraded, WaitMethod method, long order)
    {
        public DateTime EntryUtc { get; } = entryUtc;
        public string TrackKey { get; } = trackKey;
        public string Group { get; } = group;
        public bool Degraded { get; } = degraded;
        public WaitMethod Method { get; } = method;
        public long Order { get; } = order;
        public DateTime? LastSeenUtc { get; set; }
        public LinkedListNode<Entrant> InAll { get; set; }
        public LinkedListNode<Entrant> InGroup { get; set; }
    }

    /// <summary>ARV-230: a track's run of position samples (see <see cref="Hold"/>).</summary>
    private sealed class SightingRun(string track, DateTime anchor)
    {
        public string Track { get; } = track;
        public DateTime Anchor { get; set; } = anchor;
        public QueueTrackSeen Pending { get; set; }
        public long PendingSequence { get; set; }
        public bool FlushScheduled { get; set; }
        public bool EndScheduled { get; set; }
    }

    private readonly QueueZoneGeometry _geometry;
    private readonly QueueEngineSettings _settings;
    private readonly PriorityQueue<(QueueInput Input, bool Ahead), Due> _buffer = new(DueOrder.Instance);
    private readonly LinkedList<Entrant> _held = new();
    private readonly Dictionary<string, LinkedList<Entrant>> _groups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entrant> _byTrack = new(StringComparer.Ordinal);
    private readonly HashSet<string> _resolvedTracks = new(StringComparer.Ordinal);
    private readonly Queue<string> _resolvedOrder = new();
    private readonly Dictionary<string, (int Count, DateTime AtUtc, bool Degraded)> _occupancy = new(StringComparer.Ordinal);

    private long _sequence;
    private long _order;
    private int _aheadBuffered;
    private int _degradedHeld;
    private DateTime _watermark = DateTime.MinValue;
    // The zone's event clock (ARV-148): the latest time of the events accepted, each at most the reference it arrived with.
    private DateTime _clock = DateTime.MinValue;
    private DateTime _cursor = DateTime.MinValue;
    // ARV-173: the last processed event time before the instant being applied, where an interval reading of that instant
    // starts its spread. Not part of the snapshot: between steps the next event always starts a new instant (anything at or
    // before the watermark is late), and in a step split inside an instant the watermark, one tick before it, is later.
    private DateTime _instantFloor = DateTime.MinValue;
    private readonly PriorityQueue<(string Key, DateTime Seen), DateTime> _handovers = new();
    // ARV-230: each track's run of position samples (see Hold): its anchor and its pending sample, held outside the buffer. The
    // heaps schedule each run's flush (at or before its pending sample) and its end (at or before its anchor plus the hand-over
    // window), one entry each.
    private readonly Dictionary<string, SightingRun> _runs = new(StringComparer.Ordinal);
    private readonly PriorityQueue<SightingRun, DateTime> _flushes = new();
    private readonly PriorityQueue<SightingRun, DateTime> _runEnds = new();
    private int _pending;
    private bool _residualSuspect;
    // The time of the occupancy reading applied last, until the events of its instant are all applied. A full step that
    // ends inside that instant keeps it for the next step (ARV-114d), one tick after the watermark, so it is part of the
    // snapshot (QueueEngineState.PendingAnchorUtc).
    private DateTime? _anchorDueUtc;

    private readonly Dictionary<DateTime, (long In, long Out, long DegradedIn, long DegradedOut, long TrackedIn)> _movements = [];
    private readonly Dictionary<(DateTime Minute, string Line), (QueueLineRole Role, long In, long Out)> _lines = [];
    private readonly List<OccupancySample> _samples = [];
    private readonly Dictionary<(DateTime Minute, string Zone), (int Min, int Max)> _readings = [];
    private readonly List<RealisedWait> _waits = [];
    private readonly List<EntrantResolution> _resolutions = [];
    private long _late, _future, _invalid, _unknown, _duplicates, _reverse, _unmatched, _negative, _bufferFull, _forced, _tooManyDevices;
    private int _reanchors;
    private DateTime? _earliestLate;
    private readonly Dictionary<DateTime, long> _lateByMinute = [];
    private const int MaxLateMinutes = 7 * 24 * 60 + 60;
    private long _beyondHorizon;
    private bool _more;

    public QueueStateEngine(QueueZoneGeometry geometry, QueueEngineSettings settings = null)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        _settings = settings ?? new QueueEngineSettings();
        var problems = _settings.Problems().ToList();
        if (problems.Count > 0)
            throw new ArgumentException(string.Join(" ", problems), nameof(settings));
        _geometry = geometry;
        _groups[Anonymous] = new LinkedList<Entrant>();
    }

    /// <summary>The watermark: every event at or before it has been processed.</summary>
    public DateTime WatermarkUtc => _watermark;

    /// <summary>
    /// The zone's event clock (ARV-148): the latest time of the events accepted so far, each counted at most up to the
    /// reference time it arrived with; <see cref="DateTime.MinValue"/> before the first.
    /// </summary>
    public DateTime ClockUtc => _clock;

    /// <summary>People held: entered and not yet resolved.</summary>
    public int OpenEntrants => _held.Count;

    /// <summary>Events waiting in the reorder buffer, the tracks' pending position samples included (ARV-230).</summary>
    public int BufferedEvents => _buffer.Count + _pending;

    private int Records => _waits.Count + _resolutions.Count + _movements.Count + _lines.Count + _samples.Count + _readings.Count;

    private static DateTime Plus(DateTime t, TimeSpan d) => t > DateTime.MaxValue - d ? DateTime.MaxValue : t + d;

    private static DateTime Minus(DateTime t, TimeSpan d) => t < DateTime.MinValue + d ? DateTime.MinValue : t - d;

    /// <summary>
    /// Offers one event. <paramref name="referenceUtc"/> is Ariva's clock when the event arrived; events further ahead
    /// than <see cref="QueueEngineSettings.MaxAhead"/> are refused, and events at or behind the watermark are late. An
    /// event held moves the zone's clock to its time, or to <paramref name="referenceUtc"/> when it lies ahead of it.
    /// </summary>
    public void Offer(QueueInput input, DateTime referenceUtc)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (referenceUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("The reference time is UTC.", nameof(referenceUtc));
        var time = input.TimeUtc;
        if (time.Kind != DateTimeKind.Utc)
        {
            _invalid++;
            return;
        }

        if (time > Plus(referenceUtc, _settings.MaxAhead))
        {
            _future++;
            return;
        }

        if (time <= _watermark)
        {
            Late(time, referenceUtc);
            return;
        }

        var ahead = time > referenceUtc;
        if (ahead && _aheadBuffered >= _settings.MaxBufferedEvents / 10)
        {
            _bufferFull++;
            return;
        }

        // ARV-230: a track's position sample within its run takes no place of its own (see Hold), so it is never refused.
        var sighting = _settings.CoalescePositions && !ahead && input is QueueTrackSeen { TrackKey: not null } sample ? sample : null;
        if (BufferedEvents >= _settings.MaxBufferedEvents && (sighting is null || NeedsPlace(sighting)))
        {
            // Make room only with events that were due anyway; never move the watermark past what the clock allows.
            var due = Target(referenceUtc);
            if (TryOldestHeld(out var oldest) && oldest <= due && Records < _settings.MaxStepRecords)
            {
                _forced++;
                ProcessUpTo(oldest);
            }

            if (BufferedEvents >= _settings.MaxBufferedEvents)
            {
                _bufferFull++;
                return;
            }

            if (time <= _watermark)
            {
                Late(time, referenceUtc);
                return;
            }
        }

        var place = new Due(time, InputKey.Of(input, _geometry), _sequence++);
        if (sighting is not null)
            Hold(sighting, place);
        else
            _buffer.Enqueue((input, ahead), place);
        if (ahead)
            _aheadBuffered++;
        // The zone's clock moves with the events it holds, each at most to the time it arrived (CWE-501: a device whose
        // clock runs ahead never carries the watermark past Ariva's receive clock, ARV-148).
        var seen = ahead ? referenceUtc : time;
        if (seen > _clock)
            _clock = seen;
    }

    /// <summary>
    /// Where the watermark may go at <paramref name="referenceUtc"/> (ARV-148): the zone's clock (its latest event time,
    /// and never more than the push interval behind the reference, nor ahead of it) less the lateness allowance. The
    /// clock starts with the zone's first event: before it the zone has nothing to close, and whichever record of the
    /// first receive time comes first cannot move the watermark on its own.
    /// </summary>
    private DateTime Target(DateTime referenceUtc)
    {
        var clock = _clock;
        if (clock == DateTime.MinValue)
            return DateTime.MinValue;
        var idle = Minus(referenceUtc, _settings.PushInterval);
        if (idle > clock)
            clock = idle;
        if (clock > referenceUtc)
            clock = referenceUtc;
        return Minus(clock, _settings.Lateness);
    }

    private void Late(DateTime time, DateTime referenceUtc)
    {
        _late++;
        _earliestLate = _earliestLate is { } earliest && earliest <= time ? earliest : time;
        // Only minutes within the horizon can be recomputed; that also bounds the distinct minutes held (a week at most).
        if (time < Minus(referenceUtc, _settings.LateHorizon))
        {
            _beyondHorizon++;
            return;
        }

        var minute = new DateTime(time.Ticks - time.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        if (_lateByMinute.ContainsKey(minute) || _lateByMinute.Count < MaxLateMinutes)
            _lateByMinute[minute] = _lateByMinute.GetValueOrDefault(minute) + 1;
        else
            _beyondHorizon++; // only if the reference clock itself jumped about within one step
    }

    /// <summary>
    /// Moves the watermark to the zone's clock minus the lateness allowance (never backwards; the clock is the latest
    /// event time held, at least <paramref name="referenceUtc"/> minus the push interval and at most
    /// <paramref name="referenceUtc"/>), processes the held events up to it in time order (up to the step's size bound),
    /// resolves censored and fragmented entrants, and returns what happened since the previous step.
    /// </summary>
    public QueueStep Advance(DateTime referenceUtc)
    {
        if (referenceUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("The reference time is UTC.", nameof(referenceUtc));
        var target = Target(referenceUtc);
        if (target > _watermark)
            ProcessUpTo(target);

        var step = new QueueStep(_geometry.QueueZone, _watermark,
            Movements(),
            [.. _waits], [.. _resolutions], Length(),
            new QueueRejections
            {
                Late = _late, EarliestLateUtc = _earliestLate, BeyondHorizon = _beyondHorizon, LateByMinute = [.. _lateByMinute.OrderBy(m => m.Key).Select(m => (m.Key, m.Value))], Future = _future, Invalid = _invalid, UnknownGeometry = _unknown,
                Duplicates = _duplicates, Reverse = _reverse, UnmatchedExits = _unmatched, NegativeWaits = _negative,
                BufferFull = _bufferFull, ForcedAdvances = _forced, TooManyDevices = _tooManyDevices
            },
            _reanchors, _held.Count, BufferedEvents, _more)
        {
            Lines = LineMovements(),
            Occupancy = [.. _samples],
            Readings = ReadingMinutes()
        };
        _movements.Clear();
        _lines.Clear();
        _samples.Clear();
        _readings.Clear();
        _waits.Clear();
        _resolutions.Clear();
        _late = _future = _invalid = _unknown = _duplicates = _reverse = _unmatched = _negative = _bufferFull = _forced = _tooManyDevices = 0;
        _reanchors = 0;
        _earliestLate = null;
        _lateByMinute.Clear();
        _beyondHorizon = 0;
        _more = false;
        return step;
    }

    #region Processing

    private void ProcessUpTo(DateTime target)
    {
        FlushSightings(target);
        // Every minute boundary at or before the watermark has been sampled (ARV-114a); this run samples the ones after it.
        var sampledThrough = _watermark;
        while (_buffer.TryPeek(out var item, out var key) && key.Time <= target)
        {
            // Every event of the last reading's instant has been applied: the queue may be observed empty there (F5).
            if (_anchorDueUtc is { } due && key.Time != due)
                Reanchor();

            // A boundary before this event's time sees every reading up to it, and none after it.
            if (key.Time > sampledThrough)
            {
                Sample(sampledThrough, key.Time, inclusive: false);
                sampledThrough = key.Time.AddTicks(-1);
            }

            if (Records >= _settings.MaxStepRecords)
            {
                // The step is full: stop just before the unprocessed events, so none of them can become late.
                _more = true;
                var before = key.Time.AddTicks(-1);
                if (before > _watermark)
                    _watermark = before;
                // A check still due here is for this event's instant (any earlier one ran above), which the step has
                // split: it stays pending, one tick after the watermark, and runs in a later step once the rest of the
                // instant has been applied, exactly as in a step that is not split (ARV-114d). Never run it with part
                // of an instant applied: a band's 0 beside the zone's previous reading is not an empty queue.
                EndRuns();
                return;
            }

            _buffer.Dequeue();
            if (item.Ahead)
                _aheadBuffered--;
            Expire(key.Time);
            // ARV-230: a position sample is not a count, so it neither moves the cursor nor narrows an interval reading's spread:
            // the spread does not depend on how often the zone's sensors sample positions, or on which samples the runs keep.
            var counts = item.Input is not QueueTrackSeen;
            if (counts && key.Time > _cursor)
                _instantFloor = _cursor;
            Apply(item.Input);
            if (counts && key.Time > _cursor)
                _cursor = key.Time;
        }

        Reanchor();
        Expire(target);
        if (target > sampledThrough)
            Sample(sampledThrough, target, inclusive: true);
        if (target > _watermark)
            _watermark = target;
        EndRuns();
    }

    /// <summary>
    /// The queue's sensor occupancy at the minute boundaries after <paramref name="after"/> and before (or at, when
    /// <paramref name="inclusive"/>) <paramref name="upTo"/> (ARV-114a, F18 conservation). A boundary has a sample when the
    /// queue zone and every overflow band heard so far have a reading at or before it and fresh there (within the
    /// occupancy freshness, as for the queue length); a band never heard counts as empty. Only the boundaries between the
    /// newest of those readings and the freshness of the oldest qualify, so a jump of the watermark over hours costs at
    /// most an hour of boundaries.
    /// </summary>
    private void Sample(DateTime after, DateTime upTo, bool inclusive)
    {
        if (!_occupancy.TryGetValue(_geometry.QueueZone, out var queue))
            return;
        var (oldest, newest, total) = (queue.AtUtc, queue.AtUtc, 0L);
        foreach (var (zone, reading) in _occupancy)
        {
            if (!_geometry.CountsInQueue(zone))
                continue;
            oldest = reading.AtUtc < oldest ? reading.AtUtc : oldest;
            newest = reading.AtUtc > newest ? reading.AtUtc : newest;
            total += reading.Count;
        }

        var first = NextBoundary(after);
        var seen = newest.Ticks % TimeSpan.TicksPerMinute == 0 ? newest : NextBoundary(newest);
        if (seen > first)
            first = seen;
        var last = Plus(oldest, _settings.OccupancyFreshFor);
        if (upTo < last)
            last = upTo;
        for (var b = first; b <= last && (inclusive || b < upTo) && b < LatestBoundary; b = b.AddMinutes(1))
            _samples.Add(new OccupancySample(b, (int)Math.Min(total, int.MaxValue)));
    }

    private static readonly DateTime LatestBoundary = DateTime.MaxValue.AddMinutes(-2);

    /// <summary>The first whole minute after <paramref name="t"/>.</summary>
    private static DateTime NextBoundary(DateTime t) =>
        t >= LatestBoundary ? LatestBoundary : new DateTime(t.Ticks - t.Ticks % TimeSpan.TicksPerMinute + TimeSpan.TicksPerMinute, DateTimeKind.Utc);

    private void Apply(QueueInput input)
    {
        switch (input)
        {
            case QueueCrossing c:
                Cross(c);
                break;
            case QueueInterval i:
                Interval(i);
                break;
            case QueueOccupancy o:
                Occupancy(o);
                break;
            case QueueTrackSeen s:
                if (s.TrackKey is not null && _byTrack.TryGetValue(s.TrackKey, out var entrant))
                {
                    entrant.LastSeenUtc = s.TimeUtc;
                    _handovers.Enqueue((s.TrackKey, s.TimeUtc), Plus(s.TimeUtc, _settings.HandoverWindow));
                    // Superseded deadlines are dropped lazily; rebuild when they outnumber the tracks.
                    if (_handovers.Count > 2 * _byTrack.Count + 1024)
                        RebuildHandovers();
                }

                break;
        }
    }

    private void Cross(QueueCrossing c)
    {
        var role = _geometry.RoleOf(c.LineName);
        if (role != QueueLineRole.Unknown && c.Direction is CrossingDirection.In or CrossingDirection.Out)
            Tally(c.LineName, role, c.TimeUtc, c.Direction == CrossingDirection.In ? 1 : 0, c.Direction == CrossingDirection.Out ? 1 : 0);
        switch (role, c.Direction)
        {
            case (QueueLineRole.Entry, CrossingDirection.In):
                Enter(c.TimeUtc, c.TrackKey, c.Degraded, c.TrackKey is null ? WaitMethod.Fifo : WaitMethod.Track);
                break;
            case (QueueLineRole.OverflowEntry, CrossingDirection.In):
                if (c.TrackKey is not null)
                    Enter(c.TimeUtc, c.TrackKey, c.Degraded, WaitMethod.Track);
                break;
            case (QueueLineRole.Entry, CrossingDirection.Out):
                Abandon(c.TimeUtc, c.TrackKey, anonymous: true);
                break;
            case (QueueLineRole.OverflowEntry, CrossingDirection.Out):
                Abandon(c.TimeUtc, c.TrackKey, anonymous: false);
                break;
            case (QueueLineRole.Exit, CrossingDirection.Out):
                Exit(c.TimeUtc, c.TrackKey, c.Degraded, cumulative: false);
                break;
            case (QueueLineRole.Exit, CrossingDirection.In):
                _reverse++;
                break;
            case (QueueLineRole.Count, _):
                break; // counted on its line only (ARV-113)
            default:
                _unknown++;
                break;
        }
    }

    private void Interval(QueueInterval i)
    {
        var role = _geometry.RoleOf(i.LineName);
        if (role == QueueLineRole.Unknown)
        {
            _unknown++;
            return;
        }

        if (i.FromUtc.Kind != DateTimeKind.Utc || i.FromUtc >= i.TimeUtc || i.TimeUtc - i.FromUtc > TimeSpan.FromDays(1) ||
            i.In is < 0 || i.Out is < 0 || i.In > _settings.MaxIntervalPeople || i.Out > _settings.MaxIntervalPeople)
        {
            _invalid++;
            return;
        }

        // Spread over the part of the interval after the watermark and the last event processed before this instant: earlier
        // minutes are already processed, and no reading of the same instant (the other line of the same push) narrows the
        // spread, so every reading ending at T stays in the minutes before T (ARV-173; the cursor is T after the first).
        var start = i.FromUtc;
        if (_instantFloor > start)
            start = _instantFloor;
        if (_watermark > start)
            start = _watermark;
        var span = i.TimeUtc - start;
        // Strictly before the reading's end: a span of a few ticks (the rest of an instant after a full step) would otherwise
        // round its last people up to the end itself, the next minute when the reading ends on one.
        var last = i.TimeUtc.AddTicks(-1);
        DateTime Spread(int k, int n)
        {
            var at = start + span * ((k + 0.5) / n);
            return at <= last ? at : last;
        }
        // The line's own counts, spread the same way whatever the counts do to the queue (ARV-113).
        for (var k = 0; k < i.In; k++)
            Tally(i.LineName, role, Spread(k, i.In), 1, 0);
        for (var k = 0; k < i.Out; k++)
            Tally(i.LineName, role, Spread(k, i.Out), 0, 1);
        if (role == QueueLineRole.Entry)
        {
            for (var k = 0; k < i.In; k++)
                Enter(Spread(k, i.In), null, i.Degraded, WaitMethod.Cumulative);
            for (var k = 0; k < i.Out; k++)
                Abandon(Spread(k, i.Out), null, anonymous: true);
        }
        else if (role == QueueLineRole.Exit)
        {
            for (var k = 0; k < i.Out; k++)
                Exit(Spread(k, i.Out), null, i.Degraded, cumulative: true);
            _reverse += i.In;
        }

        // Anonymous counts of an overflow band's entry line are not entries (see the class remarks).
    }

    private void Enter(DateTime time, string trackKey, bool degraded, WaitMethod method)
    {
        if (trackKey is not null && (_byTrack.ContainsKey(trackKey) || _resolvedTracks.Contains(trackKey)))
        {
            // First entry counts (F5); a repeat of an open or resolved track is a duplicate or a second line.
            _duplicates++;
            return;
        }

        // Make room first: censoring can remove the last entrant of this person's group (and the group with it).
        if (_held.Count >= _settings.MaxOpenEntrants)
            Resolve(_held.First!.Value, time, EntrantOutcome.Censored);

        var group = trackKey is null ? Anonymous : DeviceOf(trackKey);
        if (!_groups.TryGetValue(group, out var members))
        {
            if (_groups.Count > _settings.MaxDevicesPerZone)
            {
                _tooManyDevices++;
                trackKey = null;
                group = Anonymous;
                method = WaitMethod.Fifo;
                members = _groups[Anonymous];
            }
            else
            {
                _groups[group] = members = new LinkedList<Entrant>();
            }
        }

        // Events are applied in time order, so a new entrant is never earlier than those held, except a later interval reading
        // of the same instant, which is spread over the same part of the minute (ARV-173): it is held at the latest entry
        // time, so the held list stays in entry order for FIFO, and counted at its own time, in the minute its reading
        // covers. Equal times keep the order of application (content, then arrival: ARV-148).
        var counted = time;
        if (_held.Last is { } last && last.Value.EntryUtc > time)
            time = last.Value.EntryUtc;
        var entrant = new Entrant(time, trackKey, group, degraded, method, _order++);
        entrant.InAll = _held.AddLast(entrant);
        entrant.InGroup = members.AddLast(entrant);
        if (trackKey is not null)
            _byTrack[trackKey] = entrant;
        if (degraded)
            _degradedHeld++;
        Count(counted, entry: true, degraded, tracked: trackKey is not null);
    }

    private void Exit(DateTime time, string trackKey, bool degraded, bool cumulative)
    {
        // The out variable is declared before the condition and the outcome kept in its own statement (ARV-069a): a
        // mutant that short-circuits the condition still compiles, so Stryker measures this method.
        Entrant own = null;
        var tracked = trackKey is not null && _byTrack.TryGetValue(trackKey, out own);
        if (!tracked && trackKey is not null && _resolvedTracks.Contains(trackKey))
        {
            _duplicates++;
            return;
        }

        var entrant = tracked ? own : FifoPartner(trackKey);
        // ARV-173: an exit of an interval reading may be spread before the earliest person held entered (an entry reading of
        // the same instant is applied first): in time order nobody counted in was waiting then, so it pairs with nobody and
        // that person stays held. Every exit applied before it was earlier, so this is exactly time-ordered FIFO.
        var early = !tracked && cumulative && entrant is not null && entrant.EntryUtc > time;
        if (early)
            entrant = null;
        if (entrant is null)
        {
            _unmatched++;
            Count(time, entry: false, degraded);
            return;
        }

        var method = tracked ? WaitMethod.Track
            : cumulative || entrant.Method == WaitMethod.Cumulative ? WaitMethod.Cumulative : WaitMethod.Fifo;
        Forget(entrant, entrant.TrackKey ?? trackKey);
        Count(time, entry: false, degraded);
        if (time < entrant.EntryUtc)
        {
            _negative++;
            _resolutions.Add(new EntrantResolution(entrant.EntryUtc, entrant.EntryUtc, EntrantOutcome.Rejected, entrant.TrackKey ?? trackKey) { Tracked = entrant.TrackKey is not null });
            return;
        }

        var suspect = method != WaitMethod.Track && _residualSuspect;
        _waits.Add(new RealisedWait(entrant.EntryUtc, time, method, entrant.Degraded || degraded || suspect, entrant.TrackKey ?? trackKey));
    }

    /// <summary>
    /// The entrant an exit without its own open track pairs with: the earliest held, except that a tracked exit skips
    /// entrants tracked by the same device (track keys are namespaced "device/track"): that device would have exited them
    /// under their own id, so its unknown track is someone who entered before the engine started. The earliest of each
    /// group's first entrant, so the work is the number of devices, not the number of people.
    /// </summary>
    private Entrant FifoPartner(string trackKey)
    {
        if (trackKey is null)
            return _held.First?.Value;
        var device = DeviceOf(trackKey);
        Entrant best = null;
        foreach (var (group, members) in _groups)
        {
            var first = members.First;
            if (first is null || string.Equals(group, device, StringComparison.Ordinal))
                continue;
            if (best is null || first.Value.Order < best.Order)
                best = first.Value;
        }

        return best;
    }

    /// <summary>The device part of a namespaced track key; keys without one share a group of their own, never the anonymous one.</summary>
    private static string DeviceOf(string trackKey)
    {
        var slash = trackKey.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? Unnamespaced : trackKey[..slash];
    }

    private void Abandon(DateTime time, string trackKey, bool anonymous)
    {
        if (trackKey is not null && _byTrack.TryGetValue(trackKey, out var own))
        {
            Resolve(own, time, EntrantOutcome.Abandoned);
            return;
        }

        if (trackKey is not null && _resolvedTracks.Contains(trackKey))
        {
            _duplicates++;
            return;
        }

        // Someone at the back of the queue walks out: the latest anonymous entrant leaves.
        if (!anonymous)
            return;
        if (_groups[Anonymous].Last is { } latest)
            Resolve(latest.Value, time, EntrantOutcome.Abandoned);
        else
            _unmatched++;
    }

    private void Occupancy(QueueOccupancy o)
    {
        if (!_geometry.CountsInQueue(o.ZoneName))
        {
            _unknown++;
            return;
        }

        if (o.Count is < 0 or > CanonicalEventRules.MaxOccupancy)
        {
            _invalid++;
            return;
        }

        _occupancy[o.ZoneName] = (o.Count, o.TimeUtc, o.Degraded);
        // The readings of the minute, for the occupancy sanity check of F18 (ARV-114a).
        var readingKey = (new DateTime(o.TimeUtc.Ticks - o.TimeUtc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc), o.ZoneName);
        _readings[readingKey] = _readings.TryGetValue(readingKey, out var seen) ? (Math.Min(seen.Min, o.Count), Math.Max(seen.Max, o.Count)) : (o.Count, o.Count);
        // The queue zone and its bands are often read by different devices at the same instant, and their batches arrive
        // in any order: the check for an empty queue waits until every event of this instant has been applied
        // (ProcessUpTo, across steps when a full step splits the instant), so it never sees one zone's new reading beside
        // another's previous one.
        _anchorDueUtc = o.TimeUtc;
    }

    /// <summary>
    /// F5 re-anchoring, once every event at <see cref="_anchorDueUtc"/> has been applied: when every zone of the queue
    /// reads zero there, whoever the FIFO sequence still holds from before then is a counting residual.
    /// </summary>
    private void Reanchor()
    {
        // No pattern or out variable declared inside a condition (ARV-069a): every mutant of these conditions compiles.
        if (_anchorDueUtc is null)
            return;
        var at = _anchorDueUtc.Value;
        _anchorDueUtc = null;
        var reported = AllZonesReport(at, out var total, out _);
        if (!reported || total != 0)
            return;

        // The queue is observed empty: whoever the FIFO sequence still holds from before now is a counting residual (F5).
        var dropped = 0;
        for (var first = _held.First; first is not null && first.Value.EntryUtc <= at; first = _held.First)
        {
            Resolve(first.Value, at, first.Value.TrackKey is null ? EntrantOutcome.Reanchored : EntrantOutcome.Fragmented);
            dropped++;
        }

        if (dropped > 0)
            _reanchors++;
        _residualSuspect = dropped > _settings.ResidualTolerance;
    }

    /// <summary>
    /// Censors entrants past T_censor and fragments tracks not seen within the hand-over window, before <paramref name="now"/>, in the
    /// order of the times they resolve at (a censoring first at the same time): which of the two resolves an entrant, and the order
    /// of the resolutions, do not depend on which events happen to be applied between them (ARV-230: the tracks' runs apply fewer
    /// position samples).
    /// </summary>
    private void Expire(DateTime now)
    {
        while (true)
        {
            var first = _held.First;
            DateTime? censorAt = first is not null && now - first.Value.EntryUtc > _settings.CensorAfter ? first.Value.EntryUtc + _settings.CensorAfter : null;
            var handover = NextHandover(now);
            if (handover is { } h && (censorAt is not { } c || h.Deadline < c))
            {
                _handovers.Dequeue();
                Resolve(h.Entrant, h.Deadline, EntrantOutcome.Fragmented);
            }
            else if (censorAt is { } at)
            {
                Resolve(first.Value, at, EntrantOutcome.Censored);
            }
            else
            {
                return;
            }
        }
    }

    /// <summary>
    /// The earliest hand-over deadline before <paramref name="now"/> that still holds, or null. Hand-over deadlines are in a heap:
    /// each sighting costs a logarithm, each expiry looks only at what is due; superseded deadlines are dropped here.
    /// </summary>
    private (Entrant Entrant, DateTime Deadline)? NextHandover(DateTime now)
    {
        while (_handovers.TryPeek(out var item, out var deadline) && now > deadline)
        {
            if (_byTrack.TryGetValue(item.Key, out var entrant) && entrant.LastSeenUtc == item.Seen)
                return (entrant, deadline);
            _handovers.Dequeue();
        }

        return null;
    }

    private void RebuildHandovers()
    {
        _handovers.Clear();
        foreach (var (key, entrant) in _byTrack)
        {
            if (entrant.LastSeenUtc is { } seen)
                _handovers.Enqueue((key, seen), Plus(seen, _settings.HandoverWindow));
        }
    }

    private void Resolve(Entrant entrant, DateTime time, EntrantOutcome outcome)
    {
        Forget(entrant, entrant.TrackKey);
        _resolutions.Add(new EntrantResolution(entrant.EntryUtc, time > entrant.EntryUtc ? time : entrant.EntryUtc, outcome, entrant.TrackKey) { Tracked = entrant.TrackKey is not null });
    }

    private void Forget(Entrant entrant, string trackKey)
    {
        _held.Remove(entrant.InAll);
        var members = _groups[entrant.Group];
        members.Remove(entrant.InGroup);
        if (members.Count == 0 && entrant.Group != Anonymous)
            _groups.Remove(entrant.Group);
        if (entrant.Degraded)
            _degradedHeld--;
        if (entrant.TrackKey is not null)
            _byTrack.Remove(entrant.TrackKey);
        if (trackKey is null || _settings.MaxRememberedTracks == 0)
            return;
        if (_resolvedTracks.Add(trackKey))
            _resolvedOrder.Enqueue(trackKey);
        while (_resolvedOrder.Count > _settings.MaxRememberedTracks)
            _resolvedTracks.Remove(_resolvedOrder.Dequeue());
    }

    /// <summary>A crossing of one of the zone's lines, counted on its line and minute as applied (ARV-113).</summary>
    private void Tally(string line, QueueLineRole role, DateTime time, long @in, long @out)
    {
        var key = (new DateTime(time.Ticks - time.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc), line);
        _lines.TryGetValue(key, out var t);
        _lines[key] = (role, t.In + @in, t.Out + @out);
    }

    private List<LineMovement> LineMovements() =>
        [.. _lines.OrderBy(l => l.Key.Minute).ThenBy(l => l.Key.Line, StringComparer.Ordinal)
            .Select(l => new LineMovement(l.Key.Line, l.Value.Role, l.Key.Minute, l.Value.In, l.Value.Out))];

    private void Count(DateTime time, bool entry, bool degraded, bool tracked = false)
    {
        var minute = new DateTime(time.Ticks - time.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        _movements.TryGetValue(minute, out var m);
        _movements[minute] = entry
            ? (m.In + 1, m.Out, m.DegradedIn + (degraded ? 1 : 0), m.DegradedOut, m.TrackedIn + (tracked ? 1 : 0))
            : (m.In, m.Out + 1, m.DegradedIn, m.DegradedOut + (degraded ? 1 : 0), m.TrackedIn);
    }

    private List<MovementCount> Movements() =>
        [.. _movements.OrderBy(m => m.Key).Select(m => new MovementCount(m.Key, m.Value.In, m.Value.Out, m.Value.DegradedIn, m.Value.DegradedOut) { TrackedEntries = m.Value.TrackedIn })];

    private List<ZoneReadingMinute> ReadingMinutes() =>
        [.. _readings.OrderBy(r => r.Key.Minute).ThenBy(r => r.Key.Zone, StringComparer.Ordinal)
            .Select(r => new ZoneReadingMinute(r.Key.Zone, r.Key.Minute, r.Value.Min, r.Value.Max))];

    #endregion

    #region Position samples (ARV-230)

    // A position sample only marks an open entrant's track as seen: its last sighting, and a hand-over deadline that window after
    // it, after which the entrant is fragmented (F6) unless a later sample moved it. So of a track's samples, one between two that
    // are applied and at most the hand-over window apart changes no outcome: whatever it would have set, the later one sets before
    // any deadline it could add or leave passes (Expire resolves in time order, and a sample neither moves the cursor nor narrows
    // an interval reading). Each track keeps a run: its anchor (the latest sample applied or in the buffer) and its pending sample
    // (the latest within the window after the anchor), held outside the buffer until the watermark reaches it or a later sample
    // ends the run (and the pending sample, buffered, anchors the next). A run holds at most two places in the window, whatever the
    // sensors' rate (5 Hz positions held one place each before: about 20 MB of state a hall zone, ARV-124a). Samples ahead of the
    // reference clock, without a track, behind their run's anchor, or of a track without a run when the runs are at their bound
    // (MaxSightingRuns) are buffered as before: applying an extra sample never changes an outcome either. The zone processor
    // offers each push's samples in time order, so a device that sends a push newest first still coalesces; only samples sent out
    // of order across pushes take a place each, as they did before.

    /// <summary>True when the sample takes a place of its own (in the buffer or as a run's pending sample); false when its run takes it.</summary>
    private bool NeedsPlace(QueueTrackSeen seen)
    {
        if (!_runs.TryGetValue(seen.TrackKey, out var run))
            return true;
        var time = seen.TimeUtc;
        if (time < run.Anchor || time - run.Anchor > _settings.HandoverWindow)
            return true;
        return time != run.Anchor && run.Pending is null;
    }

    /// <summary>Holds a position sample in its track's run, or in the buffer (see the region's remarks).</summary>
    private void Hold(QueueTrackSeen seen, Due place)
    {
        var time = seen.TimeUtc;
        if (!_runs.TryGetValue(seen.TrackKey, out var run))
        {
            _buffer.Enqueue((seen, false), place);
            // The runs have their own bound (CWE-400): a track without one is not coalesced, its samples buffered as before.
            if (_runs.Count < _settings.MaxSightingRuns)
            {
                run = new SightingRun(seen.TrackKey, time);
                _runs.Add(seen.TrackKey, run);
                ScheduleEnd(run);
            }

            return;
        }

        if (time < run.Anchor)
        {
            _buffer.Enqueue((seen, false), place);
            return;
        }

        // The same instant as the anchor: the same reading.
        if (time == run.Anchor)
            return;
        if (time - run.Anchor > _settings.HandoverWindow)
        {
            // The run ends at its pending sample, which anchors the next one.
            if (run.Pending is not null)
                BufferPending(run);
            if (time - run.Anchor > _settings.HandoverWindow)
            {
                _buffer.Enqueue((seen, false), place);
                run.Anchor = time;
                ScheduleEnd(run);
                return;
            }
        }

        if (run.Pending is null)
        {
            run.Pending = seen;
            run.PendingSequence = place.Sequence;
            _pending++;
            if (!run.FlushScheduled)
            {
                _flushes.Enqueue(run, time);
                run.FlushScheduled = true;
            }
        }
        else if (time > run.Pending.TimeUtc)
        {
            // A flush scheduled for the earlier pending sample is at or before this one too.
            run.Pending = seen;
            run.PendingSequence = place.Sequence;
        }

        // Otherwise between the anchor and the pending sample, or at the latter: it changes nothing.
    }

    /// <summary>Moves the run's pending sample into the buffer, where it anchors the run.</summary>
    private void BufferPending(SightingRun run)
    {
        var pending = run.Pending;
        _buffer.Enqueue((pending, false), new Due(pending.TimeUtc, InputKey.Of(pending, _geometry), run.PendingSequence));
        run.Anchor = pending.TimeUtc;
        run.Pending = null;
        _pending--;
        ScheduleEnd(run);
    }

    private void ScheduleEnd(SightingRun run)
    {
        if (run.EndScheduled)
            return;
        _runEnds.Enqueue(run, Plus(run.Anchor, _settings.HandoverWindow));
        run.EndScheduled = true;
    }

    /// <summary>Buffers every pending sample at or before <paramref name="target"/>, before the watermark moves to it.</summary>
    private void FlushSightings(DateTime target)
    {
        while (_flushes.TryPeek(out var run, out var at) && at <= target)
        {
            _flushes.Dequeue();
            run.FlushScheduled = false;
            if (run.Pending is not { } pending)
                continue;
            if (pending.TimeUtc <= target)
            {
                BufferPending(run);
                continue;
            }

            _flushes.Enqueue(run, pending.TimeUtc);
            run.FlushScheduled = true;
        }
    }

    /// <summary>
    /// Forgets the runs the watermark has left behind: a run without a pending sample whose anchor is a hand-over window or more
    /// behind the watermark coalesces nothing more, since any sample still accepted is past its window.
    /// </summary>
    private void EndRuns()
    {
        while (_runEnds.TryPeek(out var run, out var at) && at <= _watermark)
        {
            _runEnds.Dequeue();
            run.EndScheduled = false;
            var end = Plus(run.Anchor, _settings.HandoverWindow);
            if (run.Pending is null && end <= _watermark)
            {
                if (_runs.TryGetValue(run.Track, out var current) && ReferenceEquals(current, run))
                    _runs.Remove(run.Track);
                continue;
            }

            // A pending sample is never behind the watermark (FlushSightings), so its run ends later.
            _runEnds.Enqueue(run, end > _watermark ? end : Plus(_watermark, TimeSpan.FromTicks(1)));
            run.EndScheduled = true;
        }
    }

    /// <summary>The time of the oldest event held, in the buffer or as a run's pending sample; false when none is.</summary>
    private bool TryOldestHeld(out DateTime oldest)
    {
        var any = _buffer.TryPeek(out _, out var first);
        oldest = any ? first.Time : DateTime.MaxValue;
        // Each run's flush is at or before its pending sample: make the earliest one exact.
        while (_flushes.TryPeek(out var run, out var at))
        {
            if (run.Pending is not { } pending)
            {
                _flushes.Dequeue();
                run.FlushScheduled = false;
                continue;
            }

            if (at < pending.TimeUtc)
            {
                _flushes.Dequeue();
                _flushes.Enqueue(run, pending.TimeUtc);
                continue;
            }

            if (at < oldest)
                oldest = at;
            return true;
        }

        return any;
    }

    #endregion

    #region Queue length

    private bool AllZonesReport(DateTime at, out int total, out bool degraded)
    {
        total = 0;
        degraded = false;
        if (!_occupancy.TryGetValue(_geometry.QueueZone, out var queue) || at - queue.AtUtc > _settings.OccupancyFreshFor)
            return false;
        total = queue.Count;
        degraded = queue.Degraded;
        foreach (var zone in _geometry.OverflowZones)
        {
            if (!_occupancy.TryGetValue(zone, out var band) || at - band.AtUtc > _settings.OccupancyFreshFor)
                return false;
            total += band.Count;
            degraded |= band.Degraded;
        }

        return true;
    }

    private QueueLength Length()
    {
        if (AllZonesReport(_watermark, out var total, out var degraded))
            return new QueueLength(total, true, degraded, _watermark);
        // No fresh reading for every zone: the people the engine holds, flagged when any came from a degraded event.
        return new QueueLength(_held.Count, false, _residualSuspect || _degradedHeld > 0, _watermark);
    }

    #endregion

    #region Order

    /// <summary>
    /// The content of an input that orders the events of one instant (ARV-148): kind (interval readings first, so that the
    /// readings of an instant are applied before its crossings, occupancy and track samples), then for interval readings
    /// the role of their line in the zone's geometry (entry lines first, ARV-173: the instant's entrants are held before its
    /// exits pair, whatever the lines are called), then line or zone, direction, track, interval start, counts and flag.
    /// Two inputs with the same key are the same reading, so their order cannot change a result.
    /// </summary>
    private readonly record struct InputKey(int Rank, int Role, string Name, int Direction, string Track, long FromTicks, int In, int Out, int Count, bool Degraded)
    {
        public static InputKey Of(QueueInput input, QueueZoneGeometry geometry) => input switch
        {
            QueueInterval i => new(0, geometry.RoleOf(i.LineName) == QueueLineRole.Entry ? 0 : 1, i.LineName, 0, null, i.FromUtc.Ticks, i.In, i.Out, 0, i.Degraded),
            QueueCrossing c => new(1, 0, c.LineName, (int)c.Direction, c.TrackKey, 0, 0, 0, 0, c.Degraded),
            QueueOccupancy o => new(2, 0, o.ZoneName, 0, null, 0, 0, 0, o.Count, o.Degraded),
            QueueTrackSeen s => new(3, 0, null, 0, s.TrackKey, 0, 0, 0, 0, s.Degraded),
            _ => new(4, 0, null, 0, null, 0, 0, 0, 0, input.Degraded)
        };

        public int CompareTo(InputKey other)
        {
            var c = Rank.CompareTo(other.Rank);
            if (c == 0)
                c = Role.CompareTo(other.Role);
            if (c == 0)
                c = string.CompareOrdinal(Name, other.Name);
            if (c == 0)
                c = Direction.CompareTo(other.Direction);
            if (c == 0)
                c = string.CompareOrdinal(Track, other.Track);
            if (c == 0)
                c = FromTicks.CompareTo(other.FromTicks);
            if (c == 0)
                c = In.CompareTo(other.In);
            if (c == 0)
                c = Out.CompareTo(other.Out);
            if (c == 0)
                c = Count.CompareTo(other.Count);
            if (c == 0)
                c = Degraded.CompareTo(other.Degraded);
            return c;
        }
    }

    /// <summary>A held event's place in the reorder buffer: its time, its content and its arrival.</summary>
    private readonly record struct Due(DateTime Time, InputKey Key, long Sequence);

    /// <summary>
    /// Event time first, then the content of the event, and arrival last (ARV-148): the events of one instant are applied
    /// in the same order whichever batches, topics or partitions carried them and in whatever order those arrived.
    /// </summary>
    private sealed class DueOrder : IComparer<Due>
    {
        public static readonly DueOrder Instance = new();

        public int Compare(Due x, Due y)
        {
            var c = x.Time.CompareTo(y.Time);
            if (c == 0)
                c = x.Key.CompareTo(y.Key);
            if (c == 0)
                c = x.Sequence.CompareTo(y.Sequence);
            return c;
        }
    }

    #endregion
}
