using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Ariva.Infra.Messaging.Outbox;
using Confluent.Kafka;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.Infra.Messaging.Kafka;

/// <summary>
/// Values on the wire are plain JSON documents (<see cref="EventCatalog.Json"/>), not MassTransit envelopes, so the raw
/// Confluent consumer in Ariva.Api.Stream and the Python forecasting worker read them as they are.
/// </summary>
public sealed class JsonValueSerializer<T> : IAsyncSerializer<T>, IDeserializer<T>
{
    public Task<byte[]> SerializeAsync(T data, SerializationContext context) =>
        Task.FromResult(JsonSerializer.SerializeToUtf8Bytes(data, EventCatalog.Json));

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
/// Produces a value of a type known only at run time through the rider's registered producer for its topic, keyed and
/// with the outbox headers. One compiled delegate per type.
/// <para>
/// MassTransit registers the producer provider per scope and it needs the started rider, so it is resolved in a scope
/// of its own for each produce, never at construction (hosted services are built before the bus starts).
/// </para>
/// <para>
/// ARV-166: every produce ends within <see cref="SendTimeout"/>, whatever the client does. The rider keeps one producer
/// per topic and disposes it when any send on it fails or is cancelled (MassTransit's <c>PipeContextSupervisor</c>
/// recreates a faulted context); a disposed producer that had reported a local error serves no more delivery reports,
/// so a produce that was waiting behind the failed one would otherwise wait for ever. Two rules keep that contained.
/// The produce gets a token of its own that cancels at the bound, never the caller's: a caller that gives up (a device
/// that drops its request, the relay's pass bound, a stopping host) stops waiting at once while its produce carries on
/// and ends by its report or the bound, so a caller's cancellation never disposes a producer that other sends are using.
/// The bound's own cancellation releases a produce whose report will never come; on a producer that is still live (no
/// report within 45 seconds, rare: 30.6 to 39.5 seconds measured) it disposes that producer like any failed send, which
/// is accepted because every send it strands is bounded the same way. And the wait itself ends at the bound even if the
/// token is ignored somewhere below. ARV-167 removes the cause (Ariva's own producer for sends).
/// </para>
/// </summary>
public sealed class KafkaProduce(IServiceScopeFactory scopes, TimeProvider timeProvider)
{
    /// <summary>
    /// The longest an awaited produce takes: librdkafka's message timeout (30 seconds) plus the time it takes to report
    /// a timed out message (up to its 10 second reconnect backoff; 30.6 to 39.5 seconds measured during broker outages),
    /// plus 5 seconds. Past it the produce fails with <see cref="KafkaProduceTimeoutException"/>.
    /// </summary>
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(45);

    private static readonly MethodInfo Generic = typeof(KafkaProduce).GetMethod(nameof(ProduceTyped), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly ConcurrentDictionary<Type, Func<ITopicProducerProvider, string, string, object, Guid, IReadOnlyDictionary<string, string>, CancellationToken, Task>> Delegates = new();

    public Task ProduceAsync(Type type, string topic, string key, object value, Guid messageId, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(type);
        var produce = Delegates.GetOrAdd(type, t => Generic.MakeGenericMethod(t)
            .CreateDelegate<Func<ITopicProducerProvider, string, string, object, Guid, IReadOnlyDictionary<string, string>, CancellationToken, Task>>());
        return BoundedAsync(topic, SendTimeout, timeProvider, async token =>
        {
            await using var scope = scopes.CreateAsyncScope();
            await produce(scope.ServiceProvider.GetRequiredService<ITopicProducerProvider>(), topic, key, value, messageId, headers, token);
        }, ct);
    }

    /// <summary>
    /// Runs <paramref name="produce"/> with a token of its own that cancels after <paramref name="bound"/>, and waits for it
    /// at most that long or until <paramref name="ct"/> is cancelled. The caller's token never reaches the produce; a produce
    /// the caller stopped waiting for runs on, and its outcome is observed and its token released when it ends.
    /// </summary>
    internal static async Task BoundedAsync(string topic, TimeSpan bound, TimeProvider time, Func<CancellationToken, Task> produce, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(produce);
        ct.ThrowIfCancellationRequested();
        var deadline = new CancellationTokenSource(bound, time ?? TimeProvider.System);
        Task send;
        try
        {
            send = produce(deadline.Token);
        }
        catch
        {
            deadline.Dispose();
            throw;
        }

        // Released only when the produce has ended (its timer must still cancel a produce the caller stopped waiting for),
        // and its outcome observed then, so a produce nobody awaits any more raises no unobserved task exception.
        _ = send.ContinueWith(static (task, state) =>
        {
            _ = task.Exception;
            ((CancellationTokenSource)state).Dispose();
        }, deadline, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        try
        {
            await send.WaitAsync(bound, time ?? TimeProvider.System, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException e)
        {
            // The produce's own token cancelled it at the bound.
            throw new KafkaProduceTimeoutException(topic, bound, e);
        }
        catch (TimeoutException e) when (e is not KafkaProduceTimeoutException)
        {
            // The wait reached the bound before the produce ended.
            throw new KafkaProduceTimeoutException(topic, bound, e);
        }
    }

    private static Task ProduceTyped<T>(ITopicProducerProvider producers, string topic, string key, object value, Guid messageId,
        IReadOnlyDictionary<string, string> headers, CancellationToken ct) where T : class
    {
        var producer = producers.GetProducer<string, T>(new Uri($"topic:{topic}"));
        return producer.Produce(key, (T)value, Pipe.Execute<KafkaSendContext<string, T>>(context =>
        {
            if (messageId != Guid.Empty)
                context.MessageId = messageId;
            foreach (var (name, header) in headers ?? new Dictionary<string, string>())
                context.Headers.Set(name, header);
        }), ct);
    }
}

/// <summary>The relay's transport: the outbox row's payload, read back as its event type, produced keyed to its topic.</summary>
internal sealed class MassTransitOutboxTransport(KafkaProduce produce, EventCatalog catalog, IBusControl bus) : IOutboxTransport
{
    public bool IsReady => bus.CheckHealth().Status == BusHealthStatus.Healthy;

    public Task SendAsync(OutboxEnvelope message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!catalog.TryResolve(message.MessageType, out var type))
            throw new UnknownEventTypeException(message.MessageType);
        var value = JsonSerializer.Deserialize(message.Payload, type, EventCatalog.Json)
            ?? throw new InvalidOperationException($"Outbox row {message.Seq} has an empty payload.");
        return produce.ProduceAsync(type, message.Topic, message.Key, value, message.Id, message.Headers, ct);
    }
}

/// <summary><see cref="ISvcMessageBus"/> on the rider: keyed by the event's partition key, on the event's topic.</summary>
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

/// <summary>Dead letters on the rider, keyed by the source message's key so a replay keeps its partition.</summary>
internal sealed class KafkaDeadLetterSink(KafkaProduce produce) : IDeadLetterSink
{
    public Task SendAsync(DeadLetter letter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(letter);
        return produce.ProduceAsync(typeof(DeadLetter), DeadLetters.TopicFor(letter), string.IsNullOrEmpty(letter.Key) ? "none" : letter.Key, letter,
            Guid.CreateVersion7(), new Dictionary<string, string>(), ct);
    }
}
