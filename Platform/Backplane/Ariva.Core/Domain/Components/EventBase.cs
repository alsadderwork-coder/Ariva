namespace Ariva.Core.Domain.Components;

/// <summary>
/// Base class for events, ported from AMAN's EventBase. Differences: ids are version 7 GUIDs (time ordered, so
/// outbox rows and logs sort by creation), and the partition key is abstract because ADR-0018 forbids keyless events.
/// </summary>
public abstract class EventBase : IEvent
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>When the fact happened, in UTC (event time, not the time Ariva received it).</summary>
    public DateTime OccurredOn { get; set; } = DateTime.UtcNow;

    public string EventType => GetType().Name;

    public int Version { get; set; } = 1;

    /// <summary>Ties every event of one request or workflow together for tracing.</summary>
    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>The id of the event or command that caused this one.</summary>
    public string CausationId { get; set; } = string.Empty;

    /// <summary>The Kafka key: the zone, desk, gate, device or site this event belongs to.</summary>
    public abstract string GetPartitionKey();
}
