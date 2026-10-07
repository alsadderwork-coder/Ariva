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
    }
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
/// without committing; the host's restart replays from the last checkpoint.
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
    DeskTermSource deskTerms = null) : BackgroundService
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
    private readonly Dictionary<string, (ZoneGeometry Geometry, DateTime Until)> _geometryCache = new(StringComparer.Ordinal);
    private long _skippedUnknownZone, _skippedFull, _deadLettered, _records, _legacyHealth;

    public string GroupId => PartitionedConsumer<object>.GroupId(kafka, Purpose);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var problems = settings.Problems().Concat(_zoneSettings.Problems()).ToList();
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
        var every = TimeSpan.FromSeconds(Math.Max(1, kafka.Consumers.CheckpointSeconds));
        var paused = new List<TopicPartition>();
        var next = timeProvider.GetUtcNow() + every;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = consumer.Consume(TimeSpan.FromMilliseconds(250));
                var full = false;
                if (result?.Message is not null)
                {
                    await HoldAsync(result);
                    full = await ReleaseAsync(consumer, ct);
                }
                else
                {
                    full = await ReleaseAsync(consumer, ct);
                    if (_heldRecords == 0)
                        TickIdleZones();
                }

                await RefreshDeskTermsAsync(ct);

                if (full || (timeProvider.GetUtcNow() >= next && _positions.Count > 0))
                {
                    next = timeProvider.GetUtcNow() + every;
                    try
                    {
                        await Checkpoint(consumer, ct);
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
            consumer.Close();
        }
    }

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
        if (_partitionCounts.Add(counts[0]) && _partitionCounts.Count > 1)
            logger.LogWarning("The sensing topics now have {Count} partitions; records hashed over the earlier counts are still accepted", counts[0]);
        return true;
    }

    // Start each partition where the saved state left it; a partition never saved starts from the committed offset.
    private List<TopicPartitionOffset> Assigned(List<TopicPartition> partitions, CancellationToken ct)
    {
        // Partitions may have been added (that is what caused this rebalance); a failure here keeps the counts known so far.
        try
        {
            RefreshPartitionCount();
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
        return [.. partitions.Select(p => saved.TryGetValue((p.Topic, p.Partition.Value), out var offset)
            ? new TopicPartitionOffset(p, new Offset(offset))
            : new TopicPartitionOffset(p, Offset.Unset))];
    }

    private void Drop(IEnumerable<int> partitions)
    {
        var gone = partitions.ToHashSet();
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
    private async Task HoldAsync(ConsumeResult<string, byte[]> result)
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
            await DeadLetterAsync(result, e);
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

    // Only the first events of a batch are used (Ingest's limit); the rest is not kept while the record waits.
    private static void Trim(SensingBatch batch)
    {
        const int max = ZoneProcessor.MaxEventsPerBatch;
        switch (batch)
        {
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

        ZoneProcessor zone = null;
        try
        {
            var state = await store.LoadStateAsync(batch.ZoneKey, ct);
            if (state is not null && state.ProfileVersion == geometry.ProfileVersion)
            {
                var notAfter = timeProvider.GetUtcNow().UtcDateTime.AddSeconds(settings.MaxAheadSeconds);
                zone = ZoneProcessor.Restore(batch.ZoneKey, geometry.Geometry, geometry.ProfileVersion, _zoneSettings, state, notAfter);
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

        zone ??= new ZoneProcessor(batch.ZoneKey, geometry.Geometry, geometry.ProfileVersion, _zoneSettings);
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

        // The latest minute of each zone that has a new one goes to the live hub (ARV-035); screens are best effort,
        // so a Redis failure is logged and the next checkpoint carries on.
        if (live is not null)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var latest = outputs.Where(o => o.Live.Count > 0).Select(o => Ariva.Infra.Live.LiveZoneSnapshot.From(System.Linq.Enumerable.MaxBy(o.Live, l => l.MinuteUtc), now)).ToList();
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

    private Task DeadLetterAsync(ConsumeResult<string, byte[]> result, Exception error)
    {
        var headers = (result.Message.Headers ?? [])
            .Select(h => new KeyValuePair<string, string>(h.Key, h.GetValueBytes() is { } bytes ? System.Text.Encoding.UTF8.GetString(bytes) : null));
        var letter = DeadLetters.Create(result.Topic, result.Partition.Value, result.Offset.Value, result.Message.Key, result.Message.Value, headers, GroupId,
            error, timeProvider.GetUtcNow().UtcDateTime);
        logger.LogError("Unreadable record at {Topic} partition {Partition} offset {Offset}; sent to the dead-letter topic", result.Topic, result.Partition.Value, result.Offset.Value);
        return SendAsync(letter, CancellationToken.None);
    }

    // A held batch that cannot be applied: its value is serialised again from what was read (the raw bytes are not kept).
    private Task DeadLetterAsync(Held held, Exception error, CancellationToken ct)
    {
        var body = held.Batch is null ? [] : JsonSerializer.SerializeToUtf8Bytes(held.Batch, held.Batch.GetType(), EventCatalog.Json);
        var letter = DeadLetters.Create(held.Topic, held.Partition, held.Offset, held.Key, body, [], GroupId, error, timeProvider.GetUtcNow().UtcDateTime);
        return SendAsync(letter, ct);
    }

    // A dead letter the broker refuses is tried again (2, 4 ... 30 seconds) and holds the partition until it is accepted.
    private async Task SendAsync(DeadLetter letter, CancellationToken ct)
    {
        _deadLettered++;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await deadLetters.SendAsync(letter, ct);
                return;
            }
            catch (Exception e) when (!ct.IsCancellationRequested && e is not OperationCanceledException)
            {
                var wait = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt, 5))));
                logger.LogError(e, "Dead letter for {Topic} offset {Offset} not accepted (attempt {Attempt}); retrying in {Wait}", letter.Topic, letter.Offset, attempt, wait);
                await Task.Delay(wait, timeProvider, ct);
            }
        }
    }
}
