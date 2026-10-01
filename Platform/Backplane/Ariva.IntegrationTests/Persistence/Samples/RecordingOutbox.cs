using Ariva.Core.Domain.Contracts;
using Ariva.Core.Services;

namespace Ariva.IntegrationTests.Persistence.Samples;

/// <summary>
/// Stand-in for the ARV-020 outbox: saves one <see cref="SampleOutboxRow"/> per event through the unit of work's own
/// storage provider, so the rows commit or roll back with the entity changes. Can be told to fail.
/// </summary>
public sealed class RecordingOutbox : IDomainEventOutbox
{
    public bool FailNextWrite { get; set; }

    public List<IEvent> Written { get; } = [];

    public async Task WriteAsync(IStorageProvider storage, IReadOnlyList<IEvent> events, CancellationToken ct = default)
    {
        if (FailNextWrite)
        {
            FailNextWrite = false;
            throw new InvalidOperationException("Simulated outbox failure.");
        }

        foreach (var domainEvent in events)
        {
            await storage.SaveAsync(new SampleOutboxRow
            {
                EventId = domainEvent.Id,
                EventType = domainEvent.EventType,
                PartitionKey = domainEvent.GetPartitionKey()
            }, ct);
            Written.Add(domainEvent);
        }
    }
}
