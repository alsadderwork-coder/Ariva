using Confluent.Kafka;
using Confluent.Kafka.Admin;

namespace Ariva.LoadTests;

/// <summary>
/// Ariva.Api.Stream's lag during a run (ARV-124a), read from the broker as an operator would read it with
/// kafka-consumer-groups: for each consumer group, the records on its topics past the offsets it has committed (the queue
/// engine commits after each checkpoint, every 5 seconds; the sensing archive after its batches), every 2 seconds. No host
/// serves its metrics to a lab without a collector, so this is the lab's view of <c>ariva.stream.consumer_lag</c>.
/// </summary>
public sealed class StreamLag(string bootstrap) : IAsyncDisposable
{
    /// <summary>The groups watched and their topics (KafkaTopics and the consumer group names, written out).</summary>
    public static readonly IReadOnlyList<(string Group, string[] Topics)> Groups =
    [
        ("ariva-stream.queue-engine", ["ariva.device.track-sample.v1", "ariva.device.vendor-line-crossing.v1", "ariva.device.zone-occupancy.v1", "ariva.device.interval-count.v1", "ariva.device.health.v1"]),
        ("ariva-stream.sensing-archive-tracks", ["ariva.device.track-sample.v1"])
    ];

    public sealed record Sample(DateTime At, string Phase, IReadOnlyDictionary<string, long> Lag);

    private readonly IAdminClient _admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrap }).Build();
    private readonly List<Sample> _samples = [];
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;
    private volatile string _phase = "start";
    private int _errors;

    public IReadOnlyDictionary<string, int> Partitions { get; private set; } = new Dictionary<string, int>();

    public string Phase
    {
        get => _phase;
        set => _phase = value;
    }

    public void Start() => _loop = LoopAsync(_stop.Token);

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(ct).AsTask().ContinueWith(t => t.IsCompletedSuccessfully && t.Result, TaskScheduler.Default))
        {
            try
            {
                var lag = await ReadAsync(ct);
                lock (_gate)
                    _samples.Add(new Sample(DateTime.UtcNow, _phase, lag));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is KafkaException or InvalidOperationException or TimeoutException)
            {
                Interlocked.Increment(ref _errors);
            }
        }
    }

    private async Task<IReadOnlyDictionary<string, long>> ReadAsync(CancellationToken ct)
    {
        var metadata = _admin.GetMetadata(TimeSpan.FromSeconds(5));
        var partitions = Groups.SelectMany(g => g.Topics).Distinct(StringComparer.Ordinal)
            .ToDictionary(t => t, t => metadata.Topics.FirstOrDefault(m => m.Topic == t)?.Partitions.Count ?? 0, StringComparer.Ordinal);
        Partitions = partitions;
        var all = partitions.SelectMany(p => Enumerable.Range(0, p.Value).Select(i => new TopicPartition(p.Key, i))).ToList();
        var latest = (await _admin.ListOffsetsAsync(all.Select(tp => new TopicPartitionOffsetSpec { TopicPartition = tp, OffsetSpec = OffsetSpec.Latest() }),
                new ListOffsetsOptions { RequestTimeout = TimeSpan.FromSeconds(5) }))
            .ResultInfos.ToDictionary(r => r.TopicPartitionOffsetError.TopicPartition, r => r.TopicPartitionOffsetError.Offset.Value);
        var lag = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (group, topics) in Groups)
        {
            ct.ThrowIfCancellationRequested();
            var mine = all.Where(tp => topics.Contains(tp.Topic, StringComparer.Ordinal)).ToList();
            var committed = await _admin.ListConsumerGroupOffsetsAsync([new ConsumerGroupTopicPartitions(group, mine)],
                new ListConsumerGroupOffsetsOptions { RequestTimeout = TimeSpan.FromSeconds(5) });
            // A partition the group has not committed yet counts from its start (the groups read from the earliest offset).
            lag[group] = committed.SelectMany(c => c.Partitions)
                .Sum(p => Math.Max(0, latest.GetValueOrDefault(p.TopicPartition) - (p.Offset.IsSpecial ? 0 : p.Offset.Value)));
        }

        return lag;
    }

    public IReadOnlyList<Sample> Samples
    {
        get
        {
            lock (_gate)
                return [.. _samples];
        }
    }

    public int Errors => _errors;

    /// <summary>
    /// When a group was back to its steady lag after <paramref name="phase"/> ended: the first sample from then on at most
    /// the steady phase's 95th percentile (and at least 1,000 records, a checkpoint's worth at the pilot rate); null when it
    /// never was within the run.
    /// </summary>
    public (long Steady, long Peak, double? CatchUpSeconds) CatchUp(string group, string steadyPhase, string phase)
    {
        var samples = Samples;
        var steady = samples.Where(s => s.Phase == steadyPhase && s.Lag.ContainsKey(group)).Select(s => s.Lag[group]).Order().ToList();
        var baseline = Math.Max(1_000, steady.Count == 0 ? 0 : steady[Math.Max(0, (int)Math.Ceiling(0.95 * steady.Count) - 1)]);
        var peak = samples.Where(s => s.Lag.ContainsKey(group)).Select(s => s.Lag[group]).DefaultIfEmpty(0).Max();
        var end = samples.LastOrDefault(s => s.Phase == phase)?.At;
        if (end is null)
            return (baseline, peak, null);
        var back = samples.FirstOrDefault(s => s.At > end && s.Lag.TryGetValue(group, out var lag) && lag <= baseline);
        return (baseline, peak, back is null ? null : Math.Round((back.At - end.Value).TotalSeconds, 1));
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _loop;
        _admin.Dispose();
        _stop.Dispose();
    }
}
