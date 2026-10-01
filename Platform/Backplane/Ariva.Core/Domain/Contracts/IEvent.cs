namespace Ariva.Core.Domain.Contracts;

/// <summary>
/// One event shape for domain and integration events (AMAN's IEvent), plus the Kafka key: ADR-0018 requires every
/// event to name its partition key (zone, desk, gate, device or site id), so per-entity order holds.
/// </summary>
public interface IEvent
{
    Guid Id { get; set; }
    DateTime OccurredOn { get; set; }
    string EventType { get; }
    int Version { get; set; }
    string CorrelationId { get; set; }
    string CausationId { get; set; }
    string GetPartitionKey();
}

/// <summary>
/// An entity that raises domain events. They are collected during the transaction and dispatched through the outbox
/// after commit (ADR-0018).
/// </summary>
public interface IHasDomainEvents
{
    IReadOnlyList<IEvent> DomainEvents { get; }
    void RaiseDomainEvent(IEvent domainEvent);
    void ClearDomainEvents();

    /// <summary>Returns the collected events and clears them, so a dispatcher never sends one twice.</summary>
    IReadOnlyList<IEvent> DequeueDomainEvents();
}
