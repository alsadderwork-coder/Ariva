using System.Data;
using System.Data.Common;
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

    /// <summary>
    /// ARV-242 (CWE-400): NHibernate's BeginTransaction opens the session's connection synchronously, where Npgsql bounds each read
    /// by the socket's receive timeout, set from the connection timeout left; less than a millisecond left becomes 0, no timeout
    /// at all (Npgsql 10.0.0, NpgsqlTimeout.CheckAndApply and the read buffer's Timeout setter), so a silent database held a run
    /// for two hours. The connection is opened here first, asynchronously, through NHibernate's own connection manager (the
    /// driver, so the handshake keeps its policy), where Npgsql's own timer always ends a stalled startup at the connection
    /// timeout (the token is not taken during the startup, measured, so a run ends at most one connection timeout past its own;
    /// a token already cancelled opens nothing, <c>RunBound.MinRunTimeout</c>); the transaction then begins on it.
    /// </summary>
    public async Task BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken ct = default)
    {
        await Session.GetSessionImplementation().ConnectionManager.GetConnectionAsync(ct);
        Session.BeginTransaction(isolationLevel);
    }

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

    /// <summary>
    /// ARV-228b: the command is the session's own (<see cref="global::NHibernate.AdoNet.ConnectionManager.CreateCommandAsync"/>: its
    /// connection, enlisted in its transaction, as the query of <see cref="ExecuteSqlAsync{T}"/> is), so the read sees what the unit
    /// of work sees, under the connection string's command timeout. The text and every value are checked before a connection is
    /// taken: a text that does not start with SELECT or WITH is refused, a guard against a write by mistake rather than a proof that
    /// the statement only reads (the constant text under review is the control). Outside a transaction the session then does what NHibernate's
    /// own query does after it (<c>AbstractSessionImpl.AfterOperation</c>): the connection manager's after-query and
    /// after-transaction steps, which hand the connection back to the pool, and the session's transaction completion; on every
    /// path, a map that throws and a cancelled read included.
    /// </summary>
    public async Task<List<T>> ReadSqlAsync<T>([ConstantExpected] string sql, IReadOnlyDictionary<string, object> parameters, Func<DbDataReader, T> map,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentNullException.ThrowIfNull(map);
        if (!IsRead(sql))
            throw new ArgumentException("ReadSqlAsync takes a statement that starts with SELECT or WITH.", nameof(sql));
        var values = (parameters ?? new Dictionary<string, object>()).Select(p => (p.Key, Value: ReadValue(p.Key, p.Value))).ToList();

        var session = Session.GetSessionImplementation();
        var manager = session.ConnectionManager;
        var success = false;
        try
        {
            await using var command = await manager.CreateCommandAsync(ct);
#pragma warning disable CA2100 // every caller passes a literal (ConstantExpected, CA1857 is an error)
            command.CommandText = sql;
#pragma warning restore CA2100
            foreach (var (name, value) in values)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value;
                command.Parameters.Add(parameter);
            }

            var rows = new List<T>();
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                // The token is checked between rows too: Npgsql reads rows it holds in its buffer without looking at it.
                while (await reader.ReadAsync(ct))
                {
                    ct.ThrowIfCancellationRequested();
                    rows.Add(map(reader));
                }
            }

            success = true;
            return rows;
        }
        finally
        {
            if (!manager.IsInActiveTransaction)
            {
                manager.AfterNonTransactionalQuery(success);
                manager.AfterTransaction();
                session.AfterTransactionCompletion(success, null);
            }
        }
    }

    // The statement's first word, after any white space, is SELECT or WITH (a guard against a write by mistake, not a parse).
    private static bool IsRead(string sql)
    {
        var text = sql.AsSpan().TrimStart();
        return StartsWithWord(text, "SELECT") || StartsWithWord(text, "WITH");
    }

    private static bool StartsWithWord(ReadOnlySpan<char> text, string word) =>
        text.StartsWith(word, StringComparison.OrdinalIgnoreCase) && (text.Length == word.Length || (!char.IsLetterOrDigit(text[word.Length]) && text[word.Length] != '_'));

    // A value as ReadSqlAsync binds it: NULL for null, one array for a collection of strings (= ANY), a UTC time only.
    private static object ReadValue(string name, object value) => value switch
    {
        null => DBNull.Value,
        DateTime { Kind: not DateTimeKind.Utc } => throw new ArgumentException($"The parameter '{name}' is a time that is not UTC.", nameof(value)),
        string[] strings => strings,
        IReadOnlyCollection<string> strings => strings.ToArray(),
        _ => value
    };

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
