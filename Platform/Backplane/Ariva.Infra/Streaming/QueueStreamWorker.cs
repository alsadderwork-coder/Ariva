using System.Text.Json;
using Ariva.Core.Messaging;
using Ariva.Core.Queueing;
using Ariva.Core.Sensing;
using Ariva.Infra.Messaging;
using Ariva.Infra.Messaging.Kafka;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Npgsql;
using Microsoft.Extensions.Hosting;

namespace Ariva.Infra.Streaming;

/// <summary>Settings of the queue stream worker (section <c>Stream</c>).</summary>
public sealed class StreamSettings
{
    public const string SectionName = "Stream";

    /// <summary>Run the queue engine worker in this host (Ariva.Api.Stream).</summary>
    public bool Enabled { get; init; }

    /// <summary>Zones one instance holds at most (CWE-120); events of further zones are counted and skipped.</summary>
    public int MaxZones { get; init; } = 2_000;

    /// <summary>
    /// A zone without records for this many seconds of the host's clock, while the consumer is caught up, moves its
    /// clock on by the time elapsed so that its last minutes close; 0 turns it off (deterministic replays and tests).
    /// </summary>
    public int IdleTickSeconds { get; init; } = 15;

    /// <summary>A receive time further ahead of the host's clock than this is capped (a producer's clock running fast).</summary>
    public int MaxAheadSeconds { get; init; } = 60;

    /// <summary>Records held by an instance for merging its partitions in receive order; beyond it the earliest is applied.</summary>
    public int MaxMergeRecords { get; init; } = 20_000;

    /// <summary>Bytes of record values held by an instance for the merge; beyond it the earliest is applied (CWE-120).</summary>
    public int MaxMergeBytes { get; init; } = 64 * 1024 * 1024;

    /// <summary>
    /// How often the desk terms of the nowcast (ARV-064) are read from the desk minutes for the zones held; 0 turns them
    /// off (deterministic replays and tests: the nowcast then uses the exit rate only).
    /// </summary>
    public int DeskTermSeconds { get; init; } = 15;

    /// <summary>
    /// The allowed lateness of every site's queue zones, in seconds (ARV-148, formulas F5): a zone's watermark is its
    /// latest event time less this, and an event at or behind the watermark is late. At least the push interval, since a
    /// push carries events up to that old; 90 by default (a one-minute push interval and the 30 seconds allowed before for
    /// transport and device clocks). A site may set its own in <see cref="Sites"/>.
    /// </summary>
    public int LatenessSeconds { get; init; } = 90;

    /// <summary>
    /// The sensors' push interval, in seconds (ARV-148): a push covers this long before its receive time, so a device's
    /// outage ends at the start of the minute its first push after it covers, and a zone whose devices send no events moves
    /// on with Ariva's receive clock less this. 60 by default; 0 makes the zones run on the receive clock.
    /// </summary>
    public int PushIntervalSeconds { get; init; } = 60;

    /// <summary>
    /// How often the bin maturity sweep runs, in seconds (ARV-259b): one Stream replica a round counts the queue bins still
    /// provisional past their maturity. 60 by default, from 10 to 3,600.
    /// </summary>
    public int BinMaturitySweepSeconds { get; init; } = 60;

    /// <summary>
    /// How far back the bin maturity sweep reads, in hours (ARV-259b): bins that started earlier are not counted, which bounds
    /// its read to the newest chunks. 24 by default, from 1 to 168.
    /// </summary>
    public int BinMaturityLookbackHours { get; init; } = 24;

    /// <summary>
    /// The longest lateness any site runs under (ARV-259b): the default or a site's own, whichever is larger. The bin maturity
    /// sweep counts a bin from its end plus the censoring timeout plus this, so no site's bin is counted before it is mature.
    /// </summary>
    public int MaxLatenessSeconds => Math.Max(LatenessSeconds, (Sites ?? []).Values.Select(s => s?.LatenessSeconds ?? 0).DefaultIfEmpty(0).Max());

    /// <summary>A site's own lateness and push interval, by site code (either may be left out); at most 500 sites.</summary>
    public Dictionary<string, StreamSiteSettings> Sites { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Sites with settings of their own at most.</summary>
    public const int MaxSites = 500;

    /// <summary>The lateness and push interval of <paramref name="siteCode"/>: its own where set, the defaults otherwise.</summary>
    public (int LatenessSeconds, int PushIntervalSeconds) TimingOf(string siteCode)
    {
        StreamSiteSettings site = null;
        if (siteCode is not null && Sites is not null)
            Sites.TryGetValue(siteCode, out site);
        return (site?.LatenessSeconds ?? LatenessSeconds, site?.PushIntervalSeconds ?? PushIntervalSeconds);
    }

    /// <summary>
    /// The zone settings for a site's queue zones: <paramref name="baseline"/> with the site's lateness and push interval
    /// (ARV-148), so the live stream and a replay of the site run under the same settings.
    /// </summary>
    public ZoneProcessorSettings ZoneSettingsFor(string siteCode, ZoneProcessorSettings baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        var (lateness, push) = TimingOf(siteCode);
        return baseline with
        {
            Engine = (baseline.Engine ?? new QueueEngineSettings()) with { Lateness = TimeSpan.FromSeconds(lateness), PushInterval = TimeSpan.FromSeconds(push) }
        };
    }

    /// <summary>
    /// The problems of the zone settings each site runs under (ARV-148): <paramref name="baseline"/> with the default timing
    /// and with every valid site's own, checked once at start so that no site's settings refuse a zone in the middle of
    /// the stream (a push interval at or above the device silence limit, for one). Site keys that are not site codes are
    /// skipped here (<see cref="Problems"/> reports them).
    /// </summary>
    public IEnumerable<string> ZoneSettingsProblems(ZoneProcessorSettings baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        foreach (var problem in ZoneSettingsFor(null, baseline).Problems())
            yield return $"Stream: {problem}";
        if (Sites is null || Sites.Count > MaxSites)
            yield break;
        foreach (var code in Sites.Keys)
        {
            if (!Ariva.Core.Domain.Entities.Site.IsValidCode(code))
                continue;
            foreach (var problem in ZoneSettingsFor(code, baseline).Problems())
                yield return $"Stream:Sites:{code}: {problem}";
        }
    }

    public IEnumerable<string> Problems()
    {
        if (MaxZones is < 1 or > 100_000)
            yield return "Stream:MaxZones is from 1 to 100,000.";
        if (IdleTickSeconds is < 0 or > 3_600)
            yield return "Stream:IdleTickSeconds is from 0 (off) to 3,600.";
        if (MaxAheadSeconds is < 0 or > 3_600)
            yield return "Stream:MaxAheadSeconds is from 0 to 3,600.";
        if (MaxMergeRecords is < 100 or > 1_000_000)
            yield return "Stream:MaxMergeRecords is from 100 to 1,000,000.";
        if (MaxMergeBytes is < 1024 * 1024 or > 1024 * 1024 * 1024)
            yield return "Stream:MaxMergeBytes is from 1 MB to 1 GB.";
        if (DeskTermSeconds is < 0 or > 600)
            yield return "Stream:DeskTermSeconds is from 0 (off) to 600.";
        if (BinMaturitySweepSeconds is < 10 or > 3_600)
            yield return "Stream:BinMaturitySweepSeconds is from 10 to 3,600.";
        if (BinMaturityLookbackHours is < 1 or > 168)
            yield return "Stream:BinMaturityLookbackHours is from 1 to 168.";
        foreach (var problem in TimingProblems("Stream", LatenessSeconds, PushIntervalSeconds))
            yield return problem;
        if ((Sites?.Count ?? 0) > MaxSites)
        {
            yield return $"Stream:Sites names at most {MaxSites} sites.";
            yield break;
        }

        foreach (var (code, site) in Sites ?? [])
        {
            // A site code as the topology allows it (CWE-501: configuration is still input to the engine's settings).
            if (!Ariva.Core.Domain.Entities.Site.IsValidCode(code))
            {
                yield return "Stream:Sites has a key that is not a site code (2 to 8 upper case letters or digits, an optional hyphen and 1 to 8 more).";
                continue;
            }

            var (lateness, push) = TimingOf(code);
            foreach (var problem in TimingProblems($"Stream:Sites:{code}", lateness, push))
                yield return problem;
        }
    }

    private static IEnumerable<string> TimingProblems(string section, int lateness, int push)
    {
        var latenessValid = lateness is >= 0 and <= 1_800;
        var pushValid = push is >= 0 and <= 600;
        if (!latenessValid)
            yield return $"{section}:LatenessSeconds is from 0 to 1,800.";
        if (!pushValid)
            yield return $"{section}:PushIntervalSeconds is from 0 to 600.";
        if (latenessValid && pushValid && lateness < push)
            yield return $"{section}:LatenessSeconds is at least its PushIntervalSeconds ({push}): a push carries events up to that old.";
    }
}

/// <summary>A site's own stream timing (<c>Stream:Sites:&lt;code&gt;</c>, ARV-148); a value left out takes the default.</summary>
public sealed class StreamSiteSettings
{
    /// <summary>The site's allowed lateness, in seconds (at least its push interval).</summary>
    public int? LatenessSeconds { get; init; }

    /// <summary>The push interval of the site's sensors, in seconds.</summary>
    public int? PushIntervalSeconds { get; init; }
}

/// <summary>
/// The queue engine worker of Ariva.Api.Stream (ARV-034, ADR-0018). One raw consumer reads the four sensing topics of a
/// zone (crossings, occupancy, intervals, track samples), all keyed by the zone key and co-partitioned (the range
/// assignor gives one instance the same partition of every topic, so a zone's events meet in one engine). Each zone is
/// a <see cref="ZoneProcessor"/>, loaded from its last snapshot the first time one of its records arrives.
/// <para>
/// The assigned partitions of the four topics are merged in the order of Ariva's receive time (then topic, partition and
/// offset): a record is applied only when every other assigned topic partition holds a later record or has nothing more
/// to read. A replay
/// after a restart, which fetches the topics at different rates, therefore applies the records in the same order as the
/// live run did, and the zones step identically.
/// </para>
/// <para>
/// Checkpoints (every <c>Kafka:Consumers:CheckpointSeconds</c>, when a zone's outputs reach their bound, and before a
/// clean revoke) write every zone's outputs, its snapshot and the partitions' next offsets in one transaction
/// (<see cref="StreamStore"/>); only then are the Kafka offsets stored and committed. On assignment the consumer starts
/// from the offsets saved with the snapshots, so no record is applied twice to a saved state, and the records after them
/// are replayed into the same rows. A lost partition drops its zones without saving. A record that is not a readable
/// batch of its topic, or whose key is not its zone, goes to the dead-letter topic (CWE-501). A failure stops the worker
/// without committing; the host's restart replays from the last checkpoint. Since ARV-258 the seconds since the worker last
/// wrote everything it owed (a checkpoint, or nothing read since the assignment) are the gauge
/// <see cref="StreamMetrics.CheckpointAgeName"/>, so checkpoints that keep failing while the database answers are seen.
/// </para>
/// </summary>
public sealed class QueueStreamWorker(
    KafkaSettings kafka,
    StreamSettings settings,
    StreamStore store,
    ZoneGeometrySource geometries,
    IDeadLetterSink deadLetters,
    TimeProvider timeProvider,
    ILogger<QueueStreamWorker> logger,
    Ariva.Infra.Live.ILiveSnapshotStore live = null,
    ZoneProcessorSettings zoneSettings = null,
    DeskTermSource deskTerms = null,
    StreamMetrics metrics = null,
    KafkaProducerMetrics producerMetrics = null) : BackgroundService
{
    public const string Purpose = "queue-engine";

    /// <summary>The name the sensing topics' partition counts are recorded under.</summary>
    public const string TopicSet = "sensing";

    /// <summary>
    /// The four sensing topics and the device health topic (ARV-036), all keyed by the zone key and co-partitioned; in
    /// this order when records share a receive time.
    /// </summary>
    public static readonly IReadOnlyList<string> Topics =
        [KafkaTopics.DeviceVendorLineCrossing, KafkaTopics.DeviceZoneOccupancy, KafkaTopics.DeviceIntervalCount, KafkaTopics.DeviceTrackSample, KafkaTopics.DeviceHealth];

    private static readonly Dictionary<string, Type> TypeOf = new(StringComparer.Ordinal)
    {
        [KafkaTopics.DeviceVendorLineCrossing] = typeof(VendorLineCrossingBatch),
        [KafkaTopics.DeviceZoneOccupancy] = typeof(ZoneOccupancyBatch),
        [KafkaTopics.DeviceIntervalCount] = typeof(IntervalCountBatch),
        [KafkaTopics.DeviceTrackSample] = typeof(TrackSampleBatch),
        [KafkaTopics.DeviceHealth] = typeof(DeviceHealthReported)
    };

    /// <summary>A record as the zone processor takes it: a sensing batch, or a device health report as a status batch.</summary>
    public static SensingBatch Read(string topic, byte[] value)
    {
        var read = JsonSerializer.Deserialize(value, TypeOf[topic], EventCatalog.Json);
        return read is DeviceHealthReported health ? DeviceStatusBatch.From(health) : (SensingBatch)read;
    }

    private readonly ZoneProcessorSettings _zoneSettings = zoneSettings ?? new ZoneProcessorSettings();
    private readonly RefusalWarnings _refusalWarnings = new(timeProvider);
    private readonly Dictionary<string, (ZoneProcessor Zone, int Partition, DateTime LastRecordWall)> _zones = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Topic, int Partition), long> _positions = [];
    private readonly Dictionary<(string Topic, int Partition), long> _consumed = [];
    private readonly HashSet<(string Topic, int Partition)> _committedAsked = [];
    private readonly Dictionary<(string Topic, int Partition), Queue<Held>> _merge = [];
    private readonly Dictionary<(string Topic, int Partition), (long Low, long High, DateTime At)> _watermarks = [];

    /// <summary>A record waiting for the merge: its batch (null for one already dead-lettered) and where it came from.</summary>
    private sealed record Held(string Topic, int Partition, long Offset, string Key, DateTime ReceivedUtc, SensingBatch Batch, int Bytes);

    private long _heldBytes;
    private int _heldRecords;
    private long _forcedReleases, _failedBatches, _wrongPartition;
    private readonly HashSet<int> _assigned = [];

    // ARV-274: the assigned partitions whose saved zones are still to be listed, the zones listed and waiting to be restored without a
    // record of them, the topics' partition count read last (the partition a zone's records go to now), and how the restores went.
    private readonly HashSet<int> _toList = [];
    private readonly Queue<(string ZoneKey, int Partition, int ProfileVersion)> _toRestore = new();
    private int _currentPartitionCount;
    private readonly RestoreRetry _restoreRetry = new(timeProvider, RestoreRetryAfter, RestoreAttempts);
    private readonly int[] _restoreTally = new int[Enum.GetValues<RestoreOutcome>().Length];

    /// <summary>The zones restored from their saved state in one pass of the loop at most (ARV-274), so a rebalance's restores never hold up reading.</summary>
    internal const int RestoresPerPass = 50;

    /// <summary>The time one pass of the loop gives the restores (ARV-274): no zone is started after it, so reading is not held up.</summary>
    internal static readonly TimeSpan RestorePassBudget = TimeSpan.FromSeconds(5);

    /// <summary>How long the restores wait after the database did not answer (ARV-274); <see cref="RestoreAttempts"/> failures in a row give up.</summary>
    internal static readonly TimeSpan RestoreRetryAfter = TimeSpan.FromSeconds(60);

    internal const int RestoreAttempts = 3;

    /// <summary>
    /// The saved zone states listed on an assignment at most (ARV-274, CWE-400); beyond, the first in key order, the others restored with
    /// their first record. The listing spans every replica's zones and keeps those retired (the runtime login cannot delete a state), so
    /// a deployment can pass it; the log then warns. Keys longer than a message key are not read (they are never zone keys).
    /// </summary>
    internal const int MaxListedZones = 100_000;
    private readonly Dictionary<string, (ZoneGeometry Geometry, DateTime Until)> _geometryCache = new(StringComparer.Ordinal);
    private long _skippedUnknownZone, _skippedFull, _deadLettered, _records, _legacyHealth;

    public string GroupId => PartitionedConsumer<object>.GroupId(kafka, Purpose);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // ARV-148: every site's zone settings are checked here, before any zone is created with them.
        var problems = settings.Problems().Concat(_zoneSettings.Problems()).Concat(settings.ZoneSettingsProblems(_zoneSettings)).ToList();
        if (problems.Count > 0)
            throw new InvalidOperationException(string.Join(" ", problems));
        return Task.Factory.StartNew(() => Loop(stoppingToken), stoppingToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    private async Task Loop(CancellationToken ct)
    {
        var config = KafkaClientConfig.Consumer(kafka, GroupId);
        // Co-partitioning: the range assignor gives a member the same partitions of every topic it subscribes to.
        config.PartitionAssignmentStrategy = PartitionAssignmentStrategy.Range;
        // Partitions added to the topics (a scale-out) are noticed within 30 seconds rather than librdkafka's 5 minutes.
        config.TopicMetadataRefreshIntervalMs = 30_000;
        var failed = false;
        using var consumer = new ConsumerBuilder<string, byte[]>(config)
            .SetPartitionsAssignedHandler((_, partitions) => Assigned(partitions, ct))
            .SetPartitionsRevokedHandler((c, offsets) =>
            {
                var list = offsets.Select(o => o.TopicPartition).ToList();
                if (!failed)
                {
                    try
                    {
                        Checkpoint(c, CancellationToken.None).GetAwaiter().GetResult();
                    }
                    catch (Exception e) when (e is NpgsqlException or InvalidOperationException or TimeoutException or IOException)
                    {
                        // Nothing committed: the next owner replays from the last saved positions.
                        logger.LogError(e, "Checkpoint before a revoke failed; the partitions are replayed from the last checkpoint");
                    }
                }

                Drop(list.Select(p => p.Partition.Value));
            })
            .SetPartitionsLostHandler((_, offsets) => Drop(offsets.Select(o => o.TopicPartition.Partition.Value)))
            .Build();

        await CheckCoPartitionedAsync(ct);
        consumer.Subscribe(Topics);
        logger.LogInformation("Queue stream worker {GroupId} subscribed to {Topics}", GroupId, string.Join(", ", Topics));
        // ARV-258: the checkpoint age counts from the start, so a worker that never manages a checkpoint shows it growing.
        metrics?.Checkpointed(GroupId);
        var every = TimeSpan.FromSeconds(Math.Max(1, kafka.Consumers.CheckpointSeconds));
        var paused = new List<TopicPartition>();
        var next = timeProvider.GetUtcNow() + every;
        var nextLag = timeProvider.GetUtcNow();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = consumer.Consume(TimeSpan.FromMilliseconds(250));
                var full = false;
                if (result?.Message is not null)
                {
                    await HoldAsync(result, ct);
                    full = await ReleaseAsync(consumer, ct);
                }
                else
                {
                    full = await ReleaseAsync(consumer, ct);
                    if (_heldRecords == 0)
                        TickIdleZones();
                }

                await RestoreAssignedAsync(ct);
                await RefreshDeskTermsAsync(ct);

                // ARV-125a: the lag is measured whether or not a checkpoint succeeds, so a worker held back by its database
                // shows a growing lag.
                if (metrics is not null && timeProvider.GetUtcNow() >= nextLag)
                {
                    nextLag = timeProvider.GetUtcNow() + every;
                    ReportLag(consumer, every);
                    // ARV-259e3: the zones this instance holds that are Unknown now and still in use, by site; every instance its own zones.
                    metrics.ZonesUnknown(_zones.Select(z => (Ariva.Infra.Live.LiveZones.SiteOf(z.Key), z.Value.Zone.UnknownInUse)), MeasurementFreshFor(every));
                }

                // ARV-274: zones held without a record read yet (restored on assignment) still move on with idle ticks, so their outputs are
                // written on the same interval.
                if (full || (timeProvider.GetUtcNow() >= next && (_positions.Count > 0 || _zones.Count > 0)))
                {
                    next = timeProvider.GetUtcNow() + every;
                    try
                    {
                        await Checkpoint(consumer, ct);
                        metrics?.Checkpointed(GroupId);
                        if (paused.Count > 0)
                        {
                            consumer.Resume(paused);
                            paused.Clear();
                        }
                    }
                    catch (Exception e) when (!ct.IsCancellationRequested && e is NpgsqlException or TimeoutException or IOException or InvalidOperationException)
                    {
                        // The database is away: the outputs stay with the zones and the next checkpoint writes them all.
                        // While a zone is at its bound, reading stops (the consumer keeps polling, so it stays in the group).
                        logger.LogError(e, "Queue stream checkpoint failed; retrying in {Seconds} seconds", every.TotalSeconds);
                        if (full && paused.Count == 0)
                        {
                            paused.AddRange(consumer.Assignment);
                            consumer.Pause(paused);
                        }
                    }
                }
                else if (_positions.Count == 0 && _zones.Count == 0 && timeProvider.GetUtcNow() >= next)
                {
                    // ARV-258: nothing read since the assignment, so nothing is owed: the checkpoint age stays fresh.
                    next = timeProvider.GetUtcNow() + every;
                    metrics?.Checkpointed(GroupId);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown: Close below revokes cleanly, which checkpoints and commits.
        }
        catch (Exception e)
        {
            failed = true;
            logger.LogError(e, "Queue stream worker failed; the records since the last checkpoint are replayed after restart");
            throw;
        }
        finally
        {
            metrics?.Clear();
            consumer.Close();
        }
    }

    /// <summary>
    /// ARV-125a: the records behind the end of each topic for the partitions this instance owns: the high watermark the
    /// consumer last fetched (else the one last asked of the broker) less the next offset to read (else the consumer's
    /// position). No broker call.
    /// </summary>
    private void ReportLag(IConsumer<string, byte[]> consumer, TimeSpan every)
    {
        var partitions = new List<(string Topic, long High, long Next)>();
        try
        {
            foreach (var tp in consumer.Assignment)
            {
                var high = consumer.GetWatermarkOffsets(tp).High;
                var known = high != Offset.Unset ? high.Value : _watermarks.TryGetValue((tp.Topic, tp.Partition.Value), out var asked) ? asked.High : -1;
                long next = -1;
                if (_consumed.TryGetValue((tp.Topic, tp.Partition.Value), out var consumed))
                    next = consumed;
                else if (consumer.Position(tp) is var position && position != Offset.Unset)
                    next = position.Value;
                partitions.Add((tp.Topic, known, next));
            }
        }
        catch (KafkaException e)
        {
            // A measurement never stops the worker (a rebalance in progress, for example): the last one expires instead.
            logger.LogDebug(e, "Queue stream lag not measured");
            return;
        }

        metrics.Report(GroupId, StreamMetrics.LagByTopic(partitions), MeasurementFreshFor(every));
    }

    // Reported for a minute (at least 12 intervals), longer than a checkpoint the database holds to its command timeout.
    private static TimeSpan MeasurementFreshFor(TimeSpan every) => TimeSpan.FromSeconds(Math.Max(60, every.TotalSeconds * 12));

    /// <summary>
    /// The four topics must have the same number of partitions, or a zone's records would land on different partition
    /// numbers and could be owned by two instances. Waits for the topics to exist (they are provisioned by Ariva.Api.Main).
    /// </summary>
    private async Task CheckCoPartitionedAsync(CancellationToken ct)
    {
        // Counts the topics had before (a scale-out) still decide where older records are.
        // Only plausible earlier counts (at most 64 of them, each below 10,000): the table is data, not trusted to be small.
        foreach (var count in (await store.LoadPartitionCountsAsync(TopicSet, ct)).Where(c => c is > 0 and <= 10_000).OrderDescending().Take(64))
            _partitionCounts.Add(count);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (RefreshPartitionCount())
                    return;
                logger.LogWarning("The sensing topics do not all exist yet; the queue stream worker waits for them");
            }
            catch (KafkaException e)
            {
                logger.LogWarning(e, "Kafka metadata not available yet; the queue stream worker retries");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), timeProvider, ct);
        }
    }

    /// <summary>
    /// Reads the sensing topics' partition counts: false while one is missing, an exception when they differ (a zone's
    /// records would land on different partition numbers). Partitions added later (a scale-out) are taken up here, on
    /// the rebalance they cause; every count seen stays valid for records produced before the change.
    /// </summary>
    private bool RefreshPartitionCount()
    {
        using var admin = new AdminClientBuilder(KafkaClientConfig.Admin(kafka)).Build();
        var metadata = admin.GetMetadata(TimeSpan.FromSeconds(30));
        var counts = Topics.Select(t => metadata.Topics.FirstOrDefault(m => m.Topic == t && m.Error.Code == ErrorCode.NoError)?.Partitions.Count ?? 0).ToList();
        if (counts.Any(c => c == 0))
            return false;
        if (counts.Distinct().Count() != 1)
            throw new InvalidOperationException(
                $"The sensing topics must have the same number of partitions for the queue stream worker; they have {string.Join(", ", counts)}.");
        store.SavePartitionCountAsync(TopicSet, counts[0], CancellationToken.None).GetAwaiter().GetResult();
        _currentPartitionCount = counts[0];
        if (_partitionCounts.Add(counts[0]) && _partitionCounts.Count > 1)
            logger.LogWarning("The sensing topics now have {Count} partitions; records hashed over the earlier counts are still accepted", counts[0]);
        return true;
    }

    // Start each partition where the saved state left it; a partition never saved starts from the committed offset.
    private List<TopicPartitionOffset> Assigned(List<TopicPartition> partitions, CancellationToken ct)
    {
        // Partitions may have been added (that is what caused this rebalance); a failure here keeps the counts known so far.
        var counted = false;
        try
        {
            counted = RefreshPartitionCount();
        }
        catch (KafkaException e)
        {
            logger.LogWarning(e, "Kafka metadata not available on assignment; the partition counts known so far stay in use");
        }

        foreach (var p in partitions)
            _assigned.Add(p.Partition.Value);
        var saved = store.LoadOffsetsAsync(GroupId, [.. partitions.Select(p => (p.Topic, p.Partition.Value))], ct).GetAwaiter().GetResult()
            .ToDictionary(o => (o.Topic, o.Partition), o => o.NextOffset);
        logger.LogInformation("Queue stream worker assigned {Partitions}", string.Join(", ", partitions.Select(p => $"{p.Topic}[{p.Partition.Value}]")));
        // Where reading starts is where "nothing more to read" is measured from until a record arrives.
        foreach (var ((topic, partition), offset) in saved)
            _consumed[(topic, partition)] = offset;
        // ARV-274: the saved zones of these partitions are listed on the next pass of the loop, not in this callback, and only under a
        // partition count read now: under an older count a zone could be held here while its records go to another instance.
        if (counted)
            _toList.UnionWith(partitions.Select(p => p.Partition.Value));
        else if (partitions.Count > 0)
            logger.LogWarning("The sensing topics' partition count was not read on assignment; the zones of the assigned partitions are restored when a record of them arrives");
        return [.. partitions.Select(p => saved.TryGetValue((p.Topic, p.Partition.Value), out var offset)
            ? new TopicPartitionOffset(p, new Offset(offset))
            : new TopicPartitionOffset(p, Offset.Unset))];
    }

    // ARV-274: a zone whose devices all stay silent sends no record, so it would never be held (idle-ticked, its spell without coverage
    // carried on, counted in zones_unknown) until one arrives. The assignment only notes its partitions (_toList); the next pass of the
    // loop lists the saved zone keys once, bounded, and queues those whose records go to a noted partition under the topics' partition
    // count now; each pass after restores a few, within RestoresPerPass and RestorePassBudget, so neither the rebalance nor reading is
    // held up by them. When the database does not answer, the restores wait RestoreRetryAfter and give up after RestoreAttempts: those
    // zones are restored with their first record, as before. One summary is logged when the queue is done.
    private async Task RestoreAssignedAsync(CancellationToken ct)
    {
        if ((_toList.Count == 0 && _toRestore.Count == 0) || !_restoreRetry.Due)
            return;
        var started = timeProvider.GetTimestamp();
        try
        {
            if (_toList.Count > 0)
                await ListRestoresAsync(ct);
            await DrainAsync(_toRestore, RestoresPerPass, () => timeProvider.GetElapsedTime(started) < RestorePassBudget, zone => RestoreAsync(zone, ct), _restoreTally);
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is NpgsqlException or TimeoutException or IOException)
        {
            RestoreFailed(e);
            return;
        }

        _restoreRetry.Succeeded();
        if (_toList.Count == 0 && _toRestore.Count == 0)
            ReportRestores();
    }

    /// <summary>
    /// Up to <paramref name="max"/> queued zones restored while <paramref name="withinBudget"/> holds, each outcome tallied (ARV-274).
    /// A zone is taken off the queue once its outcome is known, so one the database did not answer for (<paramref name="restore"/>
    /// throws) stays first for the next attempt; the first zone beyond Stream:MaxZones ends the queue, every zone left tallied Full.
    /// </summary>
    internal static async Task DrainAsync<T>(Queue<T> queue, int max, Func<bool> withinBudget, Func<T, Task<RestoreOutcome>> restore, int[] tally)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(withinBudget);
        ArgumentNullException.ThrowIfNull(restore);
        ArgumentNullException.ThrowIfNull(tally);
        for (var n = 0; n < max && queue.Count > 0 && withinBudget(); n++)
        {
            var outcome = await restore(queue.Peek());
            if (outcome == RestoreOutcome.Full)
            {
                tally[(int)RestoreOutcome.Full] += queue.Count;
                queue.Clear();
                return;
            }

            tally[(int)outcome]++;
            queue.Dequeue();
        }
    }

    // The saved zone keys, listed once for the partitions noted on assignment; a database failure leaves them noted for the next attempt.
    private async Task ListRestoresAsync(CancellationToken ct)
    {
        var partitions = _toList.ToHashSet();
        if (!CanList(partitions, _currentPartitionCount))
        {
            // The assignment names a partition the count read last does not have: which zones go to it now is not known here.
            logger.LogWarning("Partitions {Partitions} are not all below the sensing topics' partition count {Count}; their zones are restored when a record of them arrives",
                string.Join(", ", partitions.Order()), _currentPartitionCount);
            _toList.Clear();
            return;
        }

        var stored = await store.LoadZoneKeysAsync(MaxListedZones, ct);
        _toList.Clear();
        var listed = Listed(stored, MaxListedZones, out var cut);
        if (cut)
            logger.LogWarning("More than {Max} saved zone states; the first {Max} in key order are restored on assignment, the others when a record of them arrives",
                MaxListedZones, MaxListedZones);
        var queued = ZonesOf(listed, partitions, _currentPartitionCount, out var malformed);
        _restoreTally[(int)RestoreOutcome.Malformed] += malformed;
        foreach (var zone in queued.Where(z => _assigned.Contains(z.Partition)))
            _toRestore.Enqueue(zone);
    }

    /// <summary>
    /// Whether the saved zones of <paramref name="partitions"/> can be told apart under <paramref name="partitionCount"/>, the count read
    /// from the brokers on the assignment (ARV-274, CWE-501): not before a count is read, and not for a partition at or past it (the
    /// assignment then knows partitions the count does not, so which zones go to them now is not known here).
    /// </summary>
    internal static bool CanList(IReadOnlyCollection<int> partitions, int partitionCount) =>
        partitions is { Count: > 0 } && partitionCount >= 1 && partitions.All(p => p >= 0 && p < partitionCount);

    /// <summary>The first <paramref name="max"/> of a listing read with one row more than its maximum, and whether it was cut there.</summary>
    internal static IEnumerable<(string ZoneKey, int ProfileVersion)> Listed(IReadOnlyList<(string ZoneKey, int ProfileVersion)> stored, int max, out bool cut)
    {
        ArgumentNullException.ThrowIfNull(stored);
        cut = stored.Count > max;
        return cut ? stored.Take(max) : stored;
    }

    /// <summary>
    /// The saved zones whose key hashes to one of <paramref name="partitions"/> under the topics' partition count now (the partition
    /// their records go to, ARV-274), in the order given; a key that is not a site code, a slash and a queue zone name that fits a message
    /// key is left out and counted in <paramref name="malformed"/> (CWE-501: the keys are data from storage).
    /// </summary>
    internal static IReadOnlyList<(string ZoneKey, int Partition, int ProfileVersion)> ZonesOf(IEnumerable<(string ZoneKey, int ProfileVersion)> stored,
        IReadOnlySet<int> partitions, int partitionCount, out int malformed)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentOutOfRangeException.ThrowIfLessThan(partitionCount, 1);
        malformed = 0;
        var zones = new List<(string, int, int)>();
        foreach (var (key, version) in stored)
        {
            if (!TrySplitZoneKey(key, out _, out _))
            {
                malformed++;
                continue;
            }

            var partition = ExpectedPartition(key, partitionCount);
            if (partitions.Contains(partition))
                zones.Add((key, partition, version));
        }

        return zones;
    }

    /// <summary>A zone key's site code and queue zone name (<see cref="ZoneKeys.For"/>), if it is one: a site code, a slash and a clean name that fits a message key.</summary>
    internal static bool TrySplitZoneKey(string key, out string siteCode, out string queueZoneName)
    {
        siteCode = queueZoneName = null;
        var slash = key?.IndexOf('/', StringComparison.Ordinal) ?? -1;
        if (slash <= 0 || slash == key.Length - 1)
            return false;
        var site = key[..slash];
        var zone = key[(slash + 1)..];
        if (!Ariva.Core.Domain.Entities.Site.IsValidCode(site) || !ZoneKeys.Fits(site, zone) || !Ariva.Core.Domain.Components.DisplayText.IsClean(zone))
            return false;
        (siteCode, queueZoneName) = (site, zone);
        return true;
    }

    // One queued zone restored from its saved state as a first record of it would restore it (ZoneAsync): a zone already held, or of a
    // partition no longer assigned, is passed over; so is one no longer in its site's published profile, one saved under another
    // profile version (it starts afresh with its first record, as before), one whose state is gone, and any beyond Stream:MaxZones. A
    // snapshot that fails its checks (CWE-501) is not used: the zone starts afresh with its first record. A database failure throws.
    private async Task<RestoreOutcome> RestoreAsync((string ZoneKey, int Partition, int ProfileVersion) queued, CancellationToken ct)
    {
        var (key, partition, version) = queued;
        if (_zones.ContainsKey(key) || !_assigned.Contains(partition))
            return RestoreOutcome.Held;
        if (!TrySplitZoneKey(key, out var site, out var name))
            return RestoreOutcome.Malformed;
        if (_zones.Count >= settings.MaxZones)
            return RestoreOutcome.Full;
        var geometry = await GeometryAsync(site, name, ct);
        var choice = ChooseRestore(geometry, version);
        if (choice != RestoreOutcome.Restored)
            return choice;
        try
        {
            var state = await store.LoadStateAsync(key, ct);
            if (state is null || state.ProfileVersion != geometry.ProfileVersion)
                return RestoreOutcome.Gone;
            var notAfter = timeProvider.GetUtcNow().UtcDateTime.AddSeconds(settings.MaxAheadSeconds);
            var zone = ZoneProcessor.Restore(key, geometry.Geometry, geometry.ProfileVersion, settings.ZoneSettingsFor(site, _zoneSettings), state, notAfter);
            // Its clock moves on with the host's from here (TickIdleZones), as for a zone restored with its first record: the time the
            // stream was away is not skipped, since a record of the zone still to be read would then be late.
            _zones[key] = (zone, partition, timeProvider.GetUtcNow().UtcDateTime);
            return RestoreOutcome.Restored;
        }
        catch (Exception e) when (e is InvalidDataException or JsonException or ArgumentException or InvalidOperationException or NotSupportedException or OverflowException)
        {
            logger.LogDebug(e, "The saved state of zone {Zone} cannot be restored on assignment; the zone starts afresh with its first record", key);
            return RestoreOutcome.Unreadable;
        }
    }

    // The database did not answer: the restores wait and try again, and after RestoreAttempts give up (the zones left are restored with
    // their first record).
    private void RestoreFailed(Exception e)
    {
        if (!_restoreRetry.Failed())
        {
            logger.LogWarning(e, "Zones of the assigned partitions not restored now (the database did not answer); attempt {Attempt} of {Attempts}, the next in {Seconds} seconds",
                _restoreRetry.Failures, RestoreAttempts, RestoreRetryAfter.TotalSeconds);
            return;
        }

        logger.LogWarning(e, "Zones of the assigned partitions not restored after {Attempts} attempts (the database did not answer); they are restored when a record of them arrives",
            RestoreAttempts);
        _restoreTally[(int)RestoreOutcome.Database] += _toRestore.Count;
        _toList.Clear();
        _toRestore.Clear();
        ReportRestores();
    }

    // One line for the restores of an assignment, rather than one per zone.
    private void ReportRestores()
    {
        int Count(RestoreOutcome outcome) => _restoreTally[(int)outcome];
        if (_restoreTally.All(c => c == 0))
            return;
        var level = Count(RestoreOutcome.Full) > 0 ? LogLevel.Error
            : Count(RestoreOutcome.Malformed) + Count(RestoreOutcome.Unreadable) + Count(RestoreOutcome.Database) > 0 ? LogLevel.Warning
            : LogLevel.Information;
        logger.Log(level,
            "Queue stream worker restored {Restored} zones of its assigned partitions from their saved state; not restored: {Held} held already, " +
            "{NotPublished} not in their site's published zone profile, {OtherVersion} saved under another profile version, {Gone} no longer saved, " +
            "{Malformed} keys that are not zone keys, {Unreadable} saved states that fail their checks, {Full} beyond Stream:MaxZones, " +
            "{Database} the database did not answer for",
            Count(RestoreOutcome.Restored), Count(RestoreOutcome.Held), Count(RestoreOutcome.NotPublished), Count(RestoreOutcome.OtherVersion),
            Count(RestoreOutcome.Gone), Count(RestoreOutcome.Malformed), Count(RestoreOutcome.Unreadable), Count(RestoreOutcome.Full),
            Count(RestoreOutcome.Database));
        Array.Clear(_restoreTally);
    }

    /// <summary>What became of a saved zone on an assignment (ARV-274).</summary>
    internal enum RestoreOutcome
    {
        /// <summary>Restored from its saved state.</summary>
        Restored,

        /// <summary>Held already (a record of it came first), or its partition is no longer assigned.</summary>
        Held,

        /// <summary>Not in its site's published zone profile (retired, or the site's profile withdrawn): not restored.</summary>
        NotPublished,

        /// <summary>Saved under another profile version than the one published: it starts afresh with its first record, as before.</summary>
        OtherVersion,

        /// <summary>Its saved state was removed, or saved under another version, between the listing and the restore.</summary>
        Gone,

        /// <summary>A saved key that is not a zone key (CWE-501).</summary>
        Malformed,

        /// <summary>A saved state that fails its checks (CWE-501): the zone starts afresh with its first record.</summary>
        Unreadable,

        /// <summary>Beyond Stream:MaxZones.</summary>
        Full,

        /// <summary>Given up on after the database did not answer RestoreAttempts times.</summary>
        Database
    }

    /// <summary>Whether a zone saved under <paramref name="savedVersion"/> is restored, given its published geometry (null when it has none).</summary>
    internal static RestoreOutcome ChooseRestore(ZoneGeometry geometry, int savedVersion) =>
        geometry is null ? RestoreOutcome.NotPublished : geometry.ProfileVersion == savedVersion ? RestoreOutcome.Restored : RestoreOutcome.OtherVersion;

    private void Drop(IEnumerable<int> partitions)
    {
        var gone = partitions.ToHashSet();
        _toList.ExceptWith(gone);
        if (_toRestore.Count > 0)
        {
            var keep = _toRestore.Where(z => !gone.Contains(z.Partition)).ToList();
            _toRestore.Clear();
            foreach (var zone in keep)
                _toRestore.Enqueue(zone);
        }

        // ARV-274: an assignment's restores end with it: what became of them is said now, and the next assignment's start afresh.
        if (_toList.Count == 0 && _toRestore.Count == 0)
        {
            ReportRestores();
            _restoreRetry.Reset();
        }

        foreach (var (key, zone) in _zones.Where(z => gone.Contains(z.Value.Partition)).ToList())
            _zones.Remove(key);
        foreach (var position in _positions.Keys.Where(k => gone.Contains(k.Partition)).ToList())
            _positions.Remove(position);
        foreach (var position in _consumed.Keys.Where(k => gone.Contains(k.Partition)).ToList())
            _consumed.Remove(position);
        _committedAsked.RemoveWhere(k => gone.Contains(k.Partition));
        foreach (var partition in gone)
        {
            foreach (var key in _merge.Keys.Where(k => k.Partition == partition).ToList())
            {
                foreach (var held in _merge[key])
                {
                    _heldBytes -= held.Bytes;
                    _heldRecords--;
                }

                _merge.Remove(key);
            }
        }
        _assigned.ExceptWith(gone);
    }

    /// <summary>Reads and checks one record and holds it for the merge; an unreadable one is dead-lettered and held as a gap.</summary>
    private async Task HoldAsync(ConsumeResult<string, byte[]> result, CancellationToken ct)
    {
        _records++;
        var partition = result.Partition.Value;
        _consumed[(result.Topic, partition)] = result.Offset.Value + 1;
        SensingBatch batch = null;
        try
        {
            batch = result.Message.Value is null ? null : Read(result.Topic, result.Message.Value);
            // Health reports Ingest published before ARV-036 are keyed by device id: skipped and counted, not dead-lettered
            // (they are not misplaced records, and a rolling upgrade or the topic's three days would flood the dead letters).
            if (batch is DeviceStatusBatch legacy && Guid.TryParse(result.Message.Key, out var keyed) && keyed == legacy.DeviceId)
            {
                if (_legacyHealth++ % 1000 == 0)
                    logger.LogWarning("Device health reports keyed by device id (published before ARV-036) are skipped; {Count} so far", _legacyHealth);
                batch = null;
            }
            else if (batch is null || batch.SiteCode is not { Length: > 0 and <= 17 } || batch.QueueZoneName is not { Length: > 0 and <= 200 } ||
                !string.Equals(result.Message.Key, batch.ZoneKey, StringComparison.Ordinal) || batch.ReceivedUtc.Kind != DateTimeKind.Utc)
                throw new InvalidDataException("The record is not a sensing batch of the zone its key names.");
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            await DeadLetterAsync(result, e, ct);
            batch = null;
        }

        if (!_merge.TryGetValue((result.Topic, partition), out var queue))
            _merge[(result.Topic, partition)] = queue = new Queue<Held>();
        Trim(batch);
        var bytes = result.Message.Value?.Length ?? 0;
        _heldBytes += bytes;
        _heldRecords++;
        // A gap (dead letter) sorts first so that it never holds the others back.
        queue.Enqueue(new Held(result.Topic, partition, result.Offset.Value, result.Message.Key, batch?.ReceivedUtc ?? DateTime.MinValue, batch, bytes));
    }

    /// <summary>
    /// Applies the held records of every assigned partition in receive order while the order is certain: the earliest
    /// head is applied when every other assigned topic partition has a head of its own or nothing more to read. Merging
    /// across partitions too keeps a zone's records in order when partitions were added (its older records stay on the
    /// partition the old count gave). True when a zone's outputs reached their bound (checkpoint now).
    /// </summary>
    private async Task<bool> ReleaseAsync(IConsumer<string, byte[]> consumer, CancellationToken ct)
    {
        var full = false;
        while (_heldRecords > 0)
        {
            Held first = null;
            foreach (var queue in _merge.Values)
            {
                if (queue.TryPeek(out var head) && (first is null || Earlier(head, first)))
                    first = head;
            }

            if (first is null)
                return full;
            var forced = _heldRecords > settings.MaxMergeRecords || _heldBytes > settings.MaxMergeBytes;
            var certain = forced || consumer.Assignment.All(tp =>
                (tp.Topic == first.Topic && tp.Partition.Value == first.Partition) ||
                (_merge.TryGetValue((tp.Topic, tp.Partition.Value), out var other) && other.Count > 0) ||
                Drained(consumer, tp.Topic, tp.Partition.Value));
            if (!certain)
                return full;
            if (forced && _forcedReleases++ % 1000 == 0)
                logger.LogWarning("Queue stream merge buffer is full; records are applied before the other partitions caught up");
            _merge[(first.Topic, first.Partition)].Dequeue();
            _heldBytes -= first.Bytes;
            _heldRecords--;
            await ApplyAsync(first, first.Partition, ct);

            // The position moves only once the record is applied or its dead letter accepted, so a shutdown in between
            // replays it rather than skipping it.
            _positions[(first.Topic, first.Partition)] = first.Offset + 1;
            full |= _zones.TryGetValue(first.Batch?.ZoneKey ?? string.Empty, out var applied) && applied.Zone.Full;
        }

        return full;
    }

    private async Task ApplyAsync(Held first, int partition, CancellationToken ct)
    {
        if (first.Batch is null)
            return;
        if (_partitionCounts.Count > 0 && partition >= _partitionCounts.Max())
        {
            // A partition beyond every count seen: the topics grew since the last look.
            try
            {
                RefreshPartitionCount();
            }
            catch (KafkaException e)
            {
                logger.LogWarning(e, "Kafka metadata not available; the partition counts known so far stay in use");
            }
        }

        var crc = Crc32(System.Text.Encoding.UTF8.GetBytes(first.Batch.ZoneKey));
        if (_partitionCounts.Count > 0 && !_partitionCounts.Any(count => (int)(crc % (uint)count) == partition))
        {
            // A zone lives on the partition its key hashes to with the producers' partitioner (librdkafka's default,
            // consistent_random: CRC-32 of the key) under the topics' partition count, or an earlier count for records
            // produced before partitions were added; a record elsewhere (another partitioner) would split the zone's
            // state between instances.
            _wrongPartition++;
            await DeadLetterAsync(first, new InvalidDataException($"Zone {first.Batch.ZoneKey} does not belong to partition {partition}."), ct);
            return;
        }

        var zone = await ZoneAsync(first.Batch, partition, ct);
        if (zone is null)
            return;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        try
        {
            zone.Offer(first.Batch, now.AddSeconds(settings.MaxAheadSeconds));
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or OverflowException or InvalidDataException)
        {
            // A batch the checks let through but the engine refuses is dead-lettered, not allowed to stop the host;
            // a replay does the same, so the zone's state stays deterministic.
            _failedBatches++;
            logger.LogError(e, "Batch at {Topic} partition {Partition} offset {Offset} failed in zone {Zone}", first.Topic, partition, first.Offset, zone.ZoneKey);
            await DeadLetterAsync(first, e, ct);
        }

        _zones[zone.ZoneKey] = (zone, partition, now);
    }

    private readonly HashSet<int> _partitionCounts = [];

    /// <summary>The partition librdkafka's default partitioner (consistent_random) gives a non-empty key: CRC-32 of its UTF-8 bytes.</summary>
    public static int ExpectedPartition(string key, int partitions)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfLessThan(partitions, 1);
        return (int)(Crc32(System.Text.Encoding.UTF8.GetBytes(key)) % (uint)partitions);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    // Only the first events of a batch are used (Ingest's limit); the rest is not kept while the record waits. A health report's declared
    // lines beyond the registry's bound are cut to one more than it (ARV-259e5), which the zone still refuses as out of shape.
    internal static void Trim(SensingBatch batch)
    {
        const int max = ZoneProcessor.MaxEventsPerBatch;
        switch (batch)
        {
            case DeviceStatusBatch { CoveredLines.Count: > Ariva.Core.Domain.Entities.Device.MaxCoveredLines + 1 } s:
                s.CoveredLines = [.. s.CoveredLines.Take(Ariva.Core.Domain.Entities.Device.MaxCoveredLines + 1)];
                break;
            case VendorLineCrossingBatch { Crossings.Count: > max } c:
                c.Crossings = [.. c.Crossings.Take(max)];
                break;
            case ZoneOccupancyBatch { Occupancy.Count: > max } o:
                o.Occupancy = [.. o.Occupancy.Take(max)];
                break;
            case IntervalCountBatch { Intervals.Count: > max } i:
                i.Intervals = [.. i.Intervals.Take(max)];
                break;
            case TrackSampleBatch { Samples.Count: > max } t:
                t.Samples = [.. t.Samples.Take(max)];
                break;
        }
    }

    private static int Rank(string topic)
    {
        for (var k = 0; k < Topics.Count; k++)
        {
            if (string.Equals(Topics[k], topic, StringComparison.Ordinal))
                return k;
        }

        return Topics.Count;
    }

    private static bool Earlier(Held a, Held b) =>
        a.ReceivedUtc != b.ReceivedUtc ? a.ReceivedUtc < b.ReceivedUtc
        : a.Topic != b.Topic ? Rank(a.Topic) < Rank(b.Topic)
        : a.Partition != b.Partition ? a.Partition < b.Partition
        : a.Offset < b.Offset;

    /// <summary>Nothing more to read in this topic's partition right now: the consumer's position is at the end of the log.</summary>
    private bool Drained(IConsumer<string, byte[]> consumer, string topic, int partition)
    {
        var tp = new TopicPartition(topic, new Partition(partition));
        var cached = consumer.GetWatermarkOffsets(tp);
        long low = cached.Low, high = cached.High;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (high != Offset.Unset)
        {
            // The high watermark comes with every fetch response; the low one only from the broker.
            if (_consumed.TryGetValue((topic, partition), out var consumedNext))
                return consumedNext >= high;
            var at = consumer.Position(tp);
            if (at != Offset.Unset)
                return at.Value >= high;
            if (high == 0)
                return true;

            // Started from the group's committed offset and nothing read yet: that offset is where reading stands.
            if (_committedAsked.Add((topic, partition)))
            {
                try
                {
                    var committed = consumer.Committed([tp], TimeSpan.FromSeconds(5)).FirstOrDefault()?.Offset ?? Offset.Unset;
                    if (committed != Offset.Unset && committed.Value >= 0)
                    {
                        _consumed[(topic, partition)] = committed.Value;
                        return committed.Value >= high;
                    }
                }
                catch (KafkaException e)
                {
                    _committedAsked.Remove((topic, partition));
                    logger.LogWarning(e, "Committed offset of {Topic} partition {Partition} not available", topic, partition);
                    return false;
                }
            }
        }

        if (low == Offset.Unset || high == Offset.Unset)
        {
            // Nothing fetched from it yet: ask the broker, at most every 2 seconds per partition.
            if (!_watermarks.TryGetValue((topic, partition), out var known) || now - known.At > TimeSpan.FromSeconds(2))
            {
                try
                {
                    var queried = consumer.QueryWatermarkOffsets(tp, TimeSpan.FromSeconds(5));
                    known = (queried.Low.Value, queried.High.Value, now);
                    _watermarks[(topic, partition)] = known;
                }
                catch (KafkaException e)
                {
                    // Unknown for now: not drained, so the merge waits rather than guessing.
                    logger.LogWarning(e, "Watermarks of {Topic} partition {Partition} not available", topic, partition);
                    return false;
                }
            }

            (low, high) = (known.Low, known.High);
        }

        if (_consumed.TryGetValue((topic, partition), out var next))
            return next >= high;
        var position = consumer.Position(tp);
        if (position != Offset.Unset)
            return position.Value >= high;
        // Nothing consumed and no position yet: drained only when the partition holds nothing at all.
        return low >= high;
    }

    private async Task<ZoneProcessor> ZoneAsync(SensingBatch batch, int partition, CancellationToken ct)
    {
        if (_zones.TryGetValue(batch.ZoneKey, out var known))
            return known.Zone;
        if (_zones.Count >= settings.MaxZones)
        {
            if (_skippedFull++ % 1000 == 0)
                logger.LogError("Queue stream worker holds {Zones} zones, its maximum; events of zone {Zone} are skipped", _zones.Count, batch.ZoneKey);
            return null;
        }

        var geometry = await GeometryAsync(batch.SiteCode, batch.QueueZoneName, ct);
        if (geometry is null)
        {
            if (_skippedUnknownZone++ % 1000 == 0)
                logger.LogWarning("Zone {Zone} is not in its site's published zone profile; its events are skipped", batch.ZoneKey);
            return null;
        }

        // ARV-148: the zone runs under its site's lateness and push interval (Stream:Sites), as a replay of the site does.
        var siteSettings = settings.ZoneSettingsFor(batch.SiteCode, _zoneSettings);
        ZoneProcessor zone = null;
        try
        {
            var state = await store.LoadStateAsync(batch.ZoneKey, ct);
            if (state is not null && state.ProfileVersion == geometry.ProfileVersion)
            {
                var notAfter = timeProvider.GetUtcNow().UtcDateTime.AddSeconds(settings.MaxAheadSeconds);
                zone = ZoneProcessor.Restore(batch.ZoneKey, geometry.Geometry, geometry.ProfileVersion, siteSettings, state, notAfter);
            }
            else if (state is not null)
            {
                logger.LogInformation("Zone {Zone} moved from profile version {From} to {To}; it starts afresh", batch.ZoneKey, state.ProfileVersion, geometry.ProfileVersion);
            }
        }
        catch (Exception e) when (e is InvalidDataException or JsonException or ArgumentException or InvalidOperationException or NotSupportedException or OverflowException)
        {
            // A snapshot that does not pass its checks is not used (CWE-501); the zone starts afresh and says so.
            logger.LogError(e, "The saved state of zone {Zone} cannot be restored; the zone starts afresh", batch.ZoneKey);
            zone = null;
        }

        zone ??= new ZoneProcessor(batch.ZoneKey, geometry.Geometry, geometry.ProfileVersion, siteSettings);
        _zones[batch.ZoneKey] = (zone, partition, timeProvider.GetUtcNow().UtcDateTime);
        return zone;
    }

    private async Task<ZoneGeometry> GeometryAsync(string site, string zone, CancellationToken ct)
    {
        var key = ZoneKeys.For(site, zone);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (_geometryCache.TryGetValue(key, out var cached) && cached.Until > now)
            return cached.Geometry;
        var geometry = await geometries.LoadAsync(site, zone, ct);
        if (_geometryCache.Count > 4 * settings.MaxZones)
        {
            foreach (var expired in _geometryCache.Where(g => g.Value.Until <= now).Select(g => g.Key).ToList())
                _geometryCache.Remove(expired);
            if (_geometryCache.Count > 4 * settings.MaxZones)
                _geometryCache.Clear();
        }
        _geometryCache[key] = (geometry, now.AddMinutes(1));
        return geometry;
    }

    private DateTime _nextDeskTerms = DateTime.MinValue;

    // The desk terms of the held zones (ARV-064), every DeskTermSeconds. Live data only: a minute's nowcast takes the term
    // the zone had when the minute closed, so a replay after a restart may give a replayed minute another nowcast than the
    // first run did (the bins and waits do not depend on it). A failed read keeps the terms the zones have; they age out.
    private async Task RefreshDeskTermsAsync(CancellationToken ct)
    {
        if (deskTerms is null || settings.DeskTermSeconds == 0 || _zones.Count == 0)
            return;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (now < _nextDeskTerms)
            return;
        _nextDeskTerms = now.AddSeconds(settings.DeskTermSeconds);
        try
        {
            var terms = await deskTerms.LoadAsync([.. _zones.Keys], _zoneSettings.ExitWindowMinutes, _zoneSettings.SensorCycle, ct);
            foreach (var (key, (zone, _, _)) in _zones)
                zone.UseDesks(terms.GetValueOrDefault(key));
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is NpgsqlException or TimeoutException or IOException or InvalidOperationException)
        {
            logger.LogWarning(e, "Desk terms not read; the zones keep the ones they have");
        }
    }

    // Caught up: zones idle for IdleTickSeconds of the host's clock move their own clock on by the time elapsed.
    private void TickIdleZones()
    {
        if (settings.IdleTickSeconds == 0)
            return;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var (key, (zone, partition, last)) in _zones.ToList())
        {
            var idle = now - last;
            if (idle < TimeSpan.FromSeconds(settings.IdleTickSeconds))
                continue;
            try
            {
                zone.Tick(zone.ReferenceUtc + idle);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or OverflowException or InvalidDataException)
            {
                _failedBatches++;
                logger.LogError(e, "Idle tick failed in zone {Zone}", key);
            }

            _zones[key] = (zone, partition, now);
        }
    }

    private async Task Checkpoint(IConsumer<string, byte[]> consumer, CancellationToken ct)
    {
        var zones = _zones.Values.Select(z => z.Zone).ToList();
        var outputs = zones.Select(z => z.Peek()).ToList();
        var states = zones.Select(z => z.Capture()).ToList();
        var offsets = _positions.Select(p => new StreamOffset(p.Key.Topic, p.Key.Partition, p.Value)).ToList();
        if (offsets.Count == 0 && states.Count == 0)
            return;
        await store.SaveAsync(new StreamCheckpoint(GroupId, outputs, states, [], offsets), ct);

        // Only now, with the rows committed, are they removed from the zones: a failed or cancelled write loses nothing.
        for (var k = 0; k < zones.Count; k++)
            zones[k].Acknowledge(outputs[k]);
        Refusals(outputs);

        // The latest minute of each zone that has a new one goes to the live hub (ARV-035); screens are best effort,
        // so a Redis failure is logged and the next checkpoint carries on.
        if (live is not null)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var latest = outputs.Where(o => o.Live.Count > 0).Select(o => LiveMinuteSnapshots.From(System.Linq.Enumerable.MaxBy(o.Live, l => l.MinuteUtc), now)).ToList();
            try
            {
                await live.PublishAsync(latest, ct);
            }
            catch (Exception e) when (e is StackExchange.Redis.RedisException or TimeoutException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogWarning(e, "Live snapshots not published; the next checkpoint publishes again");
            }
        }
        try
        {
            // Nothing read since the assignment (only zones restored from their saved state): no position to commit.
            if (offsets.Count > 0)
                consumer.Commit(offsets.Select(o => new TopicPartitionOffset(o.Topic, new Partition(o.Partition), new Offset(o.NextOffset))));
        }
        catch (KafkaException e)
        {
            // The database holds the positions; Kafka's committed offsets only matter for a partition never saved.
            logger.LogWarning(e, "Committing offsets to Kafka failed; the saved positions stay authoritative");
        }

        logger.LogDebug("Queue stream checkpoint: {Zones} zones, {Rows} rows, {Records} records, {Dead} dead letters, {Failed} failed batches, {Wrong} on the wrong partition, {Forced} forced merges",
            states.Count, outputs.Sum(o => o.Count), _records, _deadLettered, _failedBatches, _wrongPartition, _forcedReleases);
    }

    /// <summary>
    /// ARV-231 (CWE-778): what each zone's queue engine refused or applied early reaches the refusal metric by kind and site, and
    /// a warning per zone and kind at most once a minute names the zone, the kind and the count (never an event's content), so a
    /// zone at its buffer's bound no longer loses crossings silently. Counted once the checkpoint wrote the outputs.
    /// </summary>
    private void Refusals(IReadOnlyList<ZoneOutputs> outputs)
    {
        foreach (var output in outputs.Where(o => o.Refusals.Count > 0))
        {
            var slash = output.ZoneKey.IndexOf('/', StringComparison.Ordinal);
            var site = slash > 0 ? output.ZoneKey[..slash] : output.ZoneKey;
            foreach (var (kind, count) in output.Refusals)
                metrics?.Refused(site, kind, count);
            _refusalWarnings.Add(output.ZoneKey, output.Refusals);
        }

        foreach (var (zone, kind, count) in _refusalWarnings.Due())
            logger.LogWarning("Zone {Zone}: the queue engine refused or applied early {Count} events of kind {Kind} since its last warning (wiki 10, 4.22)",
                zone, count, kind);
    }

    // ARV-166: a dead letter is retried until the broker takes it or the worker stops; a stop leaves the record unreleased,
    // so its position is not saved and it is read again (and dead-lettered then) after restart.
    private Task DeadLetterAsync(ConsumeResult<string, byte[]> result, Exception error, CancellationToken ct)
    {
        var headers = (result.Message.Headers ?? [])
            .Select(h => new KeyValuePair<string, string>(h.Key, h.GetValueBytes() is { } bytes ? System.Text.Encoding.UTF8.GetString(bytes) : null));
        var letter = DeadLetters.Create(result.Topic, result.Partition.Value, result.Offset.Value, result.Message.Key, result.Message.Value, headers, GroupId,
            error, timeProvider.GetUtcNow().UtcDateTime);
        logger.LogError("Unreadable record at {Topic} partition {Partition} offset {Offset}; sent to the dead-letter topic", result.Topic, result.Partition.Value, result.Offset.Value);
        return SendAsync(letter, ct);
    }

    // A held batch that cannot be applied: its value is serialised again from what was read (the raw bytes are not kept).
    private Task DeadLetterAsync(Held held, Exception error, CancellationToken ct)
    {
        var body = held.Batch is null ? [] : JsonSerializer.SerializeToUtf8Bytes(held.Batch, held.Batch.GetType(), EventCatalog.Json);
        var letter = DeadLetters.Create(held.Topic, held.Partition, held.Offset, held.Key, body, [], GroupId, error, timeProvider.GetUtcNow().UtcDateTime);
        return SendAsync(letter, ct);
    }

    // A dead letter the broker refuses is tried again (2, 4 ... 30 seconds; each attempt ends within the produce bound,
    // ARV-166) and holds the partition until it is accepted or the worker stops.
    // ARV-125a: counted once, sent, or failed when the worker stops first (the record is read again after restart).
    private async Task SendAsync(DeadLetter letter, CancellationToken ct)
    {
        _deadLettered++;
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await deadLetters.SendAsync(letter, ct);
                    producerMetrics?.DeadLetter(letter, KafkaProducerMetrics.SentOutcome);
                    return;
                }
                catch (Exception e) when (!ct.IsCancellationRequested && e is not OperationCanceledException)
                {
                    var wait = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt, 5))));
                    logger.LogError(e, "Dead letter for {Topic} partition {Partition} offset {Offset} not accepted (attempt {Attempt}); retrying in {Wait}",
                        letter.Topic, letter.Partition, letter.Offset, attempt, wait);
                    await Task.Delay(wait, timeProvider, ct);
                }
            }
        }
        catch (Exception)
        {
            // A stop (or a failure that stops the worker) before the broker took it: not sent, read again after restart.
            producerMetrics?.DeadLetter(letter, KafkaProducerMetrics.FailedOutcome);
            throw;
        }
    }
}
