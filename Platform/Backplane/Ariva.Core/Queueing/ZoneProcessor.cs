using Ariva.Core.Sensing;

namespace Ariva.Core.Queueing;

/// <summary>Settings of a queue zone's stream processing (ARV-034).</summary>
public sealed record ZoneProcessorSettings
{
    public QueueEngineSettings Engine { get; init; } = new();

    public BinSettings Bins { get; init; } = new();

    public NowcastSettings Nowcast { get; init; } = new();

    /// <summary>m of F8: the exit-rate window in whole minutes (Proposed 5).</summary>
    public int ExitWindowMinutes { get; init; } = 5;

    /// <summary>Outputs held between checkpoints at most; beyond it the zone asks for a checkpoint (<see cref="ZoneProcessor.Full"/>).</summary>
    public int MaxPendingOutputs { get; init; } = 50_000;

    public IEnumerable<string> Problems()
    {
        foreach (var p in Engine?.Problems() ?? ["Engine settings are required."])
            yield return p;
        foreach (var p in Bins?.Problems() ?? ["Bin settings are required."])
            yield return p;
        foreach (var p in Nowcast?.Problems() ?? ["Nowcast settings are required."])
            yield return p;
        if (ExitWindowMinutes is < 1 or > 60)
            yield return "ExitWindowMinutes is from 1 to 60.";
        if (MaxPendingOutputs is < 100 or > 1_000_000)
            yield return "MaxPendingOutputs is from 100 to 1,000,000.";
    }
}

/// <summary>
/// A queue zone at the end of a minute: the queue length the engine reports at its watermark, and the nowcast of F8 from
/// it and the exits of the last m whole minutes (the desk term joins with desk sessions, ARV-049).
/// </summary>
public sealed record QueueLiveMinute(
    string ZoneKey,
    DateTime MinuteUtc,
    int QueueLength,
    bool LengthMeasured,
    bool LengthDegraded,
    double? NowcastMinutes,
    double? Throughput,
    NoServiceReason? NoService,
    bool NowcastDegraded);

/// <summary>What a zone produced since the last drain: rows to persist and what to recompute.</summary>
public sealed record ZoneOutputs(
    string ZoneKey,
    IReadOnlyList<MinuteResult> Minutes,
    IReadOnlyList<BinResult> Bins,
    IReadOnlyList<QueueLiveMinute> Live,
    IReadOnlyList<RecomputationRequest> Recomputations)
{
    public int Count => Minutes.Count + Bins.Count + Live.Count + Recomputations.Count;
}

/// <summary>Counts of what a zone refused, for health.</summary>
public sealed record ZoneProcessorCounters(long Batches, long Uncommissioned, long WrongZone, long Invalid);

/// <summary>Everything a <see cref="ZoneProcessor"/> holds between checkpoints (outputs drained).</summary>
public sealed record ZoneProcessorState
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public string ZoneKey { get; init; }
    public int ProfileVersion { get; init; }
    public QueueEngineState Engine { get; init; }
    public BinAccumulatorState Bins { get; init; }
    public IReadOnlyList<ExitMinuteState> Exits { get; init; } = [];
    public DateTime ReferenceUtc { get; init; }
    public DateTime? LastLiveMinuteUtc { get; init; }
    public ZoneProcessorCounters Counters { get; init; }
}

/// <summary>
/// One queue zone's stream processing (ARV-034): sensing batches mapped to the queue engine's inputs, the engine stepped
/// with Ariva's own receive time as the reference clock (so that replaying the same records gives the same results),
/// its steps attributed to bins (F6, F7), and a live row with the queue length and the nowcast (F8) at the end of each
/// minute. Every batch is checked again here (CWE-501: Kafka is a trust boundary): the zone must be this zone, the
/// device commissioned, the counts and names within the canonical bounds. Pure: no I/O and no clock of its own.
/// </summary>
public sealed class ZoneProcessor
{
    private readonly ZoneProcessorSettings _settings;
    private readonly QueueZoneGeometry _geometry;
    private QueueStateEngine _engine;
    private BinAccumulator _bins;
    private ExitRate _exits;
    private DateTime _reference = DateTime.MinValue;
    private DateTime? _lastLive;
    private long _batches, _uncommissioned, _wrongZone, _invalid;
    private readonly List<MinuteResult> _minutes = [];
    private readonly List<BinResult> _binResults = [];
    private readonly List<QueueLiveMinute> _live = [];
    private readonly List<RecomputationRequest> _recomputations = [];

    public ZoneProcessor(string zoneKey, QueueZoneGeometry geometry, int profileVersion, ZoneProcessorSettings settings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zoneKey);
        ArgumentNullException.ThrowIfNull(geometry);
        _settings = settings ?? new ZoneProcessorSettings();
        var problems = _settings.Problems().ToList();
        if (problems.Count > 0)
            throw new ArgumentException(string.Join(" ", problems), nameof(settings));
        ZoneKey = zoneKey;
        ProfileVersion = profileVersion;
        _geometry = geometry;
        _engine = new QueueStateEngine(geometry, _settings.Engine);
        _bins = new BinAccumulator(geometry.QueueZone, profileVersion, _settings.Bins);
        _exits = new ExitRate();
    }

    public string ZoneKey { get; }

    public int ProfileVersion { get; }

    /// <summary>The reference clock: the latest receive time seen.</summary>
    public DateTime ReferenceUtc => _reference;

    public DateTime WatermarkUtc => _engine.WatermarkUtc;

    public ZoneProcessorCounters Counters => new(_batches, _uncommissioned, _wrongZone, _invalid);

    /// <summary>Outputs are waiting beyond the bound: checkpoint before offering more.</summary>
    public bool Full => _minutes.Count + _binResults.Count + _live.Count + _recomputations.Count >= _settings.MaxPendingOutputs;

    /// <summary>
    /// Offers one batch and steps the zone to its receive time (never backwards; <paramref name="referenceCapUtc"/>
    /// bounds a receive time that lies ahead of the host's clock).
    /// </summary>
    public void Offer(SensingBatch batch, DateTime referenceCapUtc)
    {
        ArgumentNullException.ThrowIfNull(batch);
        _batches++;
        if (!string.Equals(batch.ZoneKey, ZoneKey, StringComparison.Ordinal))
        {
            _wrongZone++;
            return;
        }

        if (!batch.Commissioned)
        {
            // Events of a device in commissioning check the installation; they never reach KPIs (ARV-023).
            _uncommissioned++;
            return;
        }

        if (!Plausible(batch.ReceivedUtc) || string.IsNullOrWhiteSpace(batch.DeviceCode) || batch.DeviceCode.Length > 16 ||
            batch.DeviceCode.Contains('/', StringComparison.Ordinal))
        {
            _invalid++;
            return;
        }

        var reference = batch.ReceivedUtc < referenceCapUtc ? batch.ReceivedUtc : referenceCapUtc;
        if (reference > _reference)
            _reference = reference;
        var unreliable = batch.Clock?.State == ClockState.Unreliable;
        foreach (var input in Inputs(batch, unreliable))
        {
            if (input is null)
                _invalid++;
            else
                _engine.Offer(input, _reference);
        }

        Step();
    }

    /// <summary>Moves an idle zone's clock forward (the host decides when a zone is idle and caught up).</summary>
    public void Tick(DateTime referenceUtc)
    {
        if (!Plausible(referenceUtc))
            throw new ArgumentException("The reference time is UTC and plausible.", nameof(referenceUtc));
        if (referenceUtc > _reference)
            _reference = referenceUtc;
        Step();
    }

    /// <summary>The events a batch carries at most (Ingest's limit); a longer list is cut.</summary>
    public const int MaxEventsPerBatch = 3_000;

    private IEnumerable<QueueInput> Inputs(SensingBatch batch, bool unreliable)
    {
        bool Degraded(SensedFlags flags) => unreliable || flags != SensedFlags.None;
        string Track(string id) => id is null ? null : $"{batch.DeviceCode}/{id}";

        // Every event is checked against the canonical bounds again (CWE-501), and so is its corrected time.
        static bool Valid<T>(Sensed<T> e) where T : CanonicalEvent =>
            e?.Event is not null && Plausible(e.TimeUtc) && CanonicalEventRules.Validate(e.Event).Count == 0;
        switch (batch)
        {
            case VendorLineCrossingBatch c:
                foreach (var e in (c.Crossings ?? []).Take(MaxEventsPerBatch))
                    yield return !Valid(e) ? null : new QueueCrossing(e.Event.LineName, e.Event.Direction, Track(e.Event.TrackId), e.TimeUtc, Degraded(e.Flags));
                break;
            case ZoneOccupancyBatch o:
                foreach (var e in (o.Occupancy ?? []).Take(MaxEventsPerBatch))
                    yield return !Valid(e) ? null : new QueueOccupancy(e.Event.ZoneName, e.Event.Count, e.TimeUtc, Degraded(e.Flags));
                break;
            case IntervalCountBatch i:
                foreach (var e in (i.Intervals ?? []).Take(MaxEventsPerBatch))
                {
                    var from = Valid(e) ? Shift(e.Event.FromUtc, e.Event.TimeUtc, e.TimeUtc) : null;
                    yield return from is null ? null : new QueueInterval(e.Event.LineName, e.Event.In, e.Event.Out, from.Value, e.TimeUtc, Degraded(e.Flags));
                }

                break;
            case TrackSampleBatch t:
                foreach (var e in (t.Samples ?? []).Take(MaxEventsPerBatch))
                    yield return !Valid(e) ? null : new QueueTrackSeen(Track(e.Event.TrackId), e.TimeUtc, Degraded(e.Flags));
                break;
        }
    }

    /// <summary>A time the engines can work with: UTC, from the year 2000 and well before the end of the calendar.</summary>
    public static bool Plausible(DateTime t) => t.Kind == DateTimeKind.Utc && t >= EarliestUtc && t <= LatestUtc;

    internal static readonly DateTime EarliestUtc = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    internal static readonly DateTime LatestUtc = new(9000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // An interval's start moves with its end when the clock was corrected (both validated, at most a day apart).
    private static DateTime? Shift(DateTime from, DateTime sentEnd, DateTime correctedEnd)
    {
        var start = correctedEnd - (sentEnd - from);
        return Plausible(start) ? start : null;
    }

    private void Step()
    {
        var before = _engine.WatermarkUtc;
        QueueStep step;
        var guard = 0;
        do
        {
            step = _engine.Advance(_reference);
            var update = _bins.Accept(step);
            _minutes.AddRange(update.Minutes);
            _binResults.AddRange(update.Bins);
            _recomputations.AddRange(update.Recomputations);
            _exits.Add(step.Movements);
        }
        while (step.More && ++guard < 1_000);

        if (step.WatermarkUtc > before && before > DateTime.MinValue)
            _exits.Observed(before, step.WatermarkUtc);
        Live(step);
    }

    private void Live(QueueStep step)
    {
        var watermark = step.WatermarkUtc;
        if (watermark <= DateTime.MinValue.AddMinutes(1))
            return;
        var minute = new DateTime(watermark.Ticks - watermark.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc).AddMinutes(-1);
        if (_lastLive is { } last && minute <= last)
            return;
        _lastLive = minute;
        var length = step.Length;
        var window = _exits.Window(minute.AddMinutes(1), _settings.ExitWindowMinutes);
        var nowcast = Nowcast.Compute(new NowcastInput
        {
            QueueLength = length.Count,
            ExitsInWindow = window.Complete ? window.Exits : null,
            ExitWindowMinutes = _settings.ExitWindowMinutes,
            Degraded = length.Degraded || window.DegradedExits > 0
        }, _settings.Nowcast);
        _live.Add(new QueueLiveMinute(ZoneKey, minute, length.Count, length.FromSensors, length.Degraded, nowcast.Minutes, nowcast.Throughput, nowcast.NoService, nowcast.Degraded));
    }

    /// <summary>The outputs since the last drain, which the host persists with the zone's state in one transaction.</summary>
    public ZoneOutputs Drain()
    {
        var outputs = new ZoneOutputs(ZoneKey, [.. _minutes], [.. _binResults], [.. _live], [.. _recomputations]);
        _minutes.Clear();
        _binResults.Clear();
        _live.Clear();
        _recomputations.Clear();
        return outputs;
    }

    /// <summary>
    /// The outputs since the last acknowledgement, without removing them: the host writes them with the zone's state and
    /// calls <see cref="Acknowledge"/> only after its transaction committed, so a failed or cancelled write loses nothing.
    /// </summary>
    public ZoneOutputs Peek() => new(ZoneKey, [.. _minutes], [.. _binResults], [.. _live], [.. _recomputations]);

    /// <summary>Removes the outputs a <see cref="Peek"/> returned (the ones before any produced since).</summary>
    public void Acknowledge(ZoneOutputs written)
    {
        ArgumentNullException.ThrowIfNull(written);
        _minutes.RemoveRange(0, Math.Min(written.Minutes.Count, _minutes.Count));
        _binResults.RemoveRange(0, Math.Min(written.Bins.Count, _binResults.Count));
        _live.RemoveRange(0, Math.Min(written.Live.Count, _live.Count));
        _recomputations.RemoveRange(0, Math.Min(written.Recomputations.Count, _recomputations.Count));
    }

    /// <summary>
    /// The zone's state. Pending outputs are not part of it: they belong with the state in the same write (see
    /// <see cref="Peek"/>), since the state already reflects them.
    /// </summary>
    public ZoneProcessorState Capture()
    {
        return new ZoneProcessorState
        {
            ZoneKey = ZoneKey,
            ProfileVersion = ProfileVersion,
            Engine = _engine.Capture(),
            Bins = _bins.Capture(),
            Exits = _exits.Capture(),
            ReferenceUtc = _reference,
            LastLiveMinuteUtc = _lastLive,
            Counters = Counters
        };
    }

    /// <summary>
    /// A zone in the captured state, for the same zone, geometry, profile version and settings. A snapshot is data from
    /// storage (CWE-501): every key, count and time in it is checked, nothing after <paramref name="notAfterUtc"/> (the
    /// host's clock plus its allowance) is accepted, and any problem is an <see cref="InvalidDataException"/>.
    /// </summary>
    public static ZoneProcessor Restore(string zoneKey, QueueZoneGeometry geometry, int profileVersion, ZoneProcessorSettings settings, ZoneProcessorState state,
        DateTime notAfterUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        var problems = SnapshotChecks.Of(state, zoneKey, geometry?.QueueZone, profileVersion, notAfterUtc).ToList();
        if (problems.Count > 0)
            throw new InvalidDataException("The zone snapshot is not valid: " + string.Join(" ", problems.Take(5)));
        var zone = new ZoneProcessor(state.ZoneKey, geometry, state.ProfileVersion, settings);
        zone._engine = QueueStateEngine.Restore(geometry, zone._settings.Engine, state.Engine);
        zone._bins = BinAccumulator.Restore(zone._settings.Bins, state.Bins);
        zone._exits = ExitRate.Restore(state.Exits);
        zone._reference = DateTime.SpecifyKind(state.ReferenceUtc, DateTimeKind.Utc);
        zone._lastLive = state.LastLiveMinuteUtc is { } live ? DateTime.SpecifyKind(live, DateTimeKind.Utc) : null;
        if (state.Counters is { } c)
            (zone._batches, zone._uncommissioned, zone._wrongZone, zone._invalid) = (c.Batches, c.Uncommissioned, c.WrongZone, c.Invalid);
        return zone;
    }
}
