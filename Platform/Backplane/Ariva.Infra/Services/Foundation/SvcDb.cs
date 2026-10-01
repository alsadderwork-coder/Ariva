using System.Data;
using System.Diagnostics.CodeAnalysis;

namespace Ariva.Infra.Services.Foundation;

/// <summary>
/// Base class for services that touch the database, ported from AMAN. Reads go straight to the storage provider;
/// writes open a ReadCommitted transaction when none is active and promise the unit of work to commit (AMAN's
/// <c>With.Transaction</c>), so a service never commits by itself.
/// </summary>
internal abstract class SvcDb(IUnitOfWork unitOfWork)
{
    protected IUnitOfWork UnitOfWork { get; } = unitOfWork;

    private IStorageProvider Storage => UnitOfWork.StorageProvider;

    #region Queries

    protected IQueryable<T> Query<T>() where T : class, IDomain => Storage.Query<T>();

    /// <summary>
    /// AMAN name kept; NHibernate has no no-tracking queries, so this is the same as <see cref="Query{T}"/>. Use it
    /// for read-only paths so intent is visible, and project to view models instead of loading entities.
    /// </summary>
    protected IQueryable<T> QueryAsNoTracking<T>() where T : class, IDomain => Storage.Query<T>();

    protected Task<T> GetAsync<T>(Guid id, CancellationToken ct = default) where T : class, IDomain => Storage.GetAsync<T>(id, ct);

    protected Task<T> LoadAsync<T>(Guid id, CancellationToken ct = default) where T : class, IDomain => Storage.LoadAsync<T>(id, ct);

    protected Task<List<T>> ListAsync<T>(CancellationToken ct = default) where T : class, IDomain => Storage.ListAsync<T>(ct);

    /// <summary>Parameterised SQL only; see <see cref="IStorageProvider.ExecuteSqlAsync{T}"/>.</summary>
    protected Task<List<T>> ExecuteSqlAsync<T>([ConstantExpected] string sql, IReadOnlyDictionary<string, object> parameters = null, CancellationToken ct = default) where T : class, new() =>
        Storage.ExecuteSqlAsync<T>(sql, parameters, ct);

    #endregion

    #region Commands

    protected Task<T> SaveAsync<T>(T entity, CancellationToken ct = default) where T : class, IDomain =>
        InTransactionAsync(storage => storage.SaveAsync(entity, ct));

    protected Task<T> UpdateAsync<T>(T entity, CancellationToken ct = default) where T : class, IDomain =>
        InTransactionAsync(storage => storage.UpdateAsync(entity, ct));

    protected Task<T> SaveOrUpdateAsync<T>(T entity, CancellationToken ct = default) where T : class, IDomain =>
        InTransactionAsync(storage => storage.SaveOrUpdateAsync(entity, ct));

    protected Task<T> MergeAsync<T>(T entity, CancellationToken ct = default) where T : class, IDomain =>
        InTransactionAsync(storage => storage.MergeAsync(entity, ct));

    protected Task DeleteAsync<T>(T entity, CancellationToken ct = default) where T : class, IDomain =>
        InTransactionAsync(async storage =>
        {
            await storage.DeleteAsync(entity, ct);
            return true;
        });

    protected Task AttachAsync<T>(T entity, CancellationToken ct = default) where T : class, IDomain => Storage.AttachAsync(entity, ct);

    protected void RegisterPostCommitAction(Func<Task> action) => UnitOfWork.RegisterPostCommitAction(action);

    protected void RegisterPostRollbackAction(Func<Task> action) => UnitOfWork.RegisterPostRollbackAction(action);

    #endregion

    private async Task<TResult> InTransactionAsync<TResult>(Func<IStorageProvider, Task<TResult>> command)
    {
        if (!Storage.IsTransactionActive())
            Storage.BeginTransaction(IsolationLevel.ReadCommitted);

        var result = await command(Storage);
        UnitOfWork.PromiseToCommit();
        return result;
    }
}
