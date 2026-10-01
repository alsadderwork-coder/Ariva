namespace Ariva.Core.Services;

/// <summary>
/// One unit of work per request or message scope, ported from AMAN. A command marks the unit to commit
/// (<see cref="PromiseToCommit"/>); <see cref="EndAsync"/> then commits or rolls back. Differences from AMAN:
/// domain events are handed to <see cref="IDomainEventOutbox"/> inside the transaction, before commit, instead of
/// being published after commit where a crash between the two lost them (ADR-0018); there is no synchronous
/// End or Rollback (AMAN blocked on Task.Run, a thread pool starvation risk).
/// </summary>
public interface IUnitOfWork : ISvcScoped, IAsyncDisposable
{
    /// <summary>Identifies the unit in logs.</summary>
    Guid Id { get; }

    IStorageProvider StorageProvider { get; }

    bool IsToBeCommitted { get; }

    /// <summary>Commits when a command promised to, otherwise rolls back. A failed commit rolls back and rethrows.</summary>
    Task EndAsync(CancellationToken ct = default);

    Task RollbackAsync(CancellationToken ct = default);

    /// <summary>Runs after a successful commit (cache eviction, notifications). A failure is logged, not rethrown.</summary>
    void RegisterPostCommitAction(Func<Task> action);

    /// <summary>Runs after a rollback. A failure is logged, not rethrown.</summary>
    void RegisterPostRollbackAction(Func<Task> action);

    void PromiseToCommit();

    void PromiseNotToCommit();
}

/// <summary>
/// Receives the domain events of a unit of work inside its transaction (the transactional outbox of ADR-0018,
/// implemented in ARV-020). Writing through the given storage provider puts the events in the same transaction as
/// the entity changes, so both commit or neither does.
/// </summary>
public interface IDomainEventOutbox
{
    Task WriteAsync(IStorageProvider storage, IReadOnlyList<IEvent> events, CancellationToken ct = default);
}
