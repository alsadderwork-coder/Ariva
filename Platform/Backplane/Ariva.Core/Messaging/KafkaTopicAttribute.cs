namespace Ariva.Core.Messaging;

/// <summary>
/// Names the topic an event is published to (ADR-0018, ADR-0019). Every event an aggregate raises or a host publishes
/// carries one, with a <see cref="KafkaTopics"/> constant; the outbox refuses an event without it rather than drop it.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class KafkaTopicAttribute(string topic) : Attribute
{
    public string Topic { get; } = topic;
}
