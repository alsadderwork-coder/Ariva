using Ariva.Core.Desks;
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

    /// <summary>
    /// A device heard before and silent for longer than this is out and its zone degraded (ARV-036); the same 180 seconds
    /// as Devices:Health:HeartbeatTimeoutSeconds, so the stream and the health screen agree.
    /// </summary>
    public int DeviceSilenceSeconds { get; init; } = 180;

    /// <summary>A device silent this long is treated as removed: its outage ends there and it is no longer tracked.</summary>
    public int DeviceForgetHours { get; init; } = 24;

    /// <summary>Devices tracked per zone at most.</summary>
    public int MaxDevices { get; init; } = 256;

    /// <summary>
    /// A desk term (ARV-064) is used for a live minute while its own minute is at most this many minutes older; desk
    /// minutes close about a minute after the queue's (the desk feed's lateness), so 5 minutes leaves room for a slow read.
    /// </summary>
    public int DeskTermFreshMinutes { get; init; } = 5;

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
        if (DeviceSilenceSeconds is < 30 or > 3_600)
            yield return "DeviceSilenceSeconds is from 30 to 3,600.";
        if (DeviceForgetHours is < 1 or > 72 || DeviceForgetHours * 3_600 <= DeviceSilenceSeconds)
            yield return "DeviceForgetHours is from 1 to 72 and longer than the silence limit.";
        if (MaxDevices is < 1 or > 4_096)
            yield return "MaxDevices is from 1 to 4,096.";
        if (DeskTermFreshMinutes is < 1 or > 30)
            yield return "DeskTermFreshMinutes is from 1 to 30.";
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
    bool NowcastDegraded)
{
    /// <summary>
    /// The shadow nowcast without AMAN inputs (ARV-117), written in the same checkpoint as this minute's
    /// <c>queue_minute</c> row, to <c>queue_minute_shadow</c> (ARV-117a), and read only by the validation comparison. Never serialised: not in the live snapshot or the replay ledger (a replay has no desk term,
    /// so its shadow is the published nowcast in every minute).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ShadowNowcast Shadow { get; init; }
}

/// <summary>
/// What a zone produced since the last drain: rows to persist, what to recompute, device outages that ended, the closed
/// minutes of each line (ARV-113), the health checks of each bin result (ARV-114a), the closed minutes of each
/// overflow band with the changes they made (ARV-115), and the readings of the desks' staff and service zones for the
/// desk engine (ARV-116: counts only).
/// </summary>
public sealed record ZoneOutputs(
    string ZoneKey,
    IReadOnlyList<MinuteResult> Minutes,
    IReadOnlyList<BinResult> Bins,
    IReadOnlyList<QueueLiveMinute> Live,
    IReadOnlyList<RecomputationRequest> Recomputations,
    IReadOnlyList<DeviceOutage> Outages = null,
    IReadOnlyList<LineMinute> Lines = null,
    IReadOnlyList<ZoneHealthBin> Health = null,
    IReadOnlyList<OverflowMinute> Overflow = null,
    IReadOnlyList<OverflowChange> OverflowChanges = null,
    IReadOnlyList<DeskZoneSample> DeskReadings = null)
{
    public IReadOnlyList<DeviceOutage> Outages { get; init; } = Outages ?? [];

    public IReadOnlyList<LineMinute> Lines { get; init; } = Lines ?? [];

    public IReadOnlyList<ZoneHealthBin> Health { get; init; } = Health ?? [];

    public IReadOnlyList<OverflowMinute> Overflow { get; init; } = Overflow ?? [];

    public IReadOnlyList<OverflowChange> OverflowChanges { get; init; } = OverflowChanges ?? [];

    public IReadOnlyList<DeskZoneSample> DeskReadings { get; init; } = DeskReadings ?? [];

    public int Count => Minutes.Count + Bins.Count + Live.Count + Recomputations.Count + Outages.Count + Lines.Count + Health.Count + Overflow.Count +
        OverflowChanges.Count + DeskReadings.Count;
}

/// <summary>
/// Counts of what a zone refused, for health. <see cref="OtherZones"/> (ARV-116): occupancy readings of a zone that is
/// neither the queue's nor a staff or service zone linked to a desk, kept from the queue engine.
/// </summary>
public sealed record ZoneProcessorCounters(long Batches, long Uncommissioned, long WrongZone, long Invalid, long OtherZones = 0);

/// <summary>Everything a <see cref="ZoneProcessor"/> holds between checkpoints (outputs drained).</summary>
public sealed record ZoneProcessorState
{
    /// <summary>
    /// 2 adds device liveness (ARV-036); a version 1 snapshot restores with no devices heard yet. 3 adds the open line
    /// minutes (ARV-113); an earlier snapshot restores with none open. 4 adds the health tallies of the open bins and the
    /// engine's pending health inputs (ARV-114a); an earlier snapshot restores with empty ones, so the bins open at the
    /// upgrade report the tracks and occupancy seen after it only. 5 adds the overflow bands' open minutes and states
    /// (ARV-115); an earlier snapshot restores with none open and every band empty, so a band occupied at the upgrade is
    /// reported occupied at its next minute with occupancy. 6 adds the Unknown state of a band silent beyond the occupancy
    /// freshness window (ARV-115, the owner's decision of 2026-10-06); a version 5 snapshot has no such property and
    /// restores with every band it lists occupied or empty, as before. 7 adds the latest reading passed on per desk zone
    /// (ARV-116); an earlier snapshot restores with none, so each desk zone's next reading passes.
    /// </summary>
    public const int CurrentVersion = 7;

    public int Version { get; init; } = CurrentVersion;
    public string ZoneKey { get; init; }
    public int ProfileVersion { get; init; }
    public QueueEngineState Engine { get; init; }
    public BinAccumulatorState Bins { get; init; }
    public IReadOnlyList<ExitMinuteState> Exits { get; init; } = [];
    public DateTime ReferenceUtc { get; init; }
    public DateTime? LastLiveMinuteUtc { get; init; }
    public ZoneProcessorCounters Counters { get; init; }
    public IReadOnlyList<DeviceLivenessState> Devices { get; init; } = [];
    public IReadOnlyList<RecentOutageState> RecentOutages { get; init; } = [];
    public IReadOnlyList<LineMinuteState> Lines { get; init; } = [];

    /// <summary>The latest line minute released before the watermark passed it (<see cref="LineCounts.ReleasedEarlyThroughUtc"/>).</summary>
    public DateTime? LinesReleasedEarlyThroughUtc { get; init; }

    /// <summary>The overflow bands' minutes still open (ARV-115).</summary>
    public IReadOnlyList<OverflowMinuteState> OverflowOpen { get; init; } = [];

    /// <summary>The overflow bands' states (ARV-115): occupied, empty or unknown, as of each band's latest closed minute.</summary>
    public IReadOnlyList<OverflowBandState> OverflowBands { get; init; } = [];

    /// <summary>The latest reading passed on to the desk engine per staff and service zone (ARV-116).</summary>
    public IReadOnlyList<DeskZoneMemoState> DeskZones { get; init; } = [];
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
    private long _batches, _uncommissioned, _wrongZone, _invalid, _otherZones;
    private readonly List<MinuteResult> _minutes = [];
    private readonly List<BinResult> _binResults = [];
    private readonly List<QueueLiveMinute> _live = [];
    private readonly List<RecomputationRequest> _recomputations = [];
    private readonly List<DeviceOutage> _outages = [];
    private readonly List<LineMinute> _lineMinutes = [];
    private readonly List<ZoneHealthBin> _health = [];
    private readonly List<OverflowMinute> _overflow = [];
    private readonly List<OverflowChange> _overflowChanges = [];
    private readonly List<DeskZoneSample> _deskReadings = [];
    private DeskZoneReadings _deskZones;
    private LineCounts _lines = new();
    private OverflowBands _bands;
    private DeviceLiveness _liveness;
    private bool _watchDevices = true;
    private DeskTerm _desks;

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
        _bins = new BinAccumulator(geometry.QueueZone, profileVersion, _settings.Bins, geometry);
        _exits = new ExitRate();
        _liveness = NewLiveness(_settings);
        _bands = new OverflowBands(geometry, _settings.Engine.OccupancyFreshFor);
        _deskZones = new DeskZoneReadings(geometry.DeskZones);
    }

    private static DeviceLiveness NewLiveness(ZoneProcessorSettings settings) =>
        new(TimeSpan.FromSeconds(settings.DeviceSilenceSeconds), TimeSpan.FromHours(settings.DeviceForgetHours), settings.MaxDevices);

    public string ZoneKey { get; }

    public int ProfileVersion { get; }

    /// <summary>The reference clock: the latest receive time seen.</summary>
    public DateTime ReferenceUtc => _reference;

    public DateTime WatermarkUtc => _engine.WatermarkUtc;

    public ZoneProcessorCounters Counters => new(_batches, _uncommissioned, _wrongZone, _invalid, _otherZones);

    /// <summary>What happened to the desk zones' readings (ARV-116), for health; not part of the saved state.</summary>
    public DeskZoneReadingCounters DeskZoneCounters => _deskZones.Counters;

    /// <summary>
    /// The desk term of the desks serving this queue (ARV-064), from the stream host's desk minutes; null when none is
    /// known. Not part of the saved state: the host gives it again within seconds of a restart. Replays never set it. Its
    /// <see cref="DeskTerm.SensorOnly"/> part gives the shadow nowcast (ARV-117).
    /// </summary>
    public void UseDesks(DeskTerm desks) => _desks = desks;

    /// <summary>The desk term in use, if any.</summary>
    public DeskTerm Desks => _desks;

    /// <summary>Outputs are waiting beyond the bound: checkpoint before offering more.</summary>
    public bool Full => _minutes.Count + _binResults.Count + _live.Count + _recomputations.Count + _outages.Count + _lineMinutes.Count + _health.Count +
        _overflow.Count + _overflowChanges.Count + _deskReadings.Count >= _settings.MaxPendingOutputs;

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

        if (!Plausible(batch.ReceivedUtc) || !DeviceCodes.IsValid(batch.DeviceCode))
        {
            _invalid++;
            return;
        }

        var reference = batch.ReceivedUtc < referenceCapUtc ? batch.ReceivedUtc : referenceCapUtc;
        if (reference > _reference)
            _reference = reference;
        // Every batch of a commissioned device says it was heard; a health report may say it is offline (ARV-036).
        if (_liveness.Heard(ZoneKey, batch.DeviceCode, _reference, batch is not DeviceStatusBatch { Online: false }) is { } ended)
            Ended(ended.Outage, ended.MarkFromUtc);
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

    /// <summary>
    /// Moves an idle zone's clock forward (the host decides when a zone is idle and caught up). A zone that has not taken
    /// an event yet has no clock to move, so the host's "its clock plus the idle time" is no time at all: nothing happens
    /// (ARV-064: an uncommissioned device's zone logged an error every idle tick).
    /// </summary>
    public void Tick(DateTime referenceUtc)
    {
        if (_reference == DateTime.MinValue && !Plausible(referenceUtc))
            return;
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
        var prefix = batch.DeviceCode + "/";
        // Ingest namespaces track ids by device (<code>/<id>) and the archive keeps a day's pseudonym (<code>/~ and 22
        // characters) instead; the engine's id is <code>/<the device's own part>, and the canonical rules check that part.
        string Local(string id) => id is null ? null : id.StartsWith(prefix, StringComparison.Ordinal) ? id[prefix.Length..] : id;
        string Track(string id) => id is null ? null : prefix + Local(id);
        static string Checkable(string local) => local is { Length: 23 } && local[0] == '~' && local.AsSpan(1).ContainsAnyExcept(Base64Url) == false ? "pseudonym" : local;

        // Every event is checked against the canonical bounds again (CWE-501), and so is its corrected time.
        static bool Valid<T>(Sensed<T> e, T checkable) where T : CanonicalEvent =>
            e?.Event is not null && Plausible(e.TimeUtc) && CanonicalEventRules.Validate(checkable).Count == 0;
        switch (batch)
        {
            case VendorLineCrossingBatch c:
                foreach (var e in (c.Crossings ?? []).Take(MaxEventsPerBatch))
                    yield return e?.Event is null || !Valid(e, e.Event with { TrackId = e.Event.TrackId is null ? null : Checkable(Local(e.Event.TrackId)) }) ? null
                        : new QueueCrossing(e.Event.LineName, e.Event.Direction, Track(e.Event.TrackId), e.TimeUtc, Degraded(e.Flags));
                break;
            case ZoneOccupancyBatch o:
                foreach (var e in (o.Occupancy ?? []).Take(MaxEventsPerBatch))
                {
                    if (!Valid(e, e?.Event))
                    {
                        yield return null;
                        continue;
                    }

                    // ARV-116: a staff or service zone that names its desk is a desk signal (F10 rank 3 or 4), never part of the
                    // queue; only its count, time and flag cross to the desk engine.
                    if (_deskZones.Links(e.Event.ZoneName))
                    {
                        if (_deskZones.Accept(e.Event.ZoneName, e.Event.Count, e.TimeUtc, Degraded(e.Flags)) is { } sample)
                            _deskReadings.Add(sample);
                        continue;
                    }

                    // Any other zone (a staff or service zone without a desk, or one the profile's links leave out) never moves
                    // the queue: it is counted here rather than buffered by the engine, where a late one would count as a
                    // late event of the queue's bins.
                    if (!_geometry.CountsInQueue(e.Event.ZoneName))
                    {
                        _otherZones++;
                        continue;
                    }

                    yield return new QueueOccupancy(e.Event.ZoneName, e.Event.Count, e.TimeUtc, Degraded(e.Flags));
                }

                break;
            case IntervalCountBatch i:
                foreach (var e in (i.Intervals ?? []).Take(MaxEventsPerBatch))
                {
                    var from = Valid(e, e?.Event) ? Shift(e.Event.FromUtc, e.Event.TimeUtc, e.TimeUtc) : null;
                    yield return from is null ? null : new QueueInterval(e.Event.LineName, e.Event.In, e.Event.Out, from.Value, e.TimeUtc, Degraded(e.Flags));
                }

                break;
            case TrackSampleBatch t:
                foreach (var e in (t.Samples ?? []).Take(MaxEventsPerBatch))
                    yield return e?.Event is null || !Valid(e, e.Event with { TrackId = Checkable(Local(e.Event.TrackId)) }) ? null
                        : new QueueTrackSeen(Track(e.Event.TrackId), e.TimeUtc, Degraded(e.Flags));
                break;
        }
    }

    private static readonly System.Buffers.SearchValues<char> Base64Url =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_");

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

    private void Ended(DeviceOutage outage, DateTime markFromUtc)
    {
        _outages.Add(outage);
        _recomputations.AddRange(_bins.Mark(markFromUtc, outage.ToUtc, BinQuality.Degraded));
    }

    // Before the engine steps, so that bins finalised in this step already carry the marks of the devices out now.
    private void WatchDevices()
    {
        if (!_watchDevices || _reference == DateTime.MinValue)
            return;
        foreach (var (outage, markFrom) in _liveness.Observe(ZoneKey, _reference))
            Ended(outage, markFrom);
        foreach (var (from, to) in _liveness.MarksDue(_reference, MarkEvery))
            _recomputations.AddRange(_bins.Mark(from, to, BinQuality.Degraded));
    }

    private static readonly TimeSpan MarkEvery = TimeSpan.FromMinutes(5);

    private void Step()
    {
        WatchDevices();
        var before = _engine.WatermarkUtc;
        QueueStep step;
        var guard = 0;
        do
        {
            step = _engine.Advance(_reference);
            var update = _bins.Accept(step);
            _minutes.AddRange(update.Minutes);
            _binResults.AddRange(update.Bins);
            _health.AddRange(update.Health);
            _recomputations.AddRange(update.Recomputations);
            _lineMinutes.AddRange(_lines.Accept(step));
            var (bandMinutes, bandChanges) = _bands.Accept(step);
            _overflow.AddRange(bandMinutes);
            _overflowChanges.AddRange(bandChanges);
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
        var deviceOut = _liveness.OutDuring(minute);
        var window = _exits.Window(minute.AddMinutes(1), _settings.ExitWindowMinutes);
        // The desk term joins when the desks serving this queue have closed a minute recently (ARV-064).
        var desks = _desks is { } d && d.AsOfMinuteUtc >= minute.AddMinutes(-_settings.DeskTermFreshMinutes) ? d : null;
        var (published, shadow) = ShadowNowcasts.Inputs(new NowcastInput
        {
            QueueLength = length.Count,
            ExitsInWindow = window.Complete ? window.Exits : null,
            ExitWindowMinutes = _settings.ExitWindowMinutes,
            Degraded = length.Degraded || deviceOut || window.DegradedExits > 0
        }, desks);
        var nowcast = Nowcast.Compute(published, _settings.Nowcast);
        // ARV-117: the shadow nowcast without AMAN inputs, beside the published one, for the validation comparison only.
        _live.Add(new QueueLiveMinute(ZoneKey, minute, length.Count, length.FromSensors, length.Degraded || deviceOut, nowcast.Minutes, nowcast.Throughput,
            nowcast.NoService, nowcast.Degraded) { Shadow = ShadowNowcast.From(Nowcast.Compute(shadow, _settings.Nowcast)) });
        _liveness.Published(minute);
    }

    /// <summary>
    /// Ends a replay (ARV-036): steps to <paramref name="endUtc"/> as usual, reports the device outages still open there
    /// (marked through the end, <see cref="DeviceOutage.Closed"/> false), then stops watching devices and settles the
    /// engine to <paramref name="settleUtc"/> so the bins before the end become final without inventing outages after it
    /// or unknown overflow bands (ARV-115).
    /// </summary>
    public void Finish(DateTime endUtc, DateTime settleUtc)
    {
        Tick(endUtc);
        foreach (var (open, markFrom) in _liveness.EndAll(ZoneKey, endUtc))
            Ended(open, markFrom);

        _watchDevices = false;
        // The range's end is not a silent band either (ARV-115): no band becomes Unknown while the zone settles.
        _bands.StopWatchingSilence();
        if (settleUtc > endUtc)
            Tick(settleUtc);
    }

    /// <summary>The outputs since the last drain, which the host persists with the zone's state in one transaction.</summary>
    public ZoneOutputs Drain()
    {
        var outputs = Peek();
        _overflow.Clear();
        _overflowChanges.Clear();
        _deskReadings.Clear();
        _lineMinutes.Clear();
        _health.Clear();
        _minutes.Clear();
        _binResults.Clear();
        _live.Clear();
        _recomputations.Clear();
        _outages.Clear();
        return outputs;
    }

    /// <summary>
    /// The outputs since the last acknowledgement, without removing them: the host writes them with the zone's state and
    /// calls <see cref="Acknowledge"/> only after its transaction committed, so a failed or cancelled write loses nothing.
    /// </summary>
    public ZoneOutputs Peek() =>
        new(ZoneKey, [.. _minutes], [.. _binResults], [.. _live], [.. _recomputations], [.. _outages], [.. _lineMinutes], [.. _health], [.. _overflow], [.. _overflowChanges],
            [.. _deskReadings]);

    /// <summary>Removes the outputs a <see cref="Peek"/> returned (the ones before any produced since).</summary>
    public void Acknowledge(ZoneOutputs written)
    {
        ArgumentNullException.ThrowIfNull(written);
        _minutes.RemoveRange(0, Math.Min(written.Minutes.Count, _minutes.Count));
        _binResults.RemoveRange(0, Math.Min(written.Bins.Count, _binResults.Count));
        _live.RemoveRange(0, Math.Min(written.Live.Count, _live.Count));
        _recomputations.RemoveRange(0, Math.Min(written.Recomputations.Count, _recomputations.Count));
        _outages.RemoveRange(0, Math.Min(written.Outages.Count, _outages.Count));
        _lineMinutes.RemoveRange(0, Math.Min(written.Lines.Count, _lineMinutes.Count));
        _health.RemoveRange(0, Math.Min(written.Health.Count, _health.Count));
        _overflow.RemoveRange(0, Math.Min(written.Overflow.Count, _overflow.Count));
        _overflowChanges.RemoveRange(0, Math.Min(written.OverflowChanges.Count, _overflowChanges.Count));
        _deskReadings.RemoveRange(0, Math.Min(written.DeskReadings.Count, _deskReadings.Count));
    }

    /// <summary>
    /// The zone's state. Pending outputs are not part of it: they belong with the state in the same write (see
    /// <see cref="Peek"/>), since the state already reflects them.
    /// </summary>
    public ZoneProcessorState Capture()
    {
        var (devices, recent) = _liveness.Capture();
        return new ZoneProcessorState
        {
            Devices = devices,
            RecentOutages = recent,
            ZoneKey = ZoneKey,
            ProfileVersion = ProfileVersion,
            Engine = _engine.Capture(),
            Bins = _bins.Capture(),
            Exits = _exits.Capture(),
            ReferenceUtc = _reference,
            LastLiveMinuteUtc = _lastLive,
            Counters = Counters,
            Lines = _lines.Capture(),
            LinesReleasedEarlyThroughUtc = _lines.ReleasedEarlyThroughUtc,
            OverflowOpen = _bands.CaptureOpen(),
            OverflowBands = _bands.CaptureBands(),
            DeskZones = _deskZones.Capture()
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
        zone._bins = BinAccumulator.Restore(zone._settings.Bins, state.Bins, geometry);
        zone._exits = ExitRate.Restore(state.Exits);
        zone._reference = DateTime.SpecifyKind(state.ReferenceUtc, DateTimeKind.Utc);
        zone._lastLive = state.LastLiveMinuteUtc is { } live ? DateTime.SpecifyKind(live, DateTimeKind.Utc) : null;
        if (state.Counters is { } c)
            (zone._batches, zone._uncommissioned, zone._wrongZone, zone._invalid, zone._otherZones) = (c.Batches, c.Uncommissioned, c.WrongZone, c.Invalid,
                Math.Max(0, c.OtherZones));
        zone._liveness.Restore(state.Devices, state.RecentOutages);
        zone._lines = LineCounts.Restore(geometry, state.Lines, state.LinesReleasedEarlyThroughUtc);
        zone._bands = OverflowBands.Restore(geometry, state.OverflowOpen, state.OverflowBands, zone._settings.Engine.OccupancyFreshFor);
        zone._deskZones = DeskZoneReadings.Restore(geometry.DeskZones, state.DeskZones, notAfterUtc);
        return zone;
    }
}
