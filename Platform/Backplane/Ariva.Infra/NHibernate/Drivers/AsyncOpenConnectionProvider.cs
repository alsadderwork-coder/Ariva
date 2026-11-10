using System.Data.Common;
using System.Runtime.CompilerServices;
using NHibernate.Connection;

namespace Ariva.Infra.NHibernate.Drivers;

/// <summary>
/// ARV-243 (CWE-400; ASVS 5.0 V15.2.2): the connection provider every NHibernate session factory uses, which opens every
/// connection through Npgsql's asynchronous open, also when NHibernate asks for one synchronously.
/// <para>
/// NHibernate asks synchronously when it loads a lazy collection (every bag is lazy, fetched by subselect) after the session
/// gave its connection back, which outside a transaction it does after each statement; also for a synchronous query and for
/// SchemaUpdate in vm-local. Npgsql 10.0.0's synchronous open bounds each read of the startup by the socket's receive timeout,
/// set from the connection timeout left, and less than a millisecond left becomes 0, no timeout at all
/// (<c>NpgsqlTimeout.CheckAndApply</c>), so a database that accepts and never answers could hold that caller for good (the
/// ARV-242 finding, where it held a run for two hours). The asynchronous open is ended by Npgsql's own timer at the connection
/// timeout, whatever is left. So the synchronous request blocks its thread on the asynchronous open, at most one connection
/// timeout, as the synchronous open would have blocked it, but never past it. Npgsql and NHibernate do not resume on the
/// caller's context (ConfigureAwait(false) throughout), and the hosts have no synchronization context, so the wait cannot
/// deadlock. The session's connection is still released as before (after each statement outside a transaction), so the pool
/// sizes ARV-232 measured are unchanged.
/// </para>
/// <para>
/// The synchronous override is sealed and <see cref="NHibernateSessionFactoryProvider"/> refuses a factory whose provider is not
/// this one or derived from it. A derived type could still open synchronously in its own asynchronous override, which is why
/// <c>ConnectionProviderTests</c> holds that no production type derives from it or implements NHibernate's provider interface
/// (only a test's recording provider does).
/// </para>
/// </summary>
public class AsyncOpenConnectionProvider : DriverConnectionProvider
{
    #region Methods

    /// <summary>Opens the connection through <see cref="DriverConnectionProvider.GetConnectionAsync(string, CancellationToken)"/> and waits for it.</summary>
    /// <param name="connectionString">The connection string NHibernate passes.</param>
    /// <returns>The open connection.</returns>
    // Not inlined, so a recording provider (LazyLoadOpenTests) finds this frame on the stack whatever the JIT does.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public sealed override DbConnection GetConnection(string connectionString) =>
        GetConnectionAsync(connectionString, CancellationToken.None).GetAwaiter().GetResult();

    #endregion
}
