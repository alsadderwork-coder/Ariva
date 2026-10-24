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

    /// <summary>
    /// A parameterised SQL command (UPDATE ... RETURNING) inside the unit of work's transaction, committed with it.
    /// For changes that must be atomic in the database rather than read, changed and written through the session.
    /// </summary>
    protected Task<List<T>> ExecuteCommandAsync<T>([ConstantExpected] string sql, IReadOnlyDictionary<string, object> parameters = null, CancellationToken ct = default) where T : class, new() =>
        InTransactionAsync(storage => storage.ExecuteSqlAsync<T>(sql, parameters, ct), ct);

    #endregion

    #region Commands

    protected Task<T> SaveAsync<T>(T entity, CancellationToken ct = default) where T : class, IDomain =>
        InTransactionAsync(storage => storage.SaveAsync(entity, ct), ct);

    protected Task<T> UpdateAsync<T>(T entity, CancellationToken ct = default) where T : class, IDomain =>
        InTransactionAsync(storage => storage.UpdateAsync(entity, ct), ct);

    protected Task<T> SaveOrUpdateAsync<T>(T entity, CancellationToken ct = default) where T : class, IDomain =>
        InTransactionAsync(storage => storage.SaveOrUpdateAsync(entity, ct), ct);

    protected Task<T> MergeAsync<T>(T entity, CancellationToken ct = default) where T : class, IDomain =>
        InTransactionAsync(storage => storage.MergeAsync(entity, ct), ct);

    protected Task DeleteAsync<T>(T entity, CancellationToken ct = default) where T : class, IDomain =>
        InTransactionAsync(async storage =>
        {
            await storage.DeleteAsync(entity, ct);
            return true;
        }, ct);

    /// <summary>
    /// Sends pending changes to the database now, inside the transaction. The session flushes only at commit and orders
    /// inserts before updates, so call this when a later insert depends on an earlier update (a partial unique index
    /// freed by a soft delete).
    /// </summary>
    protected Task FlushAsync(CancellationToken ct = default) =>
        InTransactionAsync(async storage =>
        {
            await storage.FlushAsync(ct);
            return true;
        }, ct);

    protected Task AttachAsync<T>(T entity, CancellationToken ct = default) where T : class, IDomain => Storage.AttachAsync(entity, ct);

    /// <summary>
    /// Takes the site's row in share mode, held until the unit of work ends (ARV-154). Every create that adds configuration to
    /// a site calls it after its own checks and before its first write or any other lock, so a site configuration import, which
    /// takes the row for update while it checks that the site is empty and writes, waits for the creates in flight and they
    /// wait for it; creates do not wait for each other.
    /// </summary>
    protected Task ShareSiteAsync(string siteCode, CancellationToken ct = default) =>
        ExecuteCommandAsync<SiteRow>("""SELECT 1 AS "Value" FROM site WHERE code = :code FOR SHARE""",
            new Dictionary<string, object> { ["code"] = siteCode }, ct);

    protected void RegisterPostCommitAction(Func<Task> action) => UnitOfWork.RegisterPostCommitAction(action);

    protected void RegisterPostRollbackAction(Func<Task> action) => UnitOfWork.RegisterPostRollbackAction(action);

    #endregion

    private async Task<TResult> InTransactionAsync<TResult>(Func<IStorageProvider, Task<TResult>> command, CancellationToken ct)
    {
        if (!Storage.IsTransactionActive())
            await Storage.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var result = await command(Storage);
        UnitOfWork.PromiseToCommit();
        return result;
    }

    private sealed class SiteRow
    {
        public int Value { get; set; }
    }
}
