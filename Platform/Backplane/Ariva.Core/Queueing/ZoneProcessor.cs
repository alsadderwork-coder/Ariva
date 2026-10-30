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
    /// Sensing batches a zone remembers at most to apply a resent batch once (ARV-168, <see cref="RecentBatches"/>): a
    /// batch is remembered until the watermark is a lateness past its latest event, about three minutes with the
    /// defaults, so a device pushing all four kinds every second holds about 720.
    /// </summary>
    public int MaxRememberedBatches { get; init; } = 20_000;

    /// <summary>Of those, one device's at most: a device that floods the zone with batches forgets only its own.</summary>
    public int MaxRememberedBatchesPerDevice { get; init; } = 1_000;

    /// <summary>
    /// A desk term (ARV-064) is used for a live minute while its own minute is at most this many minutes older; desk
    /// minutes close about a minute after the queue's (the desk feed's lateness), so 5 minutes leaves room for a slow read.
    /// </summary>
    public int DeskTermFreshMinutes { get; init; } = 5;

    /// <summary>
    /// The sensor cycle time of the shadow nowcast (ARV-117b, Proposed values). Not part of the replay's settings hash: a
    /// replay has no desk term, so these settings change none of its outputs (the shadow is never in the ledger).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public SensorCycleSettings SensorCycle { get; init; } = new();

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
        // ARV-148: a device pushing less often than the silence limit would be out between its pushes.
        if (DeviceSilenceSeconds is < 30 or > 3_600 || (Engine is not null && Engine.PushInterval >= TimeSpan.FromSeconds(DeviceSilenceSeconds)))
            yield return "DeviceSilenceSeconds is from 30 to 3,600 and longer than the engine's push interval.";
        if (DeviceForgetHours is < 1 or > 72 || DeviceForgetHours * 3_600 <= DeviceSilenceSeconds)
            yield return "DeviceForgetHours is from 1 to 72 and longer than the silence limit.";
        if (MaxDevices is < 1 or > 4_096)
            yield return "MaxDevices is from 1 to 4,096.";
        if (MaxRememberedBatches is < 1 or > MaxRememberedBatchesLimit)
            yield return "MaxRememberedBatches is from 1 to 200,000.";
        if (MaxRememberedBatchesPerDevice < 1 || MaxRememberedBatchesPerDevice > MaxRememberedBatches)
            yield return "MaxRememberedBatchesPerDevice is from 1 to MaxRememberedBatches.";
        if (DeskTermFreshMinutes is < 1 or > 30)
            yield return "DeskTermFreshMinutes is from 1 to 30.";
        foreach (var p in SensorCycle?.Problems() ?? ["SensorCycle settings are required."])
            yield return p;
    }

    /// <summary>The largest <see cref="MaxRememberedBatches"/> (and so the most batches a snapshot may hold).</summary>
    public const int MaxRememberedBatchesLimit = 200_000;
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

    /// <summary>
    /// An entry or exit line of the zone had no live covering device during the minute (ARV-259e1, formulas F11: Unknown, and so
    /// <see cref="LengthDegraded"/> and a degraded nowcast too). Left out of the JSON when false, so a minute without it reads
    /// and hashes as before (the replay ledger, the live snapshot).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool Unknown { get; init; }
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

    /// <summary>
    /// What the queue engine refused or applied early since the last acknowledgement, by kind (ARV-231: the stream's refusal
    /// metric and warnings). Not rows to persist: outside <see cref="Count"/> and the replay ledger.
    /// </summary>
    public IReadOnlyList<QueueRefusalCount> Refusals { get; init; } = [];

    public int Count => Minutes.Count + Bins.Count + Live.Count + Recomputations.Count + Outages.Count + Lines.Count + Health.Count + Overflow.Count +
        OverflowChanges.Count + DeskReadings.Count;
}

/// <summary>A count of one kind of <see cref="QueueRejections"/> (its property name).</summary>
public sealed record QueueRefusalCount(string Kind, long Count);

/// <summary>
/// Counts of what a zone refused, for health. <see cref="OtherZones"/> (ARV-116): occupancy readings of a zone that is
/// neither the queue's nor a staff or service zone linked to a desk, kept from the queue engine.
/// <see cref="RepeatedBatches"/> (ARV-168): batches dropped because the zone applied them within their window (a resend);
/// <see cref="ForgottenBatches"/>: batches the zone forgot before their window ended because a bound was reached, so a
/// resend of one of them would have been applied again.
/// </summary>
public sealed record ZoneProcessorCounters(long Batches, long Uncommissioned, long WrongZone, long Invalid, long OtherZones = 0, long RepeatedBatches = 0,
    long ForgottenBatches = 0);

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
    /// (ARV-116); an earlier snapshot restores with none, so each desk zone's next reading passes. 8 adds the queue
    /// engine's pending empty-queue check (<see cref="QueueEngineState.PendingAnchorUtc"/>, ARV-114d); an earlier snapshot
    /// carries none (the engine that wrote it ran the check when a step filled) and is refused if it says otherwise. 9 adds
    /// the queue engine's event clock (<see cref="QueueEngineState.ClockUtc"/>, ARV-148); a snapshot without one restores
    /// with the clock at the latest of its watermark and its held events, and the clock then catches up with the events.
    /// 10 adds the batches the zone remembers to apply a resent batch once (<see cref="RecentBatches"/>, ARV-168); an
    /// earlier snapshot carries none (its zone did not remember batches) and is refused if it says otherwise, and restores
    /// remembering none, so a batch applied before the upgrade and resent just after it is applied again. 11 adds the queue
    /// engine's runs of position samples (<see cref="QueueEngineState.Runs"/>, ARV-230); an earlier snapshot carries none and is
    /// refused if it says otherwise, and restores without runs: its held samples are all in its buffer, and runs start anew.
    /// 12 adds the buffer's shares per device (the device of each anonymous buffered event, a tracked one's named by its key, and
    /// the step's DeviceShareFull count) and the minutes a full buffer may have shortened, the engine's and the zone's (ARV-231);
    /// an earlier snapshot carries none and is refused if it says otherwise, and restores with its tracked events counted for their
    /// devices, its anonymous ones for none, and no minute marked. 13 adds runs of position samples ahead of the clock (a run's
    /// anchor after the engine's clock, held in the buffer as a sample ahead of it or, in a quiet zone whose watermark followed
    /// the receive clock, already applied; and its pending sample marked ahead, <see cref="QueueRunState.PendingAhead"/>,
    /// ARV-239); an earlier snapshot carries neither and is refused if it does, and restores as before, its samples ahead of the
    /// clock all in its buffer. 14 adds the entry and exit lines without a live covering device and the spells that ended but
    /// whose live minutes are not all published (<see cref="Coverage"/>, <see cref="RecentUncovered"/>, ARV-259e1); an earlier
    /// snapshot carries neither, nor an Unknown bin mark, and is refused if it does, and restores with its lines without coverage
    /// derived from its devices (from where the latest outage started), their bins marked Unknown from there at its next step.
    /// </summary>
    public const int CurrentVersion = 14;

    /// <summary>The first version that may carry the queue engine's pending empty-queue check (ARV-114d).</summary>
    public const int PendingAnchorSinceVersion = 8;

    /// <summary>The first version that may carry remembered batches (ARV-168).</summary>
    public const int RecentBatchesSinceVersion = 10;

    /// <summary>The first version that may carry the queue engine's runs of position samples (ARV-230).</summary>
    public const int SightingRunsSinceVersion = 11;

    /// <summary>The first version that may carry the buffer's devices and full-buffer minutes (ARV-231).</summary>
    public const int BufferSharesSinceVersion = 12;

    /// <summary>The first version that may carry runs of position samples ahead of the clock (ARV-239).</summary>
    public const int AheadRunsSinceVersion = 13;

    /// <summary>The first version that may carry lines without a live covering device and Unknown bin marks (ARV-259e1).</summary>
    public const int LineCoverageSinceVersion = 14;

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

    /// <summary>The batches the zone applied within their window, by device and batch id (ARV-168), in the order they would be forgotten.</summary>
    public IReadOnlyList<RecentBatchState> RecentBatches { get; init; } = [];

    /// <summary>The minutes, not yet published live, that a full buffer may have shortened (ARV-231, version 12), in time order.</summary>
    public IReadOnlyList<DateTime> FullBufferMinutes { get; init; } = [];

    /// <summary>The entry and exit lines without a live covering device now, with where it started (ARV-259e1, version 14).</summary>
    public IReadOnlyList<LineCoverageState> Coverage { get; init; } = [];

    /// <summary>Spells without coverage that ended but whose live minutes are not all published (ARV-259e1, version 14).</summary>
    public IReadOnlyList<RecentUncoveredState> RecentUncovered { get; init; } = [];
}

/// <summary>
/// One queue zone's stream processing (ARV-034): sensing batches mapped to the queue engine's inputs, the engine stepped
/// on event time (ARV-148: its watermark is the zone's latest event time less the lateness, each event counted at most
/// up to Ariva's receive time of its own batch, and the zone's clock never more than the push interval behind the latest
/// receive time), its steps attributed to bins (F6, F7), and a live row with the queue length and the nowcast (F8) at
/// the end of each minute. Device liveness runs on the receive time (ARV-036): it is observed once for each new receive time, before
/// any record of it is applied, and a bin becomes final only once every outage minute in it is known (the receive
/// time less the push interval), so the order of the records of one receive time never changes the bins. Replaying
/// the same records gives the same results whatever batches and topics carried the events. Every batch is checked
/// again here (CWE-501: Kafka is a trust boundary): the zone must be this zone, the device commissioned, the counts and
/// names within the canonical bounds. A batch the zone applied within its window, by the same device with the same
/// batch id (a resend), is dropped: its push still says the device was heard and moves the receive clock, but none of
/// its events reach the engines again (ARV-168, <see cref="RecentBatches"/>). Pure: no I/O and no clock of its own.
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
    private long _batches, _uncommissioned, _wrongZone, _invalid, _otherZones, _repeated, _forgotten;
    // ARV-231: the engine's refusals since the last acknowledgement, by kind, and the minutes not yet published live that a full
    // buffer may have shortened (at most two days of them).
    private readonly Dictionary<string, long> _refusals = new(StringComparer.Ordinal);
    private readonly SortedSet<DateTime> _fullMinutes = [];
    private const int MaxFullMinutes = 2 * 24 * 60;
    private RecentBatches _recent;
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
    private LineCoverage _coverage;
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
        _coverage = new LineCoverage(CoveredLines(geometry));
        _bands = new OverflowBands(geometry, _settings.Engine.OccupancyFreshFor);
        _deskZones = new DeskZoneReadings(geometry.DeskZones);
        _recent = new RecentBatches(_settings.MaxRememberedBatches, _settings.MaxRememberedBatchesPerDevice);
    }

    private static DeviceLiveness NewLiveness(ZoneProcessorSettings settings) =>
        new(TimeSpan.FromSeconds(settings.DeviceSilenceSeconds), TimeSpan.FromHours(settings.DeviceForgetHours), settings.MaxDevices, settings.Engine.PushInterval);

    /// <summary>The lines whose coverage F11 judges: the queue's entry and exit lines (ARV-259e1).</summary>
    private static IEnumerable<string> CoveredLines(QueueZoneGeometry geometry) =>
        (geometry.EntryLines ?? Enumerable.Empty<string>()).Concat(geometry.ExitLines ?? Enumerable.Empty<string>());

    public string ZoneKey { get; }

    public int ProfileVersion { get; }

    /// <summary>The reference clock: the latest receive time seen.</summary>
    public DateTime ReferenceUtc => _reference;

    public DateTime WatermarkUtc => _engine.WatermarkUtc;

    public ZoneProcessorCounters Counters => new(_batches, _uncommissioned, _wrongZone, _invalid, _otherZones, _repeated, _forgotten);

    /// <summary>The batches the zone remembers now (ARV-168).</summary>
    public int RememberedBatches => _recent.Count;

    /// <summary>
    /// Whether an entry or exit line of the zone has no live covering device now (ARV-259e1, formulas F11): the zone is Unknown
    /// until a covering device is heard again.
    /// </summary>
    public bool UnknownNow => _coverage.AnyUncovered;

    /// <summary>
    /// Whether the zone counts in the Stream's gauge of zones Unknown (ARV-259e3): Unknown now while it still tracks a device. A
    /// zone whose devices have all been silent for the forget limit (24 hours) tracks none: it is taken as out of use (retired from
    /// the profile, its sensors moved or removed) and no longer pages, though it stays Unknown (<see cref="UnknownNow"/>).
    /// </summary>
    public bool UnknownInUse => _coverage.AnyUncovered && _liveness.Count > 0;

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
    /// Offers one batch and steps the zone: its receive time moves the zone's receive clock (never backwards;
    /// <paramref name="referenceCapUtc"/> bounds a receive time that lies ahead of the host's clock), and its events the
    /// zone's event clock (ARV-148). A receive time later than any before is observed for device liveness first.
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
        {
            _reference = reference;
            // ARV-148: the devices are observed once per receive time, before any record of it says who was heard, so the
            // order of one receive time's records never decides whether a silence was an outage.
            WatchDevices();
        }

        // Every batch of a commissioned device says it was heard; a health report may say it is offline (ARV-036).
        var changes = _liveness.Changes;
        var ended = _liveness.Heard(ZoneKey, batch.DeviceCode, _reference, batch is not DeviceStatusBatch { Online: false });
        if (ended is { } e)
            Ended(e.Outage, e.MarkFromUtc);
        // ARV-259e1: a device back, heard for the first time or reporting itself offline may change which lines a live device
        // covers. A line covered again is covered from where the device's outage ended, or from the minute a new device's push
        // covers (ARV-148).
        Covered(changes, _reference, ended?.Outage.ToUtc ?? _liveness.CoveredFrom(_reference));

        // ARV-168: a batch this device sent before with the same id, still within its window, is a resend (the device
        // dropped the request, or Kafka delivered the push twice): it was heard, but its events were applied already.
        // Keyed by the device and the id, never the id alone, so one device's ids never drop another's batches (CWE-501).
        var identified = batch is not DeviceStatusBatch && batch.Id != Guid.Empty;
        if (identified && _recent.Contains(batch.DeviceCode, batch.Id))
        {
            _repeated++;
            Step();
            return;
        }

        var unreliable = batch.Clock?.State == ClockState.Unreliable;
        // ARV-148: each event is offered with its own batch's receive time (capped as above), not the zone's latest one, so
        // an event moves the zone's clock at most to the time Ingest stamped on the push that carried it. That depends on
        // the records alone: a batch applied after the host ticked the zone on gives what a replay without ticks gives.
        // ARV-231: the devices the zone has heard recently share its buffer, the ones that have not pushed for a while included.
        _engine.ZoneDevices = _liveness.Count;
        foreach (var input in Inputs(batch, unreliable))
        {
            if (input is null)
                _invalid++;
            else
                _engine.Offer(input, reference, batch.DeviceCode);
        }

        // Remembered until the watermark is a lateness past its latest event (events beyond the engine's MaxAhead are
        // refused, so they never lengthen the window); Step forgets it once the watermark gets there.
        if (identified && LatestEvent(batch, Plus(reference, _settings.Engine.MaxAhead)) is { } latest)
            _forgotten += _recent.Remember(batch.DeviceCode, batch.Id, Plus(latest, _settings.Engine.Lateness));
        Step();
    }

    private static DateTime Plus(DateTime t, TimeSpan d) => t > DateTime.MaxValue - d ? DateTime.MaxValue : t + d;

    /// <summary>
    /// The latest plausible event time of a batch, at most <paramref name="notAfterUtc"/> (ARV-168): every kind, every path
    /// (the queue, the desk zones, zones that count for neither), the same events <see cref="Inputs"/> reads; null when it
    /// has none.
    /// </summary>
    private static DateTime? LatestEvent(SensingBatch batch, DateTime notAfterUtc)
    {
        static DateTime? Latest<T>(IReadOnlyList<Sensed<T>> events, DateTime notAfter) where T : CanonicalEvent
        {
            DateTime? latest = null;
            foreach (var e in (events ?? []).Take(MaxEventsPerBatch))
            {
                if (e is not null && Plausible(e.TimeUtc) && e.TimeUtc <= notAfter && (latest is null || e.TimeUtc > latest))
                    latest = e.TimeUtc;
            }

            return latest;
        }

        return batch switch
        {
            VendorLineCrossingBatch c => Latest(c.Crossings, notAfterUtc),
            ZoneOccupancyBatch o => Latest(o.Occupancy, notAfterUtc),
            IntervalCountBatch i => Latest(i.Intervals, notAfterUtc),
            TrackSampleBatch t => Latest(t.Samples, notAfterUtc),
            _ => null
        };
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
        {
            _reference = referenceUtc;
            WatchDevices();
        }

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
                // ARV-230 (CWE-400): a push's samples are offered in time order (stable: the samples of one instant keep the order
                // they came in, which is all the engine's order depends on), so the engine coalesces each track's samples into its
                // run whichever order the device lists them in; a push sent newest first would otherwise take a place a sample.
                foreach (var e in (t.Samples ?? []).Take(MaxEventsPerBatch).OrderBy(e => e?.TimeUtc ?? DateTime.MinValue))
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

    // ARV-259e1: after a change of the devices' live state, the lines that lost their last live covering device open a spell, and
    // the lines a device covers again close theirs at closeAtUtc, their bins marked Unknown (F11) through the spell's end.
    private void Covered(long changesBefore, DateTime openFallbackUtc, DateTime closeAtUtc)
    {
        if (_liveness.Changes == changesBefore)
            return;
        foreach (var (from, to) in LineCoverage.Union(_coverage.Recompute(_liveness, openFallbackUtc, closeAtUtc)))
            _recomputations.AddRange(_bins.Mark(from, to, BinQuality.Unknown));
    }

    // At each new receive time, before its records and before the engine steps: the open outages, and the spells of lines
    // without a live covering device (ARV-259e1), are marked through the minutes known to be out (KnownThrough), so a bin
    // becoming final at this receive time already carries them.
    private void WatchDevices()
    {
        if (!_watchDevices || _reference == DateTime.MinValue)
            return;
        var changes = _liveness.Changes;
        var forgotten = _liveness.Observe(ZoneKey, _reference);
        foreach (var (outage, markFrom) in forgotten)
            Ended(outage, markFrom);
        // A device gone out or forgotten may leave a line without a live covering device; a line left with no device tracked at all
        // has been without one since the latest forgotten device's outage started.
        Covered(changes, forgotten.Count > 0 ? forgotten.Max(f => f.Outage.FromUtc) : _reference, _reference);
        var known = KnownThrough();
        foreach (var (from, to) in _liveness.MarksDue(known))
            _recomputations.AddRange(_bins.Mark(from, to, BinQuality.Degraded));
        foreach (var (from, to) in LineCoverage.Union(_coverage.MarksDue(known)))
            _recomputations.AddRange(_bins.Mark(from, to, BinQuality.Unknown));
    }

    /// <summary>
    /// The minute up to which every device outage of the zone is known (ARV-148): the start of the minute the push interval
    /// before the receive clock falls in. A device heard again now ends its outage there at the earliest (F11: the first
    /// push after it covers that minute), so the minutes before it are out or not for good; a bin is final only up to it.
    /// </summary>
    private DateTime KnownThrough()
    {
        var push = _settings.Engine.PushInterval;
        var at = _reference > DateTime.MinValue + push ? _reference - push : DateTime.MinValue;
        return new DateTime(at.Ticks - at.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
    }

    private void Step()
    {
        var before = _engine.WatermarkUtc;
        // While the zone watches its devices, a bin is final only once every outage minute in it is known. With a lateness of
        // at least the push interval (the stream requires it) the watermark never passes a bin's end before that anyway;
        // the rule holds the zone to it under any settings.
        DateTime? finalThrough = _watchDevices ? KnownThrough() : null;
        QueueStep step;
        var guard = 0;
        do
        {
            step = _engine.Advance(_reference);
            Refused(step.Rejections);
            var update = _bins.Accept(step, finalThrough);
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
        // ARV-168: a batch whose window the watermark has reached is forgotten; every event of a repeat would be late.
        _recent.Expire(step.WatermarkUtc);
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
        // ARV-259e1 (F11): a minute in which an entry or exit line had no live covering device is Unknown, and so doubtful for
        // every reader of its length and nowcast.
        var unknown = _coverage.UncoveredDuring(minute);
        // ARV-231: a minute a full buffer may have shortened (events refused, or applied before their lateness was up).
        var full = _fullMinutes.Contains(minute);
        _fullMinutes.RemoveWhere(m => m <= minute);
        var window = _exits.Window(minute.AddMinutes(1), _settings.ExitWindowMinutes);
        // The desk term joins when the desks serving this queue have closed a minute recently (ARV-064).
        var desks = _desks is { } d && d.AsOfMinuteUtc >= minute.AddMinutes(-_settings.DeskTermFreshMinutes) ? d : null;
        // ARV-117b: the queue's exits over the minutes of the sensor-only busy window, for the shadow's cycle time only (a
        // window beyond the queue's own minutes is not complete, so it gives none).
        var cycleExits = desks?.SensorOnly?.SensorBusy is { } busy && busy.Minutes is >= 1 and <= 60 && busy.ToMinuteUtc < DateTime.MaxValue.AddMinutes(-1)
            ? _exits.Window(busy.ToMinuteUtc.AddMinutes(1), busy.Minutes)
            : null;
        var (published, shadow, cycle) = ShadowNowcasts.Inputs(new NowcastInput
        {
            QueueLength = length.Count,
            ExitsInWindow = window.Complete ? window.Exits : null,
            ExitWindowMinutes = _settings.ExitWindowMinutes,
            Degraded = length.Degraded || deviceOut || unknown || full || window.DegradedExits > 0
        }, desks, cycleExits, _settings.SensorCycle);
        var nowcast = Nowcast.Compute(published, _settings.Nowcast);
        // ARV-117: the shadow nowcast without AMAN inputs, beside the published one, for the validation comparison only.
        _live.Add(new QueueLiveMinute(ZoneKey, minute, length.Count, length.FromSensors, length.Degraded || deviceOut || unknown || full, nowcast.Minutes,
            nowcast.Throughput, nowcast.NoService, nowcast.Degraded)
        {
            Shadow = ShadowNowcast.From(Nowcast.Compute(shadow, _settings.Nowcast), cycle?.CycleMinutes),
            Unknown = unknown
        });
        _liveness.Published(minute);
        _coverage.Published(minute);
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
        // ARV-259e1: the spells without coverage still open end there too, their bins marked through the end; no Unknown is
        // invented while the zone settles.
        foreach (var (from, to) in LineCoverage.Union(_coverage.EndAll(endUtc)))
            _recomputations.AddRange(_bins.Mark(from, to, BinQuality.Unknown));

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
        _refusals.Clear();
        return outputs;
    }

    /// <summary>
    /// The outputs since the last acknowledgement, without removing them: the host writes them with the zone's state and
    /// calls <see cref="Acknowledge"/> only after its transaction committed, so a failed or cancelled write loses nothing.
    /// </summary>
    public ZoneOutputs Peek() =>
        new(ZoneKey, [.. _minutes], [.. _binResults], [.. _live], [.. _recomputations], [.. _outages], [.. _lineMinutes], [.. _health], [.. _overflow], [.. _overflowChanges],
            [.. _deskReadings])
        {
            Refusals = [.. _refusals.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => new QueueRefusalCount(r.Key, r.Value))]
        };

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
        foreach (var (kind, count) in written.Refusals)
        {
            var left = _refusals.GetValueOrDefault(kind) - count;
            if (left > 0)
                _refusals[kind] = left;
            else
                _refusals.Remove(kind);
        }
    }

    private void Refused(QueueRejections rejections)
    {
        foreach (var (kind, count) in rejections.Counts())
            _refusals[kind] = _refusals.GetValueOrDefault(kind) + count;
        foreach (var minute in rejections.FullBufferMinutes)
        {
            if (_fullMinutes.Count < MaxFullMinutes && (!_lastLive.HasValue || minute > _lastLive.Value))
                _fullMinutes.Add(minute);
        }
    }

    /// <summary>
    /// The zone's state. Pending outputs are not part of it: they belong with the state in the same write (see
    /// <see cref="Peek"/>), since the state already reflects them.
    /// </summary>
    public ZoneProcessorState Capture()
    {
        var (devices, recent) = _liveness.Capture();
        var (coverage, uncovered) = _coverage.Capture();
        return new ZoneProcessorState
        {
            Devices = devices,
            RecentOutages = recent,
            Coverage = coverage,
            RecentUncovered = uncovered,
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
            DeskZones = _deskZones.Capture(),
            RecentBatches = _recent.Capture(),
            FullBufferMinutes = [.. _fullMinutes]
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
            (zone._batches, zone._uncommissioned, zone._wrongZone, zone._invalid, zone._otherZones, zone._repeated, zone._forgotten) = (c.Batches, c.Uncommissioned,
                c.WrongZone, c.Invalid, Math.Max(0, c.OtherZones), Math.Max(0, c.RepeatedBatches), Math.Max(0, c.ForgottenBatches));
        zone._liveness.Restore(state.Devices, state.RecentOutages);
        // ARV-259e1: a snapshot's lines without coverage must agree with its devices (CWE-501); an earlier one's are derived from them.
        zone._coverage = LineCoverage.Restore(CoveredLines(geometry), state.Coverage, state.RecentUncovered, zone._liveness, zone._reference,
            state.Version < ZoneProcessorState.LineCoverageSinceVersion);
        zone._lines = LineCounts.Restore(geometry, state.Lines, state.LinesReleasedEarlyThroughUtc);
        zone._bands = OverflowBands.Restore(geometry, state.OverflowOpen, state.OverflowBands, zone._settings.Engine.OccupancyFreshFor);
        zone._deskZones = DeskZoneReadings.Restore(geometry.DeskZones, state.DeskZones, notAfterUtc);
        // ARV-168: each window ends after the watermark (the zone forgets a batch when the watermark reaches it) and at most
        // a lateness past the latest event the zone could have taken, its receive clock plus the engine's MaxAhead.
        var engine = zone._settings.Engine;
        zone._recent = RecentBatches.Restore(state.RecentBatches, zone._settings.MaxRememberedBatches, zone._settings.MaxRememberedBatchesPerDevice,
            zone._engine.WatermarkUtc, Plus(Plus(zone._reference, engine.MaxAhead), engine.Lateness));
        foreach (var minute in state.FullBufferMinutes ?? [])
            zone._fullMinutes.Add(DateTime.SpecifyKind(minute, DateTimeKind.Utc));
        return zone;
    }
}
