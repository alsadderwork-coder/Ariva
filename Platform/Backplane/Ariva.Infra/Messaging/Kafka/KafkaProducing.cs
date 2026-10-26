using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Ariva.Core.Messaging;
using Ariva.Infra.Messaging.Outbox;
using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Headers = Confluent.Kafka.Headers;

namespace Ariva.Infra.Messaging.Kafka;

/// <summary>
/// Values on the wire are plain JSON documents (<see cref="EventCatalog.Json"/>), not MassTransit envelopes, so the raw
/// Confluent consumer in Ariva.Api.Stream and the Python forecasting worker read them as they are. The consumers' value
/// reader; Ariva's producer writes the same bytes (<see cref="KafkaProduce.ValueOf"/>).
/// </summary>
public sealed class JsonValueSerializer<T> : IAsyncSerializer<T>, IDeserializer<T>
{
    public Task<byte[]> SerializeAsync(T data, SerializationContext context) =>
        Task.FromResult(KafkaProduce.ValueOf(data, typeof(T)));

    /// <summary>
    /// A value that is not a readable <typeparamref name="T"/> gives null rather than an exception: the rider then
    /// treats the record as skipped and the endpoint's dead-letter pipe (<see cref="UnreadableFilter"/>) sends it to the
    /// dead-letter topic with its raw bytes, instead of the exception escaping below the consume pipe.
    /// </summary>
    public T Deserialize(ReadOnlySpan<byte> data, bool isNull, SerializationContext context)
    {
        if (isNull)
            return default;
        try
        {
            return JsonSerializer.Deserialize<T>(data, EventCatalog.Json);
        }
        catch (JsonException)
        {
            return default;
        }
        catch (NotSupportedException)
        {
            return default;
        }
    }
}

/// <summary>
/// A produce that Kafka neither acknowledged nor refused within <see cref="KafkaProduce.SendTimeout"/> (ARV-166). The
/// message may still be written later (at-least-once: the caller sends it again and consumers drop the duplicate).
/// </summary>
public sealed class KafkaProduceTimeoutException(string topic, TimeSpan bound, Exception inner = null)
    : TimeoutException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"Kafka neither acknowledged nor refused the message for {topic} within {bound.TotalSeconds:F0} seconds."), inner);

/// <summary>
/// Ariva's one Kafka producer in a host (ARV-167, ADR-0018 amendment): every send Ariva makes (<see cref="ISvcMessageBus"/>,
/// which is also Ingest's direct publish, the outbox relay, and the dead letters) goes through the one Confluent client
/// this class owns. MassTransit's rider only consumes.
/// <para>
/// Lifetime: built when the host starts (<see cref="KafkaProducerLifetime"/> resolves it), flushed for at most
/// <see cref="FlushTimeout"/> when the host stops, and disposed with the container. A send is handed to the client with
/// <c>Produce(topic, message, handler)</c>, which takes no cancellation token, so neither a caller who gives up nor a failed
/// send can reach the client: no send outcome disposes or replaces it (MassTransit's rider disposed a topic's producer on
/// either, and stranded every send queued on it: ARV-166). A message once handed over is the client's until its delivery
/// report, which librdkafka gives within its message timeout (30 seconds) plus the time to notice it.
/// </para>
/// <para>
/// The one exception is a fatal client error (librdkafka raises one for an idempotence refusal, such as a missing
/// IdempotentWrite ACL; its remedy is a new client). The producer is then recreated in place, at most once per
/// <c>Kafka:Producer:RecreateSeconds</c> (30) counted from the previous client's creation; until then every send fails at
/// once (<see cref="IsReady"/> is false, so the relay waits). The swap is race free: a send reads the current client once;
/// the replaced client is retired off the send path (closed to new sends, flushed for at most
/// <see cref="RetireTimeout"/>, so its own reports complete their own waiters, then whatever it still holds fails with
/// <c>Local_Destroy</c> and its disposal is bounded by <see cref="RetireTimeout"/> as well); a sender that reached it after
/// it closed fails at once. Every fatal error and every recreation is logged Critical (at most one of each per client,
/// so at most two lines per interval) and counted (<see cref="KafkaProducerMetrics"/>).
/// </para>
/// <para>
/// Bounds: each awaited send ends within <see cref="SendTimeout"/> (one timer, a backstop now that every send gets its
/// report); a caller's cancellation ends only its own wait; the client's queue is bounded
/// (<see cref="KafkaClientConfig.QueueMaxKbytes"/>, <see cref="KafkaClientConfig.QueueMaxMessages"/>) and refuses at once
/// when full (<c>Local_QueueFull</c>, which Ingest answers 503 and the relay treats as a broker failure).
/// </para>
/// <para>
/// Wire format, the rider's kept (ARV-167, checked against MassTransit 8.5.11): topic a <see cref="KafkaTopics"/> constant
/// or a dead-letter topic of one; key the UTF-8 bytes of the string key; value the JSON of the runtime type
/// (<see cref="ValueOf"/>); partition chosen by librdkafka's default partitioner from the key; headers the given ones
/// (Ariva's <see cref="OutboxHeaders"/>) as UTF-8 text, then, when the send is traced, <see cref="ActivityIdHeader"/> (the
/// send span's W3C id, which MassTransit's consume side takes as the parent of its receive span), then
/// <see cref="MessageIdHeader"/>, which MassTransit's consume side reads into <c>ConsumeContext.MessageId</c>. The rider's
/// <c>ConversationId</c>, <c>SourceAddress</c> and <c>DestinationAddress</c> are not written: nothing in Ariva reads them.
/// The send span lasts until the message's delivery report (or its failure at retirement), not only the caller's wait.
/// </para>
/// </summary>
public sealed class KafkaProduce : IDisposable
{
    /// <summary>
    /// The longest an awaited produce takes: librdkafka's message timeout (30 seconds) plus the time it takes to report
    /// a timed out message (up to its 10 second reconnect backoff; 30.6 to 39.5 seconds measured during broker outages),
    /// plus 5 seconds. Past it the wait fails with <see cref="KafkaProduceTimeoutException"/>.
    /// </summary>
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// How long the host's stop waits for messages still queued to be acknowledged. Every caller awaits its own report, so
    /// nothing acknowledged to anyone depends on the flush: an unflushed relay row is sent again, an unanswered device
    /// sends again. 5 seconds covers a healthy cluster's acknowledgements (milliseconds) and a leader election, and leaves
    /// most of the host's 30 second stop to the rest.
    /// </summary>
    public static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The bound on each step of retiring a client (its flush, then its disposal), off the send path: after a fatal error
    /// when it is replaced, and when the host is disposed.
    /// </summary>
    public static readonly TimeSpan RetireTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The header MassTransit's consume side reads into <c>ConsumeContext.MessageId</c>.</summary>
    public const string MessageIdHeader = "MessageId";

    /// <summary>
    /// The header that carries the send span's id (W3C trace context), the one MassTransit's rider wrote and its consume
    /// side reads as the parent of its receive span.
    /// </summary>
    public const string ActivityIdHeader = "MT-Activity-Id";

    /// <summary>
    /// The source of the send spans (a producer span per send, while a listener such as OpenTelemetry samples it), and the
    /// name of the producer's meter (<see cref="KafkaProducerMetrics"/>).
    /// </summary>
    public const string ActivitySourceName = "Ariva.Kafka";

    private static readonly ActivitySource Spans = new(ActivitySourceName);

    /// <summary>Topics Ariva produces: its own (never AMAN's) and the dead-letter topic of every topic.</summary>
    private static readonly FrozenSet<string> Producible = KafkaTopics.Owned
        .Concat(KafkaTopics.All.Select(KafkaTopics.DeadLetter))
        .ToFrozenSet(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    #region Fields

    private readonly Func<Action<Error>, IProducer<string, byte[]>> _clients;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly KafkaProducerMetrics _metrics;
    private readonly TimeSpan _recreateAfter;
    private readonly object _swap = new();
    private Generation _current;
    private ITimer _recreation;
    private readonly ITimer _sweep;
    private readonly object _probing = new();
    private Task<bool> _probe;
    private long _probeStarted;
    private long _sends;
    private int _clientNumbers;
    private int _started;
    private int _disposed;

    #endregion

    #region Constructors

    public KafkaProduce(KafkaSettings settings, TimeProvider timeProvider, ILogger<KafkaProduce> logger, KafkaProducerMetrics metrics = null)
        : this(ClientsFrom(KafkaClientConfig.Producer(settings), logger), timeProvider, logger,
            TimeSpan.FromSeconds(settings.Producer.RecreateSeconds), metrics)
    {
    }

    /// <summary>Real clients with the given settings (tests use a small queue).</summary>
    internal KafkaProduce(ProducerConfig config, TimeProvider timeProvider, ILogger logger)
        : this(ClientsFrom(config, logger), timeProvider, logger, TimeSpan.FromSeconds(new ProducerSettings().RecreateSeconds), null)
    {
    }

    /// <summary>Over one client the caller provides (unit tests that raise no fatal error).</summary>
    internal KafkaProduce(IProducer<string, byte[]> client, TimeProvider timeProvider, ILogger logger)
        : this(_ => client, timeProvider, logger, TimeSpan.FromSeconds(new ProducerSettings().RecreateSeconds), null)
    {
    }

    /// <summary>
    /// Over clients the caller makes: each call builds a new client that reports its errors to the given callback (unit
    /// tests make fakes; the hosts build Confluent clients).
    /// </summary>
    internal KafkaProduce(Func<Action<Error>, IProducer<string, byte[]>> clients, TimeProvider timeProvider, ILogger logger, TimeSpan recreateAfter,
        KafkaProducerMetrics metrics)
    {
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _recreateAfter = recreateAfter;
        _metrics = metrics;
        _current = NewGeneration();
        _sweep = _time.CreateTimer(static state => ((KafkaProduce)state).SweepSafely(), this, SendTimeout, SendTimeout);
    }

    #endregion

    #region Properties

    /// <summary>
    /// The host has started the producer, and it is not in the gap after a fatal client error: the relay sends only then
    /// (whatever MassTransit's bus reports, since sending does not need the bus).
    /// </summary>
    public bool IsReady => Volatile.Read(ref _started) != 0 && Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref Volatile.Read(ref _current).Fatal) is null;

    /// <summary>Sends on the current client still waiting for their delivery report.</summary>
    internal int Unreported => Volatile.Read(ref _current).Unreported.Count;

    /// <summary>The current client's number: 1 for the first, one more at each recreation.</summary>
    internal int ClientNumber => Volatile.Read(ref _current).Number;

    #endregion

    #region Methods

    /// <summary>The value's bytes: the JSON of its runtime type, as the rider's <see cref="JsonValueSerializer{T}"/> wrote it.</summary>
    public static byte[] ValueOf(object value, Type type) => JsonSerializer.SerializeToUtf8Bytes(value, type, EventCatalog.Json);

    /// <summary>
    /// Whether a send's failure says the broker or the producer is unavailable rather than that the message is wrong
    /// (ARV-165): librdkafka's message timeout, all brokers down, a transport error, a full queue, the producer's own bound,
    /// a client in its fatal gap, retired or stopping, or a report that never came. The relay does not count these against
    /// the row.
    /// </summary>
    public static bool IsBrokerUnavailable(Exception error) => error switch
    {
        KafkaProduceTimeoutException => true,
        KafkaException kafka => kafka.Error.IsFatal || kafka.Error.Code is ErrorCode.Local_MsgTimedOut or ErrorCode.Local_AllBrokersDown
            or ErrorCode.Local_Transport or ErrorCode.Local_QueueFull or ErrorCode.Local_Destroy or ErrorCode.Local_TimedOut,
        ObjectDisposedException => true,
        _ => false
    };

    /// <summary>
    /// Whether a send's failure says its topic is missing or refused to this principal (ARV-177): the broker's unknown topic,
    /// the client's (once it knows the topic is missing, every later send for it fails so at once), or the topic's
    /// authorization. The row's own failure, but one every row of the topic meets, so the relay holds the topic until that
    /// row's backoff ends.
    /// </summary>
    public static bool IsTopicRefused(Exception error) =>
        error is KafkaException { Error.Code: ErrorCode.UnknownTopicOrPart or ErrorCode.Local_UnknownTopic or ErrorCode.TopicAuthorizationFailed };

    /// <summary>Whether Ariva may produce to <paramref name="topic"/>.</summary>
    public static bool IsProducible(string topic) => topic is not null && Producible.Contains(topic);

    /// <summary>Called by <see cref="KafkaProducerLifetime"/> when the host starts.</summary>
    internal void Started() => Volatile.Write(ref _started, 1);

    /// <summary>Produces <paramref name="value"/> as its runtime type <paramref name="type"/>, keyed, with the headers and the message id.</summary>
    public Task ProduceAsync(Type type, string topic, string key, object value, Guid messageId, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(value);
        return SendAsync(topic, key, ValueOf(value, type), messageId, headers, ct);
    }

    /// <summary>
    /// Hands the message to the current client and waits for its delivery report, at most <see cref="SendTimeout"/> or
    /// until <paramref name="ct"/> is cancelled. Throws at once when the client refuses it (a full queue, a message over
    /// the size limit), when the producer is in the gap after a fatal error, and with the report's error when Kafka did not
    /// take it.
    /// </summary>
    public Task SendAsync(string topic, string key, byte[] value, Guid messageId, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        if (!IsProducible(topic))
            throw new ArgumentException($"'{topic}' is not a topic Ariva produces. Use a KafkaTopics constant.", nameof(topic));
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        // Read once: everything below is this client's, even if a recreation replaces it meanwhile.
        var generation = Volatile.Read(ref _current);
        if (Volatile.Read(ref generation.Fatal) is { } fatal)
            throw new KafkaException(fatal);

        var span = StartSpan(topic, messageId);
        var message = new Message<string, byte[]> { Key = key, Value = value, Headers = HeadersOf(messageId, headers, span?.Id) };
        var id = Interlocked.Increment(ref _sends);
        var pending = new Pending(new TaskCompletionSource<Error>(TaskCreationOptions.RunContinuationsAsynchronously), span, _time.GetTimestamp());
        generation.Unreported[id] = pending;
        // The retirement closes a client before it fails what the client holds; a send that registered after that fails here
        // instead (the dictionary's write and the close are both full fences, so one of the two sees the other).
        if (Volatile.Read(ref generation.Closed) != 0)
        {
            var retired = new Error(ErrorCode.Local_Destroy, "The producer was replaced after a fatal error; send the message again.");
            Complete(generation, id, retired);
            throw new KafkaException(retired);
        }

        try
        {
            generation.Client.Produce(topic, message, delivery => Complete(generation, id, delivery.Error));
        }
        catch (Exception e)
        {
            if (generation.Unreported.TryRemove(id, out var refused))
                EndSpan(refused.Span, e.GetType().Name);
            // A client disposed by its retirement while this send reached it: a Kafka error like every other refusal.
            if (e is ObjectDisposedException && Volatile.Read(ref generation.Closed) != 0)
                throw new KafkaException(new Error(ErrorCode.Local_Destroy, "The producer was replaced after a fatal error; send the message again."), e);
            throw;
        }

        return WaitAsync(topic, SendTimeout, _time, pending.Report.Task, ct);
    }

    /// <summary>
    /// Waits for a delivery report: one timer for the bound, linked with the caller's token, so the wait ends at the bound
    /// whatever the client does. The caller's cancellation wins when both have happened. The report itself is never
    /// faulted (it carries the error), so a report that comes after its caller stopped waiting leaves nothing unobserved.
    /// </summary>
    internal static async Task WaitAsync(string topic, TimeSpan bound, TimeProvider time, Task<Error> report, CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(bound, time ?? TimeProvider.System);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        Error error;
        try
        {
            error = await report.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException e) when (ct.IsCancellationRequested)
        {
            // The caller's own cancellation, carrying its token (MassTransit passes on only that of the message's token).
            throw new OperationCanceledException(e.Message, e, ct);
        }
        catch (OperationCanceledException e)
        {
            throw new KafkaProduceTimeoutException(topic, bound, e);
        }

        if (error.IsError)
            throw new KafkaException(error);
    }

    /// <summary>
    /// Waits for the current client to deliver what it still holds, at most <paramref name="bound"/> and never past
    /// <paramref name="ct"/>; returns how many messages were still unreported.
    /// </summary>
    internal Task<int> FlushAsync(TimeSpan bound, CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return Task.FromResult(0);
        var client = Volatile.Read(ref _current).Client;
        return Task.Run(() => Flush(client, bound, ct), CancellationToken.None);
    }

    /// <summary>
    /// Fails every send still waiting for its report on the current client (a disposed client gives no more reports, so
    /// its waiters would otherwise wait out their bound) and disposes the client within <see cref="RetireTimeout"/>.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Generation current;
        lock (_swap)
        {
            _recreation?.Dispose();
            _recreation = null;
            current = _current;
        }

        _sweep.Dispose();

        Interlocked.Exchange(ref current.Closed, 1);
        FailUnreported(current, new Error(ErrorCode.Local_Destroy, "The host stopped before Kafka reported on the message."));
        var disposal = Task.Run(current.Client.Dispose);
        Observe(disposal);
        if (!disposal.Wait(RetireTimeout))
            _logger.LogWarning("Kafka producer client {Client} did not close within {Seconds} s at stop", current.Number, RetireTimeout.TotalSeconds);
    }

    /// <summary>
    /// The client's errors. Most are transient (a broker away, a connection dropped) and librdkafka recovers by itself. A
    /// fatal one leaves the client unable to send again: sends fail at once and a recreation is scheduled, at most once per
    /// interval counted from that client's creation. A fatal error of a client already replaced changes nothing.
    /// </summary>
    private void OnError(Generation generation, Error error)
    {
        if (!error.IsFatal)
        {
            _logger.LogWarning("Kafka producer error ({Code}): {Reason}", error.Code, error.Reason);
            return;
        }

        // librdkafka raises a client's fatal error once; this keeps it once whatever the caller does.
        if (Interlocked.CompareExchange(ref generation.Fatal, error, null) is not null)
            return;
        lock (_swap)
        {
            if (Volatile.Read(ref _disposed) != 0 || !ReferenceEquals(generation, _current))
                return;
            var wait = _recreateAfter - _time.GetElapsedTime(generation.CreatedAt);
            if (wait < TimeSpan.Zero)
                wait = TimeSpan.Zero;
            _metrics?.FatalError();
            _logger.LogCritical("Kafka producer fatal error ({Code}): {Reason}. Every send fails until the producer is recreated in {Seconds:F0} s (client {Client})",
                error.Code, error.Reason, wait.TotalSeconds, generation.Number);
            ScheduleRecreation(wait);
        }
    }

    private void ScheduleRecreation(TimeSpan wait)
    {
        _recreation?.Dispose();
        _recreation = _time.CreateTimer(static state => ((KafkaProduce)state).Recreate(), this, wait, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// The recreation timer's callback (ARV-165): nothing may escape it, since an exception on a timer thread ends the
    /// process. Any failure but the process's own (out of memory) is logged Critical and the recreation is tried again
    /// after the interval; a recreation that had already replaced the client finds nothing to do then.
    /// </summary>
    internal void Recreate()
    {
        try
        {
            RecreateCore();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _logger.LogCritical(e, "Kafka producer could not be recreated after a fatal error; trying again in {Seconds:F0} s", _recreateAfter.TotalSeconds);
            lock (_swap)
            {
                if (Volatile.Read(ref _disposed) == 0)
                    ScheduleRecreation(_recreateAfter);
            }
        }
    }

    /// <summary>Replaces the client after its fatal error, then retires the old one off the send path.</summary>
    private void RecreateCore()
    {
        Generation retired, next;
        lock (_swap)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            retired = _current;
            if (Volatile.Read(ref retired.Fatal) is null)
                return;
            next = NewGeneration();
            Volatile.Write(ref _current, next);
        }

        _metrics?.Recreated();
        _logger.LogCritical("Kafka producer recreated after a fatal error (client {Client} replaces client {Retired}); messages the old client had not reported fail and their senders send them again",
            next.Number, retired.Number);
        Observe(Task.Run(() => RetireAsync(retired)));
    }

    /// <summary>
    /// Closes a replaced client to new sends, lets it report what it can (its own reports complete their own waiters),
    /// fails the rest with <c>Local_Destroy</c> and disposes it, each step within <see cref="RetireTimeout"/>.
    /// </summary>
    private async Task RetireAsync(Generation retired)
    {
        Interlocked.Exchange(ref retired.Closed, 1);
        var flush = Task.Run(() => Flush(retired.Client, RetireTimeout, CancellationToken.None));
        Observe(flush);
        try
        {
            await flush.WaitAsync(RetireTimeout + TimeSpan.FromSeconds(1), _time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Bounded: what it has not reported fails below.
        }

        FailUnreported(retired, new Error(ErrorCode.Local_Destroy, "The producer was replaced after a fatal error before Kafka reported on the message."));
        var disposal = Task.Run(retired.Client.Dispose);
        Observe(disposal);
        try
        {
            await disposal.WaitAsync(RetireTimeout, _time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Kafka producer client {Client} did not close within {Seconds} s after it was replaced", retired.Number, RetireTimeout.TotalSeconds);
        }
    }

    /// <summary>
    /// The sweep's timer callback: every <see cref="SendTimeout"/>, ends the waiters whose report is more than twice that
    /// late (ARV-165). librdkafka reports every message it was given (delivered, failed, or purged at its message timeout),
    /// so this covers only a report that never comes, which would otherwise keep its entry, its span and its report task
    /// for the life of the host. Nothing may escape a timer callback.
    /// </summary>
    private void SweepSafely()
    {
        try
        {
            Sweep();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _logger.LogError(e, "Kafka producer sweep of unreported messages failed; trying again in {Seconds:F0} s", SendTimeout.TotalSeconds);
        }
    }

    /// <summary>Ends the waiters of the current client whose report is more than twice <see cref="SendTimeout"/> late; returns how many.</summary>
    internal int Sweep()
    {
        var generation = Volatile.Read(ref _current);
        var swept = 0;
        foreach (var (id, pending) in generation.Unreported)
        {
            if (_time.GetElapsedTime(pending.HandedOverAt) <= 2 * SendTimeout)
                continue;
            if (Complete(generation, id, new Error(ErrorCode.Local_TimedOut, "Kafka gave no delivery report for the message; its sender sends it again.")))
                swept++;
        }

        if (swept > 0)
            _logger.LogError("Kafka producer ended {Count} messages that had no delivery report after {Seconds:F0} s (client {Client}); their senders send them again",
                swept, 2 * SendTimeout.TotalSeconds, generation.Number);
        return swept;
    }

    /// <summary>
    /// Whether a broker describes the cluster through the current client within <paramref name="timeout"/> (ARV-165): a
    /// dependent admin client on the producer's handle, so the probe opens no connection of its own and sees what the
    /// producer sees. The request names no topic (ARV-177): DescribeCluster asks for the brokers and the controller only,
    /// so its answer does not grow with the cluster's topics (a metadata request for every topic did), and nothing in it can
    /// create a topic (a metadata request naming one could, on a broker with <c>auto.create.topics.enable</c>). At most one
    /// probe runs at a time, and a result is reused for a second, so a burst of readiness requests asks the broker once.
    /// Never throws: a broker that does not answer, a client in its fatal gap or a disposed producer is false.
    /// </summary>
    public Task<bool> BrokersAnswerAsync(TimeSpan timeout)
    {
        if (!IsReady)
            return Task.FromResult(false);
        lock (_probing)
        {
            if (_probe is { } running && (!running.IsCompleted || _time.GetElapsedTime(_probeStarted) < ProbeReuse))
                return running;
            var client = Volatile.Read(ref _current).Client;
            _probeStarted = _time.GetTimestamp();
            _probe = Task.Run(() => ProbeAsync(client, timeout));
            return _probe;
        }
    }

    /// <summary>How long a probe's result is reused.</summary>
    private static readonly TimeSpan ProbeReuse = TimeSpan.FromSeconds(1);

    /// <summary>How much longer than its request timeout a probe may take before it ends as false (ARV-245).</summary>
    internal static readonly TimeSpan ProbeSlack = TimeSpan.FromSeconds(1);

    /// <summary>The cluster description a probe asks for (unit tests give their own); null: the client's DescribeCluster.</summary>
    internal Func<IProducer<string, byte[]>, TimeSpan, Task<bool>> DescribeCluster { get; init; }

    /// <summary>
    /// One probe. Bounded here as well as by the request's timeout (ARV-245): the probe's task is shared and kept, and since
    /// ARV-245 the outbox relay waits on it before each pass after a broker failure, so a request the client never ends (its
    /// client retired under it after a fatal error, say) must not keep that task from ending. It ends as false
    /// <see cref="ProbeSlack"/> after the timeout, and the next probe asks the current client afresh.
    /// </summary>
    private async Task<bool> ProbeAsync(IProducer<string, byte[]> client, TimeSpan timeout)
    {
        try
        {
            // One bound for the client's description and a test's alike, so the tests hold the bound the hosts run with.
            using var admin = DescribeCluster is null ? new DependentAdminClientBuilder(client.Handle).Build() : null;
            var describing = DescribeCluster?.Invoke(client, timeout) ?? DescribeAsync(admin, timeout);
            return await describing.WaitAsync(timeout + ProbeSlack, _time).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _logger.LogDebug("Kafka brokers did not describe the cluster within {Seconds:F0} s ({Error})", timeout.TotalSeconds, e.GetType().Name);
            return false;
        }
    }

    private static async Task<bool> DescribeAsync(IAdminClient admin, TimeSpan timeout)
    {
        var cluster = await admin.DescribeClusterAsync(new Confluent.Kafka.Admin.DescribeClusterOptions { RequestTimeout = timeout }).ConfigureAwait(false);
        return cluster.Nodes.Count > 0;
    }

    private Generation NewGeneration()
    {
        var generation = new Generation(Interlocked.Increment(ref _clientNumbers), _time.GetTimestamp());
        generation.Client = _clients(error => OnError(generation, error));
        return generation;
    }

    private static Func<Action<Error>, IProducer<string, byte[]>> ClientsFrom(ProducerConfig config, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        var log = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        return onError => new ProducerBuilder<string, byte[]>(config)
            .SetErrorHandler((_, error) => onError(error))
            .SetLogHandler((_, message) => log.LogDebug("Kafka producer {Facility}: {Message}", message.Facility, message.Message))
            .Build();
    }

    private int Flush(IProducer<string, byte[]> client, TimeSpan bound, CancellationToken ct)
    {
        var started = _time.GetTimestamp();
        int left;
        do
        {
            left = client.Flush(TimeSpan.FromMilliseconds(100));
        }
        while (left > 0 && _time.GetElapsedTime(started) < bound && !ct.IsCancellationRequested);

        return left;
    }

    private static bool Complete(Generation generation, long id, Error error)
    {
        if (!generation.Unreported.TryRemove(id, out var pending))
            return false;
        EndSpan(pending.Span, error.IsError ? error.Code.ToString() : null);
        pending.Report.TrySetResult(error);
        return true;
    }

    private static void FailUnreported(Generation generation, Error error)
    {
        foreach (var id in generation.Unreported.Keys)
            Complete(generation, id, error);
    }

    /// <summary>A background step's outcome is observed where it ends, so a late failure raises no unobserved task exception.</summary>
    private static void Observe(Task task) =>
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    /// <summary>
    /// The send's producer span, a child of the caller's current span, started without becoming the current span: the send
    /// returns its task before the span ends (at the message's report), and the caller's current span must stay its own.
    /// </summary>
    private static Activity StartSpan(string topic, Guid messageId)
    {
        var current = Activity.Current;
        var span = Spans.CreateActivity(topic + " send", ActivityKind.Producer);
        if (span is null)
            return null;
        span.SetTag("messaging.system", "kafka");
        span.SetTag("messaging.operation", "send");
        span.SetTag("messaging.destination.name", topic);
        if (messageId != Guid.Empty)
            span.SetTag("messaging.message.id", messageId.ToString("D"));
        span.Start();
        Activity.Current = current;
        return span;
    }

    private static void EndSpan(Activity span, string error)
    {
        if (span is null)
            return;
        if (error is not null)
            span.SetStatus(ActivityStatusCode.Error, error);
        span.Stop();
    }

    internal static Headers HeadersOf(Guid messageId, IReadOnlyDictionary<string, string> headers, string spanId = null)
    {
        var result = new Headers();
        foreach (var (name, value) in headers ?? NoHeaders)
        {
            if (value is not null && !string.Equals(name, MessageIdHeader, StringComparison.Ordinal) && !string.Equals(name, ActivityIdHeader, StringComparison.Ordinal))
                result.Add(name, Encoding.UTF8.GetBytes(value));
        }

        if (spanId is not null)
            result.Add(ActivityIdHeader, Encoding.UTF8.GetBytes(spanId));
        if (messageId != Guid.Empty)
            result.Add(MessageIdHeader, Encoding.UTF8.GetBytes(messageId.ToString("D")));
        return result;
    }

    #endregion

    /// <summary>One client and the sends it has not reported on yet.</summary>
    private sealed class Generation(int number, long createdAt)
    {
        public int Number { get; } = number;
        public long CreatedAt { get; } = createdAt;
        public IProducer<string, byte[]> Client { get; set; }
        public ConcurrentDictionary<long, Pending> Unreported { get; } = new();
        public Error Fatal;
        public int Closed;
    }

    private sealed record Pending(TaskCompletionSource<Error> Report, Activity Span, long HandedOverAt);
}

/// <summary>
/// Builds the host's producer when the host starts and flushes it for at most <see cref="KafkaProduce.FlushTimeout"/> when
/// it stops (ARV-167). Registered before the relay and the bus, so it starts before them and stops after them. Hosted
/// services a host registers before its messaging stop after the flush: their late sends still work until the container
/// disposes the producer, which fails what is left with <c>Local_Destroy</c> (senders send again).
/// </summary>
internal sealed class KafkaProducerLifetime(KafkaProduce produce, ILogger<KafkaProducerLifetime> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        produce.Started();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var left = await produce.FlushAsync(KafkaProduce.FlushTimeout, cancellationToken);
        if (left > 0)
            logger.LogWarning("Kafka producer stopped with {Count} messages unacknowledged; their senders send them again", left);
    }
}

/// <summary>
/// The relay's transport: the outbox row's payload, read back as its event type, produced keyed to its topic. A dead
/// letter kept at a stop (ARV-171, type <see cref="DeadLetters.OutboxMessageType"/>) is read back as a
/// <see cref="DeadLetter"/> and goes to the dead-letter topic of its source topic, which must be the row's topic. The relay
/// waits while the producer is not ready (not started yet, or in the gap after a fatal client error); since ARV-167 it no
/// longer waits for MassTransit's bus, which sending does not need.
/// </summary>
internal sealed class KafkaOutboxTransport(KafkaProduce produce, EventCatalog catalog) : IOutboxTransport
{
    public bool IsReady => produce.IsReady;

    public bool IsBrokerUnavailable(Exception error) => KafkaProduce.IsBrokerUnavailable(error);

    public bool IsTopicRefused(Exception error) => KafkaProduce.IsTopicRefused(error);

    /// <summary>
    /// The producer's cached probe, as the readiness check asks it (ARV-245): within <see cref="KafkaReadiness.ProbeTimeout"/>
    /// and a second more in case the client does not keep to it; a probe that does not end then reads as false.
    /// </summary>
    public async Task<bool> BrokersAnswerAsync(CancellationToken ct)
    {
        try
        {
            return await produce.BrokersAnswerAsync(KafkaReadiness.ProbeTimeout).WaitAsync(KafkaReadiness.ProbeTimeout + TimeSpan.FromSeconds(1), ct);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public Task SendAsync(OutboxEnvelope message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (string.Equals(message.MessageType, DeadLetters.OutboxMessageType, StringComparison.Ordinal))
            return SendDeadLetterAsync(message, ct);
        if (!catalog.TryResolve(message.MessageType, out var type))
            throw new UnknownEventTypeException(message.MessageType);
        // The row's topic is its event type's topic (ARV-167): a row written by hand, or damaged, cannot send an event elsewhere.
        if (!string.Equals(message.Topic, catalog.TopicOf(type), StringComparison.Ordinal))
            throw new InvalidOperationException($"Outbox row {message.Seq} names a topic that is not its event type's topic.");
        var value = JsonSerializer.Deserialize(message.Payload, type, EventCatalog.Json)
            ?? throw new InvalidOperationException($"Outbox row {message.Seq} has an empty payload.");
        return produce.ProduceAsync(type, message.Topic, message.Key, value, message.Id, message.Headers, ct);
    }

    /// <summary>
    /// A kept dead letter: produced as the direct sink produces it, to the topic the row names only when that is the
    /// dead-letter topic of the letter's source topic, both from <see cref="KafkaTopics"/>. A row that is not (written by
    /// hand, or damaged) fails like an unreadable row: its key waits with its backoff and the relay logs it.
    /// </summary>
    private Task SendDeadLetterAsync(OutboxEnvelope message, CancellationToken ct)
    {
        var letter = JsonSerializer.Deserialize<DeadLetter>(message.Payload, EventCatalog.Json)
            ?? throw new InvalidOperationException($"Outbox row {message.Seq} has an empty dead letter.");
        if (!KafkaTopics.IsKnown(letter.Topic) || !string.Equals(message.Topic, KafkaTopics.DeadLetter(letter.Topic), StringComparison.Ordinal))
            throw new InvalidOperationException($"Outbox row {message.Seq} is not a dead letter for the dead-letter topic of a known topic.");
        return produce.ProduceAsync(typeof(DeadLetter), message.Topic, message.Key, letter, message.Id, message.Headers, ct);
    }
}

/// <summary><see cref="ISvcMessageBus"/> on Ariva's producer: keyed by the event's partition key, on the event's topic.</summary>
internal sealed class KafkaMessageBus(KafkaProduce produce, EventCatalog catalog) : ISvcMessageBus
{
    public Task PublishAsync<TEvent>(TEvent message, CancellationToken ct = default) where TEvent : class, IEvent
    {
        ArgumentNullException.ThrowIfNull(message);
        var key = message.GetPartitionKey();
        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException($"{typeof(TEvent).Name} {message.Id} has no partition key; every event is keyed (ADR-0018).");
        var type = message.GetType();
        return produce.ProduceAsync(type, catalog.TopicOf(type), key, message, message.Id, OutboxHeaders.For(message), ct);
    }
}

/// <summary>When Kafka is off (unit tests, a laptop without a broker) publishing fails loudly instead of dropping events.</summary>
internal sealed class DisabledMessageBus : ISvcMessageBus
{
    public Task PublishAsync<TEvent>(TEvent message, CancellationToken ct = default) where TEvent : class, IEvent =>
        throw new InvalidOperationException("Kafka is disabled (Kafka:Enabled false); this host cannot publish events.");
}

/// <summary>
/// Dead letters on Ariva's producer, keyed by the source message's key so a replay keeps its partition. A letter flagged in
/// <see cref="DeadLetters.TextReplacedHeader"/> (ARV-175) carries the key with its NUL characters or lone surrogates
/// replaced, so its partition can differ: a replay of it reads the record's key from the source topic.
/// </summary>
internal sealed class KafkaDeadLetterSink(KafkaProduce produce) : IDeadLetterSink
{
    public Task SendAsync(DeadLetter letter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(letter);
        return produce.ProduceAsync(typeof(DeadLetter), DeadLetters.TopicFor(letter), string.IsNullOrEmpty(letter.Key) ? "none" : letter.Key, letter,
            Guid.CreateVersion7(), new Dictionary<string, string>(), ct);
    }
}
