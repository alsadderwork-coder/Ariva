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
/// Produces a value of a type known only at run time through the rider's registered producer for its topic, keyed and
/// with the outbox headers. One compiled delegate per type.
/// </summary>
/// <para>
/// MassTransit registers the producer provider per scope and it needs the started rider, so it is resolved in a scope
/// of its own for each produce, never at construction (hosted services are built before the bus starts).
/// </para>
public sealed class KafkaProduce(IServiceScopeFactory scopes)
{
    private static readonly MethodInfo Generic = typeof(KafkaProduce).GetMethod(nameof(ProduceTyped), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly ConcurrentDictionary<Type, Func<ITopicProducerProvider, string, string, object, Guid, IReadOnlyDictionary<string, string>, CancellationToken, Task>> Delegates = new();

    public async Task ProduceAsync(Type type, string topic, string key, object value, Guid messageId, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(type);
        var produce = Delegates.GetOrAdd(type, t => Generic.MakeGenericMethod(t)
            .CreateDelegate<Func<ITopicProducerProvider, string, string, object, Guid, IReadOnlyDictionary<string, string>, CancellationToken, Task>>());
        await using var scope = scopes.CreateAsyncScope();
        await produce(scope.ServiceProvider.GetRequiredService<ITopicProducerProvider>(), topic, key, value, messageId, headers, ct);
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
