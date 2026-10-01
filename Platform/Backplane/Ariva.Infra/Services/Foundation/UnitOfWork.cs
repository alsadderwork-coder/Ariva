using System.Data;

namespace Ariva.Infra.Services.Foundation;

/// <summary>
/// Scoped unit of work, ported from AMAN. Commit order: flush, collect domain events from tracked entities, hand them
/// to <see cref="IDomainEventOutbox"/> (same transaction), commit, then run post-commit actions. A failed commit rolls
/// back, runs post-rollback actions and rethrows. Disposing a unit that was never ended ends it, as in AMAN (SignalR
/// hub calls have no explicit end).
/// </summary>
internal sealed class UnitOfWork(IStorageProvider storageProvider, IDomainEventOutbox outbox, ILogger<UnitOfWork> logger) : IUnitOfWork
{
    private readonly List<Func<Task>> _postCommitActions = [];
    private readonly List<Func<Task>> _postRollbackActions = [];
    private bool _ended;

    public Guid Id { get; } = Guid.CreateVersion7();

    public IStorageProvider StorageProvider { get; } = storageProvider;

    public bool IsToBeCommitted { get; private set; }

    public void PromiseToCommit() => IsToBeCommitted = true;

    public void PromiseNotToCommit() => IsToBeCommitted = false;

    public void RegisterPostCommitAction(Func<Task> action)
    {
        if (action is not null)
            _postCommitActions.Add(action);
    }

    public void RegisterPostRollbackAction(Func<Task> action)
    {
        if (action is not null)
            _postRollbackActions.Add(action);
    }

    public async Task EndAsync(CancellationToken ct = default)
    {
        _ended = true;

        if (!IsToBeCommitted)
        {
            await RollbackAsync(ct);
            return;
        }

        try
        {
            await CommitAsync(ct);
        }
        catch (Exception)
        {
            await RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            IsToBeCommitted = false;
        }
    }

    public async Task RollbackAsync(CancellationToken ct = default)
    {
        IsToBeCommitted = false;
        if (StorageProvider.IsTransactionActive())
        {
            logger.LogDebug("Unit of work {UnitOfWorkId} rolling back", Id);
            await StorageProvider.RollbackTransactionAsync(ct);
        }

        await RunAsync(_postRollbackActions, "post-rollback");
    }

    private async Task CommitAsync(CancellationToken ct)
    {
        if (!StorageProvider.IsTransactionActive())
            StorageProvider.BeginTransaction(IsolationLevel.ReadCommitted);

        // Flush first: entity changes reach the database and the session knows every entity that raised events.
        await StorageProvider.FlushAsync(ct);

        var events = DequeueDomainEvents();
        if (events.Count > 0)
        {
            await outbox.WriteAsync(StorageProvider, events, ct);
            await StorageProvider.FlushAsync(ct);
        }

        await StorageProvider.CommitTransactionAsync(ct);
        logger.LogDebug("Unit of work {UnitOfWorkId} committed with {EventCount} domain events", Id, events.Count);

        await RunAsync(_postCommitActions, "post-commit");
    }

    private List<IEvent> DequeueDomainEvents() =>
        StorageProvider.GetEntitiesWithDomainEvents()
            .SelectMany(entity => entity.DequeueDomainEvents())
            .ToList();

    /// <summary>Side effects after the decision: a failure is logged and the remaining actions still run.</summary>
    private async Task RunAsync(List<Func<Task>> actions, string stage)
    {
        foreach (var action in actions)
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unit of work {UnitOfWorkId} {Stage} action failed", Id, stage);
            }
        }

        actions.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (!_ended)
            await EndAsync();

        (StorageProvider as IDisposable)?.Dispose();
    }
}
