using Ariva.Core.Domain.Enums;

namespace Ariva.Core.Queueing;

/// <summary>One buffered or held input in a snapshot: a flat record of the four input kinds (no polymorphism on the wire).</summary>
public sealed record QueueInputState(
    string Kind,
    DateTime TimeUtc,
    bool Degraded,
    string Name = null,
    CrossingDirection Direction = default,
    string TrackKey = null,
    DateTime? FromUtc = null,
    int In = 0,
    int Out = 0,
    int Count = 0)
{
    internal const string Crossing = "crossing", Interval = "interval", Occupancy = "occupancy", TrackSeen = "track-seen";

    internal static QueueInputState From(QueueInput input) => input switch
    {
        QueueCrossing c => new(Crossing, c.TimeUtc, c.Degraded, c.LineName, c.Direction, c.TrackKey),
        QueueInterval i => new(Interval, i.TimeUtc, i.Degraded, i.LineName, FromUtc: i.FromUtc, In: i.In, Out: i.Out),
        QueueOccupancy o => new(Occupancy, o.TimeUtc, o.Degraded, o.ZoneName, Count: o.Count),
        QueueTrackSeen s => new(TrackSeen, s.TimeUtc, s.Degraded, TrackKey: s.TrackKey),
        _ => throw new ArgumentException($"Unknown input {input?.GetType().Name}.", nameof(input))
    };

    internal QueueInput ToInput() => Kind switch
    {
        Crossing => new QueueCrossing(Name, Direction, TrackKey, Utc(TimeUtc), Degraded),
        Interval => new QueueInterval(Name, In, Out, Utc(FromUtc ?? TimeUtc), Utc(TimeUtc), Degraded),
        Occupancy => new QueueOccupancy(Name, Count, Utc(TimeUtc), Degraded),
        TrackSeen => new QueueTrackSeen(TrackKey, Utc(TimeUtc), Degraded),
        _ => throw new InvalidDataException($"Unknown input kind '{Kind}' in a queue engine snapshot.")
    };

    internal static DateTime Utc(DateTime t) => DateTime.SpecifyKind(t, DateTimeKind.Utc);
}

/// <summary>A buffered input with its arrival order.</summary>
public sealed record QueueBufferedState(QueueInputState Input, bool Ahead, long Sequence);

/// <summary>A zone's latest occupancy reading.</summary>
public sealed record QueueOccupancyState(string Zone, int Count, DateTime AtUtc, bool Degraded);

/// <summary>A hand-over deadline of a tracked entrant.</summary>
public sealed record QueueHandoverState(string Key, DateTime Seen, DateTime Deadline);

/// <summary>Late events of one minute.</summary>
public sealed record LateMinuteState(DateTime MinuteUtc, long Count);

/// <summary>A held entrant in a snapshot.</summary>
public sealed record QueueEntrantState(DateTime EntryUtc, string TrackKey, string Group, bool Degraded, WaitMethod Method, long Order, DateTime? LastSeenUtc);

/// <summary>
/// Everything a <see cref="QueueStateEngine"/> holds, so that a stream worker can persist a zone's state at a checkpoint
/// and restore it on another instance after a rebalance (ARV-034). Restoring and continuing gives exactly the steps the
/// original engine would have given. Plain data: serialise it with System.Text.Json.
/// </summary>
public sealed record QueueEngineState
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public string QueueZone { get; init; }
    public IReadOnlyList<QueueBufferedState> Buffer { get; init; } = [];
    public IReadOnlyList<QueueEntrantState> Held { get; init; } = [];
    public IReadOnlyList<string> ResolvedTracks { get; init; } = [];
    public IReadOnlyList<QueueOccupancyState> Occupancy { get; init; } = [];
    public IReadOnlyList<QueueHandoverState> Handovers { get; init; } = [];
    public long Sequence { get; init; }
    public long Order { get; init; }
    public DateTime WatermarkUtc { get; init; }
    public DateTime CursorUtc { get; init; }
    public bool ResidualSuspect { get; init; }

    /// <summary>
    /// The instant of an occupancy reading whose empty-queue check (F5) still waits for the rest of that instant's events,
    /// because a full step ended inside it (ARV-114d): one tick after <see cref="WatermarkUtc"/>, where the step stopped.
    /// Null when no check is pending; a snapshot written before has none (that engine ran the check when the step filled).
    /// </summary>
    public DateTime? PendingAnchorUtc { get; init; }

    // What happened since the last step (an Offer can process events when the buffer is full).
    public IReadOnlyList<MovementCount> Movements { get; init; } = [];
    /// <summary>Line crossings since the last step (ARV-113); a snapshot written before has none.</summary>
    public IReadOnlyList<LineMovement> Lines { get; init; } = [];
    /// <summary>Occupancy samples at minute boundaries since the last step (ARV-114a); a snapshot written before has none.</summary>
    public IReadOnlyList<OccupancySample> OccupancySamples { get; init; } = [];
    /// <summary>Occupancy readings per zone and minute since the last step (ARV-114a); a snapshot written before has none.</summary>
    public IReadOnlyList<ZoneReadingMinute> Readings { get; init; } = [];
    public IReadOnlyList<RealisedWait> Waits { get; init; } = [];
    public IReadOnlyList<EntrantResolution> Resolutions { get; init; } = [];
    public IReadOnlyList<long> Counters { get; init; } = [];
    public int Reanchors { get; init; }
    public DateTime? EarliestLateUtc { get; init; }
    public IReadOnlyList<LateMinuteState> LateByMinute { get; init; } = [];
    public long BeyondHorizon { get; init; }
    public bool More { get; init; }
}

public sealed partial class QueueStateEngine
{
    /// <summary>The engine's whole state (see <see cref="QueueEngineState"/>).</summary>
    public QueueEngineState Capture() => new()
    {
        QueueZone = _geometry.QueueZone,
        Buffer = [.. _buffer.UnorderedItems.OrderBy(i => i.Priority.Time).ThenBy(i => i.Priority.Sequence)
            .Select(i => new QueueBufferedState(QueueInputState.From(i.Element.Input), i.Element.Ahead, i.Priority.Sequence))],
        Held = [.. _held.Select(e => new QueueEntrantState(e.EntryUtc, e.TrackKey, e.Group, e.Degraded, e.Method, e.Order, e.LastSeenUtc))],
        ResolvedTracks = [.. _resolvedOrder],
        Occupancy = [.. _occupancy.OrderBy(o => o.Key, StringComparer.Ordinal).Select(o => new QueueOccupancyState(o.Key, o.Value.Count, o.Value.AtUtc, o.Value.Degraded))],
        Handovers = [.. _handovers.UnorderedItems.OrderBy(h => h.Priority).ThenBy(h => h.Element.Key, StringComparer.Ordinal).ThenBy(h => h.Element.Seen)
            .Select(h => new QueueHandoverState(h.Element.Key, h.Element.Seen, h.Priority))],
        Sequence = _sequence,
        Order = _order,
        WatermarkUtc = _watermark,
        CursorUtc = _cursor,
        ResidualSuspect = _residualSuspect,
        PendingAnchorUtc = _anchorDueUtc,
        Movements = Movements(),
        Lines = LineMovements(),
        OccupancySamples = [.. _samples],
        Readings = ReadingMinutes(),
        Waits = [.. _waits],
        Resolutions = [.. _resolutions],
        Counters = [_late, _future, _invalid, _unknown, _duplicates, _reverse, _unmatched, _negative, _bufferFull, _forced, _tooManyDevices],
        Reanchors = _reanchors,
        EarliestLateUtc = _earliestLate,
        LateByMinute = [.. _lateByMinute.OrderBy(m => m.Key).Select(m => new LateMinuteState(m.Key, m.Value))],
        BeyondHorizon = _beyondHorizon,
        More = _more
    };

    /// <summary>
    /// An engine in the captured state, for the same geometry and settings. The state is checked against the bounds of
    /// <paramref name="settings"/> (a snapshot is data from storage, not trusted to be small).
    /// </summary>
    public static QueueStateEngine Restore(QueueZoneGeometry geometry, QueueEngineSettings settings, QueueEngineState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var engine = new QueueStateEngine(geometry, settings);
        var s = engine._settings;
        if (state.Version != QueueEngineState.CurrentVersion)
            throw new InvalidDataException($"Queue engine snapshot version {state.Version} is not {QueueEngineState.CurrentVersion}.");
        if (!string.Equals(state.QueueZone, geometry.QueueZone, StringComparison.Ordinal))
            throw new InvalidDataException($"The snapshot is for {state.QueueZone}, not {geometry.QueueZone}.");
        if ((state.Buffer?.Count ?? 0) > s.MaxBufferedEvents || (state.Held?.Count ?? 0) > s.MaxOpenEntrants ||
            (state.ResolvedTracks?.Count ?? 0) > s.MaxRememberedTracks || (state.Occupancy?.Count ?? 0) > 1 + geometry.OverflowZones.Count ||
            (state.Handovers?.Count ?? 0) > 2 * s.MaxOpenEntrants + 1024 || (state.Counters?.Count ?? 0) != 11 ||
            (state.LateByMinute?.Count ?? 0) > MaxLateMinutes ||
            (state.Movements?.Count ?? 0) + (state.Lines?.Count ?? 0) + (state.Waits?.Count ?? 0) + (state.Resolutions?.Count ?? 0) +
            (state.OccupancySamples?.Count ?? 0) + (state.Readings?.Count ?? 0) > s.MaxStepRecords + s.MaxBufferedEvents)
            throw new InvalidDataException("The queue engine snapshot exceeds the engine's bounds.");

        foreach (var (input, ahead, sequence) in state.Buffer ?? [])
        {
            if (input is null)
                throw new InvalidDataException("The queue engine snapshot has an empty input.");
            engine._buffer.Enqueue((input.ToInput(), ahead), (QueueInputState.Utc(input.TimeUtc), sequence));
            if (ahead)
                engine._aheadBuffered++;
        }

        foreach (var e in state.Held ?? [])
        {
            if (e is null)
                throw new InvalidDataException("The queue engine snapshot has an empty entrant.");
            var group = e.Group ?? Anonymous;
            if (!engine._groups.TryGetValue(group, out var members))
                engine._groups[group] = members = new LinkedList<Entrant>();
            var entrant = new Entrant(QueueInputState.Utc(e.EntryUtc), e.TrackKey, group, e.Degraded, e.Method, e.Order)
            {
                LastSeenUtc = e.LastSeenUtc is { } seen ? QueueInputState.Utc(seen) : null
            };
            entrant.InAll = engine._held.AddLast(entrant);
            entrant.InGroup = members.AddLast(entrant);
            if (e.TrackKey is not null && !engine._byTrack.TryAdd(e.TrackKey, entrant))
                throw new InvalidDataException("The queue engine snapshot holds a track twice.");
            if (e.Degraded)
                engine._degradedHeld++;
        }

        foreach (var track in state.ResolvedTracks ?? [])
        {
            if (track is not null && engine._resolvedTracks.Add(track))
                engine._resolvedOrder.Enqueue(track);
        }

        foreach (var (zone, count, at, degraded) in state.Occupancy ?? [])
        {
            if (zone is null)
                continue;
            if (count is < 0 or > Sensing.CanonicalEventRules.MaxOccupancy)
                throw new InvalidDataException("The queue engine snapshot has a zone occupancy out of the canonical bounds.");
            engine._occupancy[zone] = (count, QueueInputState.Utc(at), degraded);
        }

        foreach (var (key, seen, deadline) in state.Handovers ?? [])
            if (key is not null)
                engine._handovers.Enqueue((key, QueueInputState.Utc(seen)), QueueInputState.Utc(deadline));

        engine._sequence = state.Sequence;
        engine._order = state.Order;
        engine._watermark = QueueInputState.Utc(state.WatermarkUtc);
        engine._cursor = QueueInputState.Utc(state.CursorUtc);
        engine._residualSuspect = state.ResidualSuspect;
        if (state.PendingAnchorUtc is { } pending)
            engine._anchorDueUtc = PendingAnchor(engine, QueueInputState.Utc(pending));
        foreach (var m in (state.Movements ?? []).Where(m => m is not null))
        {
            if (m.TrackedEntries < 0 || m.TrackedEntries > m.Entries)
                throw new InvalidDataException("The queue engine snapshot has more tracked entries than entries in a minute.");
            engine._movements[QueueInputState.Utc(m.MinuteUtc)] = (m.Entries, m.Exits, m.DegradedEntries, m.DegradedExits, m.TrackedEntries);
        }

        // Health inputs (ARV-114a), checked as the engine writes them (CWE-501, CWE-120): whole plausible minutes, the
        // queue's own zones, readings within the canonical bounds, samples within the queue's bound, each minute (and
        // zone) once.
        var sampled = new HashSet<DateTime>();
        foreach (var o in (state.OccupancySamples ?? []).Where(o => o is not null))
        {
            var at = QueueInputState.Utc(o.AtUtc);
            if (!LineCounts.IsMinute(at) || o.Count < 0 || o.Count > geometry.MaxQueueOccupancy)
                throw new InvalidDataException("The queue engine snapshot has an occupancy sample off a minute boundary or out of bounds.");
            if (!sampled.Add(at))
                throw new InvalidDataException("The queue engine snapshot has an occupancy sample of a minute twice.");
            engine._samples.Add(o with { AtUtc = at });
        }

        foreach (var r in (state.Readings ?? []).Where(r => r is not null))
        {
            var minute = QueueInputState.Utc(r.MinuteUtc);
            if (!geometry.CountsInQueue(r.ZoneName) || !LineCounts.IsMinute(minute) || r.Min < 0 || r.Min > r.Max || r.Max > Sensing.CanonicalEventRules.MaxOccupancy)
                throw new InvalidDataException("The queue engine snapshot has an occupancy reading of another zone, off a minute or out of bounds.");
            if (!engine._readings.TryAdd((minute, r.ZoneName), (r.Min, r.Max)))
                throw new InvalidDataException("The queue engine snapshot has a zone's readings of a minute twice.");
        }
        foreach (var l in (state.Lines ?? []).Where(l => l is not null))
        {
            // The same checks as LineCounts.Restore (CWE-501, CWE-120): the zone's line with its role, counts that are not
            // negative, an aligned plausible UTC minute, and each line minute once.
            var role = geometry.RoleOf(l.LineName);
            var minute = QueueInputState.Utc(l.MinuteUtc);
            if (role == QueueLineRole.Unknown || role != l.Role || l.In < 0 || l.Out < 0 || !LineCounts.IsMinute(minute))
                throw new InvalidDataException("The queue engine snapshot counts a line that is not the zone's, a negative count or an unaligned minute.");
            if (!engine._lines.TryAdd((minute, l.LineName), (l.Role, l.In, l.Out)))
                throw new InvalidDataException("The queue engine snapshot counts a line minute twice.");
        }

        engine._waits.AddRange((state.Waits ?? []).Where(w => w is not null).Select(w => w with { EntryUtc = QueueInputState.Utc(w.EntryUtc), ExitUtc = QueueInputState.Utc(w.ExitUtc) }));
        engine._resolutions.AddRange((state.Resolutions ?? []).Where(r => r is not null).Select(r => r with { EntryUtc = QueueInputState.Utc(r.EntryUtc), ResolvedUtc = QueueInputState.Utc(r.ResolvedUtc) }));
        var c = state.Counters;
        (engine._late, engine._future, engine._invalid, engine._unknown, engine._duplicates, engine._reverse) = (c[0], c[1], c[2], c[3], c[4], c[5]);
        (engine._unmatched, engine._negative, engine._bufferFull, engine._forced, engine._tooManyDevices) = (c[6], c[7], c[8], c[9], c[10]);
        engine._reanchors = state.Reanchors;
        engine._earliestLate = state.EarliestLateUtc is { } late ? QueueInputState.Utc(late) : null;
        foreach (var (minute, count) in state.LateByMinute ?? [])
            engine._lateByMinute[QueueInputState.Utc(minute)] = count;
        engine._beyondHorizon = state.BeyondHorizon;
        engine._more = state.More;
        return engine;
    }

    /// <summary>
    /// A pending empty-queue check from a snapshot (ARV-114d), checked as the engine leaves one (CWE-501): only a full step
    /// that stopped inside an instant carries it, so it is that instant. It is a plausible UTC time, aligned with where
    /// the step stopped (exactly one tick after the watermark, never at or before it, where every event of the instant
    /// was applied and the check has run, nor any later, where none of them was), the time of the last processed event,
    /// and the time of an occupancy reading of the queue the engine holds (the reading that set it). Anything else is
    /// refused, so a snapshot cannot make the engine drop people at an instant of its choosing.
    /// </summary>
    private static DateTime PendingAnchor(QueueStateEngine engine, DateTime at)
    {
        if (!ZoneProcessor.Plausible(at) || at.AddTicks(-1) != engine._watermark || at != engine._cursor ||
            !engine._occupancy.Any(o => engine._geometry.CountsInQueue(o.Key) && o.Value.AtUtc == at) ||
            // The event that stopped the step is still buffered, so the earliest buffered event is at the instant.
            !engine._buffer.TryPeek(out _, out var first) || first.Time != at)
            throw new InvalidDataException("The queue engine snapshot has a pending empty-queue check where no full step leaves one.");
        return at;
    }
}
