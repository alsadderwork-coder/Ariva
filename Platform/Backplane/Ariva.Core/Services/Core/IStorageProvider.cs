using System.Data;
using System.Data.Common;
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

    /// <summary>
    /// Begins a transaction. The session's connection is opened first, asynchronously (ARV-242): a database that accepts the
    /// connection and never answers holds the caller until the connection timeout ends the open, never in a synchronous read
    /// without a bound; a token already cancelled opens nothing.
    /// </summary>
    Task BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken ct = default);
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

    /// <summary>
    /// Runs a parameterised SQL query on the session's connection and transaction and maps each row with <paramref name="map"/>,
    /// which reads the columns by position with the reader's typed getters (ARV-228b): no reflection and no boxed value per
    /// column, for reads of tens of thousands of rows (the alert rule backtest). The SQL must be a constant, as for
    /// <see cref="ExecuteSqlAsync{T}(string, IReadOnlyDictionary{string, object}, CancellationToken)"/> (CA1857, CWE-89); values
    /// bind by name (<c>:name</c>, the database driver's own placeholder). Unlike that method, a collection of strings binds as
    /// one array, for <c>= ANY(:name)</c> (never <c>IN (:name)</c>), and may be empty; null binds as NULL; a
    /// <see cref="DateTime"/> that is not UTC is refused, so a time is never read in the server's zone. A text that does not start with
    /// SELECT or WITH is refused: a guard against a write by mistake, not a proof that the statement only reads (a data-modifying CTE or
    /// a second statement would pass it; the constant text under review is the control). Every value is checked before a connection is
    /// taken, and outside a transaction the connection is handed back to the pool after the read, whatever its outcome.
    /// </summary>
    Task<List<T>> ReadSqlAsync<T>([ConstantExpected] string sql, IReadOnlyDictionary<string, object> parameters, Func<DbDataReader, T> map, CancellationToken ct = default);

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
