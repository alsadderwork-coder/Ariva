namespace Ariva.Infra.Services.Foundation;

/// <summary>
/// Default <see cref="IDomainEventOutbox"/> until the Kafka outbox lands (ARV-020). It fails the commit when an entity
/// raised domain events: dropping them silently would lose facts other services depend on.
/// </summary>
internal sealed class MissingDomainEventOutbox : IDomainEventOutbox
{
    public Task WriteAsync(IStorageProvider storage, IReadOnlyList<IEvent> events, CancellationToken ct = default) =>
        events is { Count: > 0 }
            ? throw new InvalidOperationException(
                $"{events.Count} domain event(s) ({string.Join(", ", events.Select(e => e.EventType).Distinct())}) were raised but no outbox is registered (ARV-020).")
            : Task.CompletedTask;
}
