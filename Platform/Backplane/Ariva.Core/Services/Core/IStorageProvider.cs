using System.Data;
using System.Diagnostics.CodeAnalysis;

namespace Ariva.Core.Services;

/// <summary>
/// The thin wrapper around the ORM (NHibernate), ported from AMAN and trimmed. One instance per scope, holding one
/// session. Services reach it through <see cref="IUnitOfWork"/> and the <c>SvcDb</c> base class, never directly.
/// Removed from AMAN on purpose: the raw <c>DbConnection</c> (it bypassed the session and its transaction), the
/// second-level cache listing helpers (the cache is off, FusionCache is the cache) and schema export with execute
/// (production schema comes only from the versioned SQL scripts, ARV-006).
/// </summary>
public interface IStorageProvider
{
    #region Transactions

    void BeginTransaction(IsolationLevel isolationLevel);
    bool IsTransactionActive();
    Task CommitTransactionAsync(CancellationToken ct = default);
    Task RollbackTransactionAsync(CancellationToken ct = default);

    /// <summary>Writes pending changes to the database inside the current transaction, without committing.</summary>
    Task FlushAsync(CancellationToken ct = default);

    #endregion

    #region Queries

    IQueryable<T> Query<T>() where T : class, IDomain;

    /// <summary>
    /// Runs a parameterised SQL query and maps each row to <typeparamref name="T"/> by column alias. The SQL must be
    /// a constant: values go in <paramref name="parameters"/> and are bound by name (:name). CA1857 (an error in
    /// .editorconfig) rejects an interpolated or concatenated string at compile time and the security scanner
    /// rejects it in review (CWE-89). There is deliberately no overload that takes a FormattableString. A value that is a
    /// collection of strings binds as a parameter list, for <c>IN (:name)</c>; it must not be empty.
    /// </summary>
    Task<List<T>> ExecuteSqlAsync<T>([ConstantExpected] string sql, IReadOnlyDictionary<string, object> parameters = null, CancellationToken ct = default) where T : class, new();

    Task<List<T>> ListAsync<T>(CancellationToken ct = default) where T : class, IDomain;
    Task<T> GetAsync<T>(Guid id, CancellationToken ct = default) where T : class, IDomain;

    /// <summary>A proxy that loads on first access; use only to set a reference without reading the row.</summary>
    Task<T> LoadAsync<T>(Guid id, CancellationToken ct = default) where T : class, IDomain;

    #endregion

    #region Commands

    Task<T> SaveAsync<T>(T instance, CancellationToken ct = default) where T : class, IDomain;
    Task<T> UpdateAsync<T>(T instance, CancellationToken ct = default) where T : class, IDomain;
    Task<T> SaveOrUpdateAsync<T>(T instance, CancellationToken ct = default) where T : class, IDomain;
    Task<T> MergeAsync<T>(T instance, CancellationToken ct = default) where T : class, IDomain;

    /// <summary>Deletes the entity; an <see cref="ISoftDeletable"/> entity is marked deleted instead.</summary>
    Task DeleteAsync<T>(T instance, CancellationToken ct = default) where T : class, IDomain;

    /// <summary>Re-attaches a detached entity to the session without reading it.</summary>
    Task AttachAsync<T>(T instance, CancellationToken ct = default) where T : class, IDomain;

    #endregion

    #region Domain Events and Schema

    /// <summary>Entities tracked by the session that hold domain events not yet dequeued.</summary>
    IReadOnlyList<IHasDomainEvents> GetEntitiesWithDomainEvents();

    /// <summary>The DDL NHibernate would create, for authoring versioned scripts. Never executed by this method.</summary>
    string GenerateDatabaseSchema();

    /// <summary>
    /// Runs NHibernate SchemaUpdate. Refused unless Database:AllowSchemaUpdate is true, which only the vm-local
    /// configuration sets; every other environment gets its schema from the versioned scripts (ARV-006).
    /// </summary>
    string UpdateDatabaseSchemaAndExecute();

    #endregion
}
