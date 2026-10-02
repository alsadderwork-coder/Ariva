using System.Data;
using System.Diagnostics.CodeAnalysis;
using Ariva.Infra.NHibernate.Interceptors;
using NHibernate;
using NHibernate.Engine;
using NHibernate.Linq;
using NHibernate.Transform;

namespace Ariva.Infra.NHibernate;

/// <summary>
/// <see cref="IStorageProvider"/> over one NHibernate session per scope, ported from AMAN. The session opens on first
/// use with the audit interceptor and the soft delete listener, flushes only at commit (AMAN's FlushMode.Commit) and
/// is closed when the scope is disposed. Disposing never opens a session just to close it (AMAN did).
/// </summary>
internal sealed class NHibernateStorageProvider(
    NHibernateSessionFactoryProvider factoryProvider,
    ICurrentUser currentUser,
    TimeProvider timeProvider) : IStorageProvider, IDisposable
{
    private ISession _session;

    private ISession Session => _session ??= OpenSession();

    private ISession OpenSession()
    {
        var session = factoryProvider.SessionFactory
            .WithOptions()
            .Interceptor(new AuditInterceptor(currentUser, timeProvider))
            .OpenSession();

        session.GetSessionImplementation().Listeners.DeleteEventListeners =
            [new SoftDeleteEventListener(currentUser, timeProvider)];
        session.FlushMode = FlushMode.Commit;
        session.EnableFilter(Mapping.NHibernateMappingRules.SoftDeleteFilterName);
        return session;
    }

    #region Transactions

    public void BeginTransaction(IsolationLevel isolationLevel) => Session.BeginTransaction(isolationLevel);

    public bool IsTransactionActive() => _session?.GetCurrentTransaction()?.IsActive ?? false;

    public Task CommitTransactionAsync(CancellationToken ct = default) =>
        Session.GetCurrentTransaction()?.CommitAsync(ct) ?? Task.CompletedTask;

    public Task RollbackTransactionAsync(CancellationToken ct = default) =>
        _session?.GetCurrentTransaction()?.RollbackAsync(ct) ?? Task.CompletedTask;

    public Task FlushAsync(CancellationToken ct = default) => Session.FlushAsync(ct);

    #endregion

    #region Queries

    public IQueryable<T> Query<T>() where T : class, IDomain => Session.Query<T>();

    public async Task<List<T>> ExecuteSqlAsync<T>([ConstantExpected] string sql, IReadOnlyDictionary<string, object> parameters = null, CancellationToken ct = default) where T : class, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        var query = Session.CreateSQLQuery(sql);
        foreach (var (name, value) in parameters ?? new Dictionary<string, object>())
        {
            // A list binds as a parameter list, for "IN (:name)" (each element its own parameter, never inlined); an
            // empty one would make "IN ()", which is not SQL, so it is refused here with the parameter's name.
            if (value is IReadOnlyCollection<string> strings)
            {
                if (strings.Count == 0)
                    throw new ArgumentException($"The parameter list '{name}' is empty.", nameof(parameters));
                query.SetParameterList(name, strings);
            }
            else
                query.SetParameter(name, value);
        }

        query.SetResultTransformer(Transformers.AliasToBean<T>());
        var rows = await query.ListAsync<T>(ct);
        return [.. rows];
    }

    public async Task<List<T>> ListAsync<T>(CancellationToken ct = default) where T : class, IDomain =>
        await Session.Query<T>().ToListAsync(ct);

    public async Task<T> GetAsync<T>(Guid id, CancellationToken ct = default) where T : class, IDomain =>
        id == Guid.Empty ? null : await Session.GetAsync<T>(id, ct);

    public async Task<T> LoadAsync<T>(Guid id, CancellationToken ct = default) where T : class, IDomain =>
        id == Guid.Empty ? null : await Session.LoadAsync<T>(id, ct);

    #endregion

    #region Commands

    public async Task<T> SaveAsync<T>(T instance, CancellationToken ct = default) where T : class, IDomain
    {
        await Session.SaveAsync(instance, ct);
        return instance;
    }

    public async Task<T> UpdateAsync<T>(T instance, CancellationToken ct = default) where T : class, IDomain
    {
        await Session.UpdateAsync(instance, ct);
        return instance;
    }

    public async Task<T> SaveOrUpdateAsync<T>(T instance, CancellationToken ct = default) where T : class, IDomain
    {
        await Session.SaveOrUpdateAsync(instance, ct);
        return instance;
    }

    public Task<T> MergeAsync<T>(T instance, CancellationToken ct = default) where T : class, IDomain =>
        Session.MergeAsync(instance, ct);

    public Task DeleteAsync<T>(T instance, CancellationToken ct = default) where T : class, IDomain =>
        Session.DeleteAsync(instance, ct);

    public Task AttachAsync<T>(T instance, CancellationToken ct = default) where T : class, IDomain =>
        Session.LockAsync(instance, LockMode.None, ct);

    #endregion

    #region Domain Events and Schema

    public IReadOnlyList<IHasDomainEvents> GetEntitiesWithDomainEvents()
    {
        if (_session?.GetSessionImplementation() is not ISessionImplementor implementor)
            return [];

        return implementor.PersistenceContext.EntityEntries.Keys
            .OfType<IHasDomainEvents>()
            .Where(entity => entity.DomainEvents.Count > 0)
            .ToList();
    }

    public string GenerateDatabaseSchema() => factoryProvider.GenerateCreateScript();

    public string UpdateDatabaseSchemaAndExecute() => factoryProvider.UpdateSchema();

    #endregion

    public void Dispose()
    {
        if (_session is null)
            return;

        _session.GetCurrentTransaction()?.Dispose();
        _session.Dispose();
        _session = null;
    }
}
