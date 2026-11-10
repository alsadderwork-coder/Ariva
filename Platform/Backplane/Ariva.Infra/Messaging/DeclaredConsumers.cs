namespace Ariva.Infra.Messaging;

/// <summary>
/// What a host consumes, as its composition root declares it (ARV-083b): each MassTransit topic endpoint
/// (<c>Consume&lt;TMessage, TConsumer&gt;</c>) and each topic the Stream engine reads raw (<c>StreamFrom</c>), with its consumer
/// group. Registered by AddArivaMessaging whether Kafka is on or not, so KafkaAccessTests holds each host's principal
/// (<see cref="KafkaAccess"/>) to what the host itself declares.
/// </summary>
/// <param name="Consumers">The topics and groups, in the order they were declared.</param>
public sealed record DeclaredConsumers(IReadOnlyList<DeclaredConsumer> Consumers);

/// <summary>A consumed topic and its group; <paramref name="Raw"/>: read by the Stream engine's own consumer, not a MassTransit endpoint.</summary>
public sealed record DeclaredConsumer(string Topic, string Group, bool Raw);
