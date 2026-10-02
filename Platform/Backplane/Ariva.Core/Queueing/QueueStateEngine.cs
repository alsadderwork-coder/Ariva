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
    /// <summary>How long events may arrive after their time and still be processed in order (D5 lateness allowance).</summary>
    public TimeSpan Lateness { get; init; } = TimeSpan.FromSeconds(30);

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

    /// <summary>Late events older than this behind the reference clock are beyond recomputation (the 3 days the sensing topics and the archive keep).</summary>
    public TimeSpan LateHorizon { get; init; } = TimeSpan.FromDays(3);

    /// <summary>Waits, resolutions and minutes one step holds at most; beyond it the step ends early (<see cref="QueueStep.More"/>).</summary>
    public int MaxStepRecords { get; init; } = 100_000;

    /// <summary>People one interval reading may count in or out; more is implausible for one line in one interval.</summary>
    public int MaxIntervalPeople { get; init; } = 5_000;

    /// <summary>Devices whose tracks a zone keeps apart; the tracks of further devices count as anonymous.</summary>
    public int MaxDevicesPerZone { get; init; } = 64;

    /// <summary>The problems with these settings, empty when valid.</summary>
    public IEnumerable<string> Problems()
    {
        if (Lateness < TimeSpan.Zero || Lateness > TimeSpan.FromMinutes(30))
            yield return "Lateness is from 0 to 30 minutes.";
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
/// Events are offered as they arrive and held in a reorder buffer; <see cref="Advance"/> moves the watermark to the
/// reference time minus the lateness allowance and processes every held event up to it in event-time order (ties in
/// arrival order). Events that arrive behind the watermark are late and counted, not applied: the minutes they touch
/// are already processed, and a recomputation decides (F6 revisions). The watermark never passes the reference time
/// minus the lateness, whatever the buffer holds.
/// </para>
/// <para>
/// Entries are inward crossings of an entry line (or of an overflow band's entry line, for a tracked person: overflow
/// time counts as wait, F5) and the "in" counts of interval readings; exits are outward crossings of an exit line and
/// "out" counts. A tracked exit closes its own track (T3, the primary method); any other exit pairs with the earliest
/// entrant still held (FIFO; with interval counts spread over their interval this is the cumulative-curve method, T1),
/// except that a tracked exit never takes a person the same device is tracking. An interval's counts are spread over the
/// part of it after the last processed event, so they never reach into minutes already processed. Leaving backwards
/// over an entry line abandons (the tracked person, or the latest anonymous entrant); entrants not resolved within
/// T_censor are censored; a tracked person with position samples not seen within the hand-over window is fragmented;
/// when every zone of the queue reports zero occupancy the FIFO sequence is re-anchored (F5).
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

    private readonly QueueZoneGeometry _geometry;
    private readonly QueueEngineSettings _settings;
    private readonly PriorityQueue<(QueueInput Input, bool Ahead), (DateTime Time, long Sequence)> _buffer = new();
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
    private DateTime _cursor = DateTime.MinValue;
    private readonly PriorityQueue<(string Key, DateTime Seen), DateTime> _handovers = new();
    private bool _residualSuspect;

    private readonly Dictionary<DateTime, (long In, long Out, long DegradedIn, long DegradedOut)> _movements = [];
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

    /// <summary>People held: entered and not yet resolved.</summary>
    public int OpenEntrants => _held.Count;

    /// <summary>Events waiting in the reorder buffer.</summary>
    public int BufferedEvents => _buffer.Count;

    private int Records => _waits.Count + _resolutions.Count + _movements.Count;

    private static DateTime Plus(DateTime t, TimeSpan d) => t > DateTime.MaxValue - d ? DateTime.MaxValue : t + d;

    private static DateTime Minus(DateTime t, TimeSpan d) => t < DateTime.MinValue + d ? DateTime.MinValue : t - d;

    /// <summary>
    /// Offers one event. <paramref name="referenceUtc"/> is Ariva's clock when the event arrived; events further ahead
    /// than <see cref="QueueEngineSettings.MaxAhead"/> are refused, and events behind the watermark are late.
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

        if (_buffer.Count >= _settings.MaxBufferedEvents)
        {
            // Make room only with events that were due anyway; never move the watermark past what the clock allows.
            _buffer.TryPeek(out _, out var oldest);
            var due = Minus(referenceUtc, _settings.Lateness);
            if (oldest.Time <= due && Records < _settings.MaxStepRecords)
            {
                _forced++;
                ProcessUpTo(oldest.Time);
            }

            if (_buffer.Count >= _settings.MaxBufferedEvents)
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

        _buffer.Enqueue((input, ahead), (time, _sequence++));
        if (ahead)
            _aheadBuffered++;
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
    /// Moves the watermark to <paramref name="referenceUtc"/> minus the lateness allowance (never backwards), processes
    /// the held events up to it in time order (up to the step's size bound), resolves censored and fragmented entrants,
    /// and returns what happened since the previous step.
    /// </summary>
    public QueueStep Advance(DateTime referenceUtc)
    {
        if (referenceUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("The reference time is UTC.", nameof(referenceUtc));
        var target = Minus(referenceUtc, _settings.Lateness);
        if (target > _watermark)
            ProcessUpTo(target);

        var step = new QueueStep(_geometry.QueueZone, _watermark,
            [.. _movements.OrderBy(m => m.Key).Select(m => new MovementCount(m.Key, m.Value.In, m.Value.Out, m.Value.DegradedIn, m.Value.DegradedOut))],
            [.. _waits], [.. _resolutions], Length(),
            new QueueRejections
            {
                Late = _late, EarliestLateUtc = _earliestLate, BeyondHorizon = _beyondHorizon, LateByMinute = [.. _lateByMinute.OrderBy(m => m.Key).Select(m => (m.Key, m.Value))], Future = _future, Invalid = _invalid, UnknownGeometry = _unknown,
                Duplicates = _duplicates, Reverse = _reverse, UnmatchedExits = _unmatched, NegativeWaits = _negative,
                BufferFull = _bufferFull, ForcedAdvances = _forced, TooManyDevices = _tooManyDevices
            },
            _reanchors, _held.Count, _buffer.Count, _more);
        _movements.Clear();
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
        while (_buffer.TryPeek(out var item, out var key) && key.Time <= target)
        {
            if (Records >= _settings.MaxStepRecords)
            {
                // The step is full: stop just before the unprocessed events, so none of them can become late.
                _more = true;
                var before = key.Time.AddTicks(-1);
                if (before > _watermark)
                    _watermark = before;
                return;
            }

            _buffer.Dequeue();
            if (item.Ahead)
                _aheadBuffered--;
            Expire(key.Time);
            Apply(item.Input);
            if (key.Time > _cursor)
                _cursor = key.Time;
        }

        Expire(target);
        if (target > _watermark)
            _watermark = target;
    }

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

        // Spread over the part of the interval after the last processed event and the watermark: earlier minutes are
        // already processed.
        var start = i.FromUtc;
        if (_cursor > start)
            start = _cursor;
        if (_watermark > start)
            start = _watermark;
        var span = i.TimeUtc - start;
        DateTime Spread(int k, int n) => start + span * ((k + 0.5) / n);
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

        // Events are applied in time order and spread counts start after the last processed event, so a new entrant is
        // never earlier than those held; equal times keep arrival order.
        if (_held.Last is { } last && last.Value.EntryUtc > time)
            time = last.Value.EntryUtc;
        var entrant = new Entrant(time, trackKey, group, degraded, method, _order++);
        entrant.InAll = _held.AddLast(entrant);
        entrant.InGroup = members.AddLast(entrant);
        if (trackKey is not null)
            _byTrack[trackKey] = entrant;
        if (degraded)
            _degradedHeld++;
        Count(time, entry: true, degraded);
    }

    private void Exit(DateTime time, string trackKey, bool degraded, bool cumulative)
    {
        Entrant entrant;
        WaitMethod method;
        if (trackKey is not null && _byTrack.TryGetValue(trackKey, out var own))
        {
            entrant = own;
            method = WaitMethod.Track;
        }
        else if (trackKey is not null && _resolvedTracks.Contains(trackKey))
        {
            _duplicates++;
            return;
        }
        else
        {
            entrant = FifoPartner(trackKey);
            if (entrant is null)
            {
                _unmatched++;
                Count(time, entry: false, degraded);
                return;
            }

            method = cumulative || entrant.Method == WaitMethod.Cumulative ? WaitMethod.Cumulative : WaitMethod.Fifo;
        }

        Forget(entrant, entrant.TrackKey ?? trackKey);
        Count(time, entry: false, degraded);
        if (time < entrant.EntryUtc)
        {
            _negative++;
            _resolutions.Add(new EntrantResolution(entrant.EntryUtc, entrant.EntryUtc, EntrantOutcome.Rejected, entrant.TrackKey ?? trackKey));
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
            if (members.First is not { } first || string.Equals(group, device, StringComparison.Ordinal))
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
        if (!AllZonesReport(o.TimeUtc, out var total, out _) || total != 0)
            return;

        // The queue is observed empty: whoever the FIFO sequence still holds from before now is a counting residual (F5).
        var dropped = 0;
        while (_held.First is { } first && first.Value.EntryUtc <= o.TimeUtc)
        {
            Resolve(first.Value, o.TimeUtc, first.Value.TrackKey is null ? EntrantOutcome.Reanchored : EntrantOutcome.Fragmented);
            dropped++;
        }

        if (dropped > 0)
            _reanchors++;
        _residualSuspect = dropped > _settings.ResidualTolerance;
    }

    /// <summary>Censors entrants past T_censor and fragments tracks not seen within the hand-over window, at <paramref name="now"/>.</summary>
    private void Expire(DateTime now)
    {
        while (_held.First is { } first && now - first.Value.EntryUtc > _settings.CensorAfter)
            Resolve(first.Value, first.Value.EntryUtc + _settings.CensorAfter, EntrantOutcome.Censored);

        // Hand-over deadlines in a heap: each sighting costs a logarithm, each expiry looks only at what is due.
        while (_handovers.TryPeek(out var item, out var deadline) && now > deadline)
        {
            _handovers.Dequeue();
            if (_byTrack.TryGetValue(item.Key, out var entrant) && entrant.LastSeenUtc == item.Seen)
                Resolve(entrant, deadline, EntrantOutcome.Fragmented);
        }
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
        _resolutions.Add(new EntrantResolution(entrant.EntryUtc, time > entrant.EntryUtc ? time : entrant.EntryUtc, outcome, entrant.TrackKey));
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

    private void Count(DateTime time, bool entry, bool degraded)
    {
        var minute = new DateTime(time.Ticks - time.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        _movements.TryGetValue(minute, out var m);
        _movements[minute] = entry
            ? (m.In + 1, m.Out, m.DegradedIn + (degraded ? 1 : 0), m.DegradedOut)
            : (m.In, m.Out + 1, m.DegradedIn, m.DegradedOut + (degraded ? 1 : 0));
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
}
