using Ariva.Core.Messaging;
using Confluent.Kafka;
using MassTransit;
using Microsoft.Extensions.Hosting;

namespace Ariva.Infra.Messaging.Kafka;

/// <summary>
/// One consumer of the host (ARV-176): its topic (a <see cref="KafkaTopics"/> constant), its consumer group, and how to
/// connect its topic endpoint to the rider.
/// </summary>
public sealed record ConsumedTopic(string Topic, string Group, Func<IKafkaRider, HostReceiveEndpointHandle> Connect);

/// <summary>
/// Connects the host's topic endpoints to MassTransit's Kafka rider once their topics exist (ARV-176), so that a consumed
/// topic the broker does not have never stops the host's other consumers.
/// <para>
/// MassTransit 8.5.11 starts every topic endpoint configured with the bus as part of the bus's start and waits for all of
/// them (<c>MassTransitBus.StartAsync</c> awaits the riders' endpoints' Ready); an endpoint whose topic is missing faults
/// its subscription ("Subscribed topic not available"), the start faults ("Bus start faulted"), the bus is stopped and
/// never started again, and no consumer of the host consumes anything while readiness answers 200 (proven by
/// <c>MissingTopicTests</c>; backlog/progress.md, ARV-176). So no topic endpoint is configured with the bus: the bus starts
/// with none, and this service, which starts after it, connects each consumer's endpoint at run time
/// (<see cref="IKafkaTopicEndpointConnector.ConnectTopicEndpoint{TKey,TValue}(string,string,Action{IRiderRegistrationContext,IKafkaTopicReceiveEndpointConfigurator{TKey,TValue}})"/>,
/// the same group and pipe as before). An endpoint's fault after it started is retried by its own transport, as it always was.
/// A connect that throws is logged and tried again at the next look: MassTransit removes an endpoint whose start threw
/// (<c>ReceiveEndpointCollection.StartEndpoint</c>), and adds one only once it is built, so a retry never meets "already added".
/// Until it connects, the consumer reads 1 on the <see cref="KafkaProducerMetrics.ConsumerUnconnectedName"/> gauge (ARV-125a).
/// </para>
/// <para>
/// At start it waits up to <see cref="ConsumerSettings.StartWaitSeconds"/> (60) for the consumed topics Ariva owns (the Helm
/// topics job, or Ariva.Api.Main in vm-local, creates them; AMAN creates its own feed topics, so those are never waited
/// for), connects the endpoint of every consumed topic that exists and waits at most <see cref="ReadyWaitSeconds"/> for
/// those endpoints to be ready; the host's start holds its web server, so both waits are bounded to fit the pod's startup
/// allowance (checked at start). A consumed topic that does not exist is logged as a Warning naming it, shows as 1 on the
/// <see cref="KafkaProducerMetrics.ConsumedTopicMissingName"/> gauge, and is looked for again every
/// <see cref="ConsumerSettings.TopicWatchSeconds"/>; its endpoint is connected, without a restart, once it appears.
/// While the broker cannot be read, that is logged instead ("Kafka not reachable") and the gauge keeps the state it last
/// read (a topic never read reports nothing; the <c>kafka</c> readiness check is the outage's signal). Once every consumer
/// is connected the watch ends and its admin client is released.
/// Topics are read from all topics' metadata, never one topic's: a metadata request that names a topic lets a broker with
/// <c>auto.create.topics.enable</c> (Kafka's default) create it. Every read is awaited with the stop's token, so the stop
/// never waits for a metadata request (librdkafka's call cannot be cancelled; it ends within its 10 second timeout on its
/// own, and the admin client is disposed after it).
/// </para>
/// </summary>
internal sealed class KafkaConsumerEndpoints : IHostedService, IDisposable
{
    /// <summary>The most the host's start waits for its connected consumers to be ready, in seconds.</summary>
    internal const int ReadyWaitSeconds = 30;

    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(10);

    private readonly KafkaSettings _settings;
    private readonly Func<IKafkaRider> _riders;
    private readonly Func<IReadOnlySet<string>> _readTopics;
    private readonly TimeProvider _time;
    private readonly ILogger<KafkaConsumerEndpoints> _logger;
    private readonly KafkaProducerMetrics _metrics;
    private readonly Consumer[] _consumers;
    private readonly bool _ownsAdmin;
    private readonly CancellationTokenSource _stopping = new();
    private IAdminClient _admin;
    private IKafkaRider _rider;
    private Task _reading = Task.CompletedTask;
    private Task _watch = Task.CompletedTask;
    private bool _unreadable;
    private int _disposed;

    public KafkaConsumerEndpoints(KafkaSettings settings, IReadOnlyList<ConsumedTopic> consumed, Func<IKafkaRider> riders, TimeProvider timeProvider,
        ILogger<KafkaConsumerEndpoints> logger, KafkaProducerMetrics metrics = null)
        : this(settings, consumed, riders, null, timeProvider, logger, metrics)
    {
    }

    /// <param name="readTopics">
    /// The topics the broker has, read synchronously (it may block, and throws <see cref="KafkaException"/> while the broker
    /// cannot be read); null for the broker's own, through an admin client. Tests pass their own.
    /// </param>
    internal KafkaConsumerEndpoints(KafkaSettings settings, IReadOnlyList<ConsumedTopic> consumed, Func<IKafkaRider> riders, Func<IReadOnlySet<string>> readTopics,
        TimeProvider timeProvider, ILogger<KafkaConsumerEndpoints> logger, KafkaProducerMetrics metrics = null)
    {
        ArgumentNullException.ThrowIfNull(consumed);
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _riders = riders ?? throw new ArgumentNullException(nameof(riders));
        _ownsAdmin = readTopics is null;
        _readTopics = readTopics ?? ReadBrokerTopics;
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metrics = metrics;
        _consumers = [.. consumed.Select(c => new Consumer(c))];
    }

    /// <summary>The ready wait (<see cref="ReadyWaitSeconds"/>); tests shorten it.</summary>
    internal TimeSpan ReadyWait { get; init; } = TimeSpan.FromSeconds(ReadyWaitSeconds);

    private TimeSpan WatchEvery => TimeSpan.FromSeconds(Math.Max(1, _settings.Consumers.TopicWatchSeconds));

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_consumers.Length == 0)
            return;
        // The rider exists once the bus has started (MassTransit throws before), which is why this resolves it here.
        _rider = _riders();
        if (_ownsAdmin)
            _admin = new AdminClientBuilder(KafkaClientConfig.Admin(_settings)).Build();
        // A consumer reports once its topic has been read: 1 while the broker says the topic does not exist, 0 otherwise.
        _metrics?.ObserveConsumedTopics(() => _consumers.Where(c => c.State != TopicState.Unknown)
            .Select(c => (c.Topic.Topic, c.Topic.Group, c.State == TopicState.Missing)));
        // ARV-125a: 1 while a consumer's topic exists but its connect failed (it is tried again at each check), 0 otherwise.
        _metrics?.ObserveUnconnectedConsumers(() => _consumers.Where(c => c.State != TopicState.Unknown)
            .Select(c => (c.Topic.Topic, c.Topic.Group, c.State == TopicState.Present)));

        var until = _time.GetUtcNow() + TimeSpan.FromSeconds(Math.Max(0, _settings.Consumers.StartWaitSeconds));
        var owned = _consumers.Select(c => c.Topic.Topic).Where(t => Enumerable.Contains(KafkaTopics.Owned, t, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal).ToList();
        var present = await PresentAsync(cancellationToken);
        var announced = false;
        while (owned.Any(topic => present is null || !present.Contains(topic)) && _time.GetUtcNow() < until)
        {
            if (!announced)
            {
                _logger.LogWarning("Waiting for Kafka topics before connecting their consumers: {Topics}",
                    string.Join(", ", owned.Where(t => present is null || !present.Contains(t))));
                announced = true;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), _time, cancellationToken);
            present = await PresentAsync(cancellationToken);
        }

        var connected = present is null ? [] : Connect(present, watching: false, cancellationToken);

        // The connected endpoints are ready (their consumers joined their groups) before the host's start ends, within the
        // ready wait: the start holds the web server, and the pod's startup probe would restart a host that waited longer.
        var ready = _time.GetUtcNow() + ReadyWait;
        foreach (var (consumer, handle) in connected)
        {
            var left = ready - _time.GetUtcNow();
            try
            {
                await handle.Ready.WaitAsync(left > TimeSpan.Zero ? left : TimeSpan.Zero, _time, cancellationToken);
            }
            catch (Exception e) when (e is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
            {
                // Its transport retries by itself; the host starts all the same.
                _logger.LogWarning(e, "Kafka consumer {Group} of {Topic} not ready yet; it keeps trying", consumer.Topic.Group, consumer.Topic.Topic);
            }
        }

        _watch = Task.Run(() => WatchAsync(_stopping.Token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        await _watch.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stopping.Cancel();
        _stopping.Dispose();
        ReleaseAdmin();
    }

    /// <summary>Looks for the missing topics every <see cref="ConsumerSettings.TopicWatchSeconds"/> and connects each that appeared.</summary>
    private async Task WatchAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _consumers.Any(c => c.State != TopicState.Connected))
            {
                await Task.Delay(WatchEvery, _time, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                if (ct.IsCancellationRequested)
                    break;
                try
                {
                    var present = await PresentAsync(ct);
                    // The stop may have come while the read ran: nothing is connected after it.
                    if (ct.IsCancellationRequested)
                        break;
                    if (present is not null)
                        Connect(present, watching: true, ct);
                }
                catch (Exception e) when (!ct.IsCancellationRequested && e is not OutOfMemoryException)
                {
                    // The watch must outlive anything a look can throw; it tries again at its next look.
                    _logger.LogWarning(e, "Kafka consumed topics could not be checked; trying again in {Seconds} s", WatchEvery.TotalSeconds);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The host is stopping.
        }

        // Every consumer is connected: nothing is looked for any more, so the admin client goes.
        if (!ct.IsCancellationRequested)
            ReleaseAdmin();
    }

    /// <summary>
    /// Connects the endpoint of every consumer not yet connected whose topic is in <paramref name="present"/>, and records
    /// the others as missing (logged once each time a topic goes missing). A connect that throws is logged with the
    /// consumer's group and topic and tried again at the next look. Returns the endpoints connected.
    /// </summary>
    private List<(Consumer Consumer, HostReceiveEndpointHandle Handle)> Connect(IReadOnlySet<string> present, bool watching, CancellationToken ct)
    {
        var connected = new List<(Consumer, HostReceiveEndpointHandle)>();
        foreach (var consumer in _consumers.Where(c => c.State != TopicState.Connected))
        {
            if (ct.IsCancellationRequested)
                break;
            var topic = consumer.Topic;
            if (!present.Contains(topic.Topic))
            {
                if (consumer.State != TopicState.Missing)
                {
                    _logger.LogWarning("Kafka topic {Topic}, consumed by {Group}, does not exist; its consumer starts when the topic appears (checked every {Seconds} s)",
                        topic.Topic, topic.Group, WatchEvery.TotalSeconds);
                }

                consumer.State = TopicState.Missing;
                continue;
            }

            var was = consumer.State;
            HostReceiveEndpointHandle handle;
            try
            {
                handle = topic.Connect(_rider);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                consumer.State = TopicState.Present;
                _logger.LogWarning(e, "Kafka consumer {Group} of {Topic} could not be connected; trying again in {Seconds} s",
                    topic.Group, topic.Topic, WatchEvery.TotalSeconds);
                continue;
            }

            consumer.State = TopicState.Connected;
            connected.Add((consumer, handle));
            if (watching && was == TopicState.Missing)
                _logger.LogInformation("Kafka topic {Topic} now exists; its consumer {Group} is starting", topic.Topic, topic.Group);
            else if (watching)
                _logger.LogInformation("Kafka consumer {Group} of {Topic} is starting", topic.Group, topic.Topic);
        }

        return connected;
    }

    /// <summary>
    /// The topics the broker has, or null while they cannot be read (logged once per outage). The read runs on the thread
    /// pool and is awaited with <paramref name="ct"/>, so a stop never waits for it; an abandoned read ends by itself.
    /// </summary>
    private async Task<IReadOnlySet<string>> PresentAsync(CancellationToken ct)
    {
        var reading = Task.Run(() =>
        {
            try
            {
                return (Topics: _readTopics(), Error: (Exception)null);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                return (Topics: null, Error: e);
            }
        }, CancellationToken.None);
        Volatile.Write(ref _reading, reading);
        var read = await reading.WaitAsync(ct);
        if (read.Error is null)
        {
            if (_unreadable)
                _logger.LogInformation("Kafka reachable again; its topics were read");
            _unreadable = false;
            return read.Topics ?? new HashSet<string>(StringComparer.Ordinal);
        }

        if (!_unreadable)
        {
            var waiting = string.Join(", ", _consumers.Where(c => c.State != TopicState.Connected).Select(c => c.Topic.Topic).Distinct(StringComparer.Ordinal));
            if (read.Error is KafkaException)
                _logger.LogWarning(read.Error, "Kafka not reachable; the consumers of {Topics} start once it is and their topics exist (checked every {Seconds} s)", waiting, WatchEvery.TotalSeconds);
            else
                _logger.LogWarning(read.Error, "Kafka topics could not be read; the consumers of {Topics} start once they can be and their topics exist (checked every {Seconds} s)", waiting, WatchEvery.TotalSeconds);
        }

        _unreadable = true;
        return null;
    }

    /// <summary>The broker's topics with partitions, from all topics' metadata (never one topic's).</summary>
    private IReadOnlySet<string> ReadBrokerTopics()
    {
        var admin = Volatile.Read(ref _admin) ?? throw new ObjectDisposedException(nameof(KafkaConsumerEndpoints), "The admin client was released.");
        var metadata = admin.GetMetadata(MetadataTimeout);
        return metadata.Topics.Where(t => t.Error.Code == ErrorCode.NoError && t.Partitions.Count > 0).Select(t => t.Topic).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Disposes the admin client, after the read that may still be running on it (an abandoned one ends within its timeout).</summary>
    private void ReleaseAdmin()
    {
        var admin = Interlocked.Exchange(ref _admin, null);
        if (admin is null)
            return;
        var reading = Volatile.Read(ref _reading);
        if (reading.IsCompleted)
            admin.Dispose();
        else
            _ = reading.ContinueWith(_ => admin.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private enum TopicState
    {
        /// <summary>Not read yet (the broker could not be read so far).</summary>
        Unknown,

        /// <summary>The broker does not have the topic.</summary>
        Missing,

        /// <summary>The topic exists and its consumer could not be connected yet.</summary>
        Present,

        /// <summary>The consumer's endpoint is connected.</summary>
        Connected
    }

    /// <summary>A consumer and its state, read by the gauge on another thread.</summary>
    private sealed class Consumer(ConsumedTopic topic)
    {
        private int _state;

        public ConsumedTopic Topic { get; } = topic;

        public TopicState State
        {
            get => (TopicState)Volatile.Read(ref _state);
            set => Volatile.Write(ref _state, (int)value);
        }
    }
}
