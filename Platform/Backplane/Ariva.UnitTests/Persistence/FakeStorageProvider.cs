using System.Data;
using System.Diagnostics.CodeAnalysis;
using Ariva.Core.Domain.Contracts;
using Ariva.Core.Services;

namespace Ariva.UnitTests.Persistence;

/// <summary>Records the calls the unit of work makes, in order. Only the members the unit of work uses do work.</summary>
internal sealed class FakeStorageProvider : IStorageProvider
{
    private bool _transactionActive;

    public List<string> Calls { get; } = [];

    public List<IHasDomainEvents> TrackedEntities { get; } = [];

    public Exception CommitFailure { get; set; }

    public void BeginTransaction(IsolationLevel isolationLevel)
    {
        Calls.Add("begin");
        _transactionActive = true;
    }

    public bool IsTransactionActive() => _transactionActive;

    public Task CommitTransactionAsync(CancellationToken ct = default)
    {
        Calls.Add("commit");
        if (CommitFailure is not null)
            throw CommitFailure;
        _transactionActive = false;
        return Task.CompletedTask;
    }

    public Task RollbackTransactionAsync(CancellationToken ct = default)
    {
        Calls.Add("rollback");
        _transactionActive = false;
        return Task.CompletedTask;
    }

    public Task FlushAsync(CancellationToken ct = default)
    {
        Calls.Add("flush");
        return Task.CompletedTask;
    }

    public IReadOnlyList<IHasDomainEvents> GetEntitiesWithDomainEvents() =>
        TrackedEntities.Where(entity => entity.DomainEvents.Count > 0).ToList();

    public IQueryable<T> Query<T>() where T : class, IDomain => throw new NotSupportedException();
    public Task<List<T>> ExecuteSqlAsync<T>([ConstantExpected] string sql, IReadOnlyDictionary<string, object> parameters = null, CancellationToken ct = default) where T : class, new() => throw new NotSupportedException();
    public Task<List<T>> ListAsync<T>(CancellationToken ct = default) where T : class, IDomain => throw new NotSupportedException();
    public Task<T> GetAsync<T>(Guid id, CancellationToken ct = default) where T : class, IDomain => throw new NotSupportedException();
    public Task<T> LoadAsync<T>(Guid id, CancellationToken ct = default) where T : class, IDomain => throw new NotSupportedException();
    public Task<T> SaveAsync<T>(T instance, CancellationToken ct = default) where T : class, IDomain => throw new NotSupportedException();
    public Task<T> UpdateAsync<T>(T instance, CancellationToken ct = default) where T : class, IDomain => throw new NotSupportedException();
    public Task<T> SaveOrUpdateAsync<T>(T instance, CancellationToken ct = default) where T : class, IDomain => throw new NotSupportedException();
    public Task<T> MergeAsync<T>(T instance, CancellationToken ct = default) where T : class, IDomain => throw new NotSupportedException();
    public Task DeleteAsync<T>(T instance, CancellationToken ct = default) where T : class, IDomain => throw new NotSupportedException();
    public Task AttachAsync<T>(T instance, CancellationToken ct = default) where T : class, IDomain => throw new NotSupportedException();
    public string GenerateDatabaseSchema() => throw new NotSupportedException();
    public string UpdateDatabaseSchemaAndExecute() => throw new NotSupportedException();
}

/// <summary>Records what the unit of work hands to the outbox, and when.</summary>
internal sealed class FakeOutbox(FakeStorageProvider storage) : IDomainEventOutbox
{
    public List<IEvent> Written { get; } = [];

    public Task WriteAsync(IStorageProvider target, IReadOnlyList<IEvent> events, CancellationToken ct = default)
    {
        storage.Calls.Add("outbox:" + events.Count);
        Written.AddRange(events);
        return Task.CompletedTask;
    }
}
