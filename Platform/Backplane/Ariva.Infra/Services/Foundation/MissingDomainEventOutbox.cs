namespace Ariva.Infra.Services.Foundation;

/// <summary>
/// Fallback <see cref="IDomainEventOutbox"/> for a composition without messaging (tools, tests); every host registers the
/// NHibernate outbox through <c>AddArivaMessaging</c> (ARV-020). It fails the commit when an entity raised domain
/// events: dropping them silently would lose facts other services depend on.
/// </summary>
internal sealed class MissingDomainEventOutbox : IDomainEventOutbox
{
    public Task WriteAsync(IStorageProvider storage, IReadOnlyList<IEvent> events, CancellationToken ct = default) =>
        events is { Count: > 0 }
            ? throw new InvalidOperationException(
                $"{events.Count} domain event(s) ({string.Join(", ", events.Select(e => e.EventType).Distinct())}) were raised but no outbox is registered (AddArivaMessaging, ARV-020).")
            : Task.CompletedTask;
}
