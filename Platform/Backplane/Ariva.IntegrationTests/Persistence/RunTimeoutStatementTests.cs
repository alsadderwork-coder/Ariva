using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using Ariva.Di;
using Ariva.Infra.Resilience;
using Ariva.IntegrationTests.Operations;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using TickerQ.Utilities.Base;

namespace Ariva.IntegrationTests.Persistence;

/// <summary>
/// ARV-235 (CWE-400, ASVS 5.0 V15.2.2): every bounded loop and every Cronz job of <c>RunTimeoutInventoryTests</c> passes its
/// run's token on to its first statement. Each is built by its own host on a migrated database, run once while the database
/// answers (so the pool holds an open connection and the session factory is built), then run with a short run timeout while
/// another session holds every table of the schema (and TimescaleDB's catalog) in ACCESS EXCLUSIVE mode: the run's first
/// statement waits on that lock, seen in <c>pg_stat_activity</c>, and the run ends with <see cref="RunTimeoutException"/> at
/// its timeout, its cause a cancellation (no client timeout, no lock timeout), within 8 s, below the 10 s that the shortest
/// command timeout of Ariva.Infra would take and far below <c>Database:CommandTimeoutSeconds</c>; no statement of the run
/// waits once it has ended; once the lock goes the next run succeeds. The runtime login has no lock timeout and no
/// statement timeout under the command timeout, checked first, so nothing but the run's token can end the wait in time. The
/// unit inventory proves the same against a database that never answers, where the connection timeout ends the open; this
/// proves it at a statement.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RunTimeoutStatementTests(PostgresFixture fixture)
{
    #region Fields

    private const string RuntimeLogin = "it_run_timeout_app";

    /// <summary>The command timeout the hosts run with here: the bound a statement would wait without the run's token.</summary>
    private const int CommandTimeoutSeconds = 30;

    private static readonly string RuntimePassword = "rt-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string _database;

    /// <summary>The run timeout set for the test.</summary>
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Well before any other bound: the run timeout and the unit inventory's margin for a busy machine, below the 10 s command
    /// timeout of Ariva.Infra's own short reads (the desk term source, the stream store, the outbox's backlog measurement).
    /// </summary>
    private static readonly TimeSpan WellBefore = TimeSpan.FromSeconds(2 + 6);

    /// <summary>
    /// Every table of the schema, of TimescaleDB's materialized aggregates and of its catalog, locked in the holder's
    /// transaction: a statement on any table (a hypertable read through the catalog, a continuous aggregate through its
    /// materialized table, a job through the job catalog) waits on it.
    /// </summary>
    private const string LockEverything = """
        DO $$
        DECLARE t regclass;
        BEGIN
          FOR t IN SELECT c.oid::regclass FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                   WHERE c.relkind IN ('r', 'p') AND n.nspname IN ('public', '_timescaledb_internal', '_timescaledb_config', '_timescaledb_catalog')
                   ORDER BY n.nspname, c.relname
          LOOP
            EXECUTE format('LOCK TABLE %s IN ACCESS EXCLUSIVE MODE', t);
          END LOOP;
        END $$
        """;

    /// <summary>The bounded loops each host runs (as <c>RunTimeoutInventoryTests.BoundedLoops</c> lists them), by the host that builds them.</summary>
    private static readonly Dictionary<string, string[]> LoopsByHost = new(StringComparer.Ordinal)
    {
        ["Main"] = ["DeviceHeartbeatMonitor", "ValidationResultsFreezer", "DisplayStaleMonitor"],
        ["Stream"] = ["AlertEvaluationWorker", "DeskFeedWorker", "BinMaturityMonitor"],
        ["Integration"] = ["IdempotencySweeper", "AcrisPoller", "AmanPoller", "FeedFreshnessMonitor", "EmailSenderWorker"]
    };

    /// <summary>The bounded loops a run of which reaches no database, and why.</summary>
    private static readonly Dictionary<string, string> NoDatabase = new(StringComparer.Ordinal)
    {
        ["ScannerSignatureMonitor"] = "asks the malware scanner only (no statement); its stalled scanner ends at the run timeout in RunTimeoutInventoryTests"
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    #endregion

    #region Tests

    public static TheoryData<string, string> Loops
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var (host, loops) in LoopsByHost)
            {
                foreach (var loop in loops)
                    data.Add(host, loop);
            }

            return data;
        }
    }

    /// <summary>The assemblies that hold Ariva's hosted services (as <c>RunTimeoutInventoryTests</c> reads them; Ingest runs no loop and is not referenced here).</summary>
    private static readonly System.Reflection.Assembly[] ArivaAssemblies =
    [
        typeof(Ariva.Infra._IAssemblyMark).Assembly, typeof(Ariva.Api.Common.Hubs.HubSessionSweeper).Assembly, typeof(Ariva.Api.Main._IAssemblyMark).Assembly,
        typeof(Ariva.Api.Stream._IAssemblyMark).Assembly, typeof(Ariva.Api.Cronz._IAssemblyMark).Assembly, typeof(Ariva.Api.Integration._IAssemblyMark).Assembly
    ];

    [Fact]
    public void Loops_Should_BeEveryBoundedLoop_When_Listed()
    {
        var bounded = ArivaAssemblies.SelectMany(a => a.GetTypes())
            .Where(t => typeof(IBoundedRuns).IsAssignableFrom(t) && typeof(IHostedService).IsAssignableFrom(t) && !t.IsAbstract)
            .Select(t => t.Name);

        LoopsByHost.Values.SelectMany(loops => loops).Concat(NoDatabase.Keys).Should().BeEquivalentTo(bounded,
            "every bounded loop is run here on a held statement, or named with why its run reaches no database");
    }

    [Theory]
    [MemberData(nameof(Loops))]
    public async Task Loop_Should_EndItsRunAtTheRunTimeout_When_TheDatabaseHoldsItsStatement(string host, string loop)
    {
        var database = await MigratedAsync();
        await using var app = Host(host, database);
        var type = ArivaAssemblies.SelectMany(a => a.GetTypes()).Single(t => t.Name == loop && typeof(IBoundedRuns).IsAssignableFrom(t));
        var instance = ActivatorUtilities.CreateInstance(app.Services, type);
        ShortenRunTimeout(instance);
        var runOnce = type.GetMethod("RunOnceAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, [typeof(CancellationToken)]);
        runOnce.Should().NotBeNull($"{loop} runs each run through RunOnceAsync");

        await HeldAsync(database, loop, () => (Task)runOnce.Invoke(instance, [Ct]));
    }

    /// <summary>Every TickerQ function of Ariva.Api.Cronz, by its class.</summary>
    public static TheoryData<string> Jobs => [.. JobTypes().Select(t => t.Name)];

    private static IEnumerable<Type> JobTypes() => typeof(Ariva.Api.Cronz._IAssemblyMark).Assembly.GetTypes()
        .Where(t => t.GetMethods().Any(m => m.GetCustomAttribute<TickerFunctionAttribute>() is not null)).OrderBy(t => t.Name, StringComparer.Ordinal);

    [Theory]
    [MemberData(nameof(Jobs))]
    public async Task Job_Should_EndItsRunAtTheRunTimeout_When_TheDatabaseHoldsItsStatement(string job)
    {
        var database = await MigratedAsync();
        await using var app = Host("Cronz", database);
        await using var scope = app.Services.CreateAsyncScope();
        var type = JobTypes().Single(t => t.Name == job);
        var instance = ActivatorUtilities.CreateInstance(scope.ServiceProvider, type);
        ShortenRunTimeout(instance);
        var function = type.GetMethods().Single(m => m.GetCustomAttribute<TickerFunctionAttribute>() is not null);

        await HeldAsync(database, job, () => (Task)function.Invoke(instance, [new TickerFunctionContext(), Ct]));
    }

    #endregion

    #region Helpers

    /// <summary>
    /// One run while the database answers, then one while every table is held: that run waits on the lock and ends at its
    /// run timeout, well before the command timeout; then one more once the lock is gone.
    /// </summary>
    private async Task HeldAsync(string database, string name, Func<Task> run)
    {
        await NoOtherBoundAsync(database);
        await run();

        await using var holder = new NpgsqlConnection(fixture.ConnectionString(database));
        await holder.OpenAsync(Ct);
        await using var hold = await holder.BeginTransactionAsync(Ct);
        await using (var lockEverything = new NpgsqlCommand(LockEverything, holder, hold))
            await lockEverything.ExecuteNonQueryAsync(Ct);
        var watch = Stopwatch.StartNew();

        var held = run();
        var waited = await WaitedOnTheLockAsync(database, held);
        Exception failure = null;
        try
        {
            await held;
        }
#pragma warning disable CA1031 // the run's failure is what the test reads
        catch (Exception e)
#pragma warning restore CA1031
        {
            failure = e;
        }

        var elapsed = watch.Elapsed;
        var stillWaiting = await WaitingAsync(database);
        await hold.RollbackAsync(Ct);

        failure.Should().BeOfType<RunTimeoutException>($"{name}'s statement waited on the lock until its run's token ended it")
            .Which.Timeout.Should().Be(RunTimeout);
        // The cut's cause is the run's token: a cancellation, never a client timeout or the server's lock timeout that RunBound would
        // report the same way once the run's time had passed (review round 1).
        var causes = Causes(failure).ToList();
        causes.Should().Contain(e => e is OperationCanceledException, $"{name}'s run was ended by its token");
        causes.Should().NotContain(e => e is NpgsqlException && e.InnerException is TimeoutException, "no client command timeout ended it");
        causes.OfType<PostgresException>().Select(e => e.SqlState).Should().NotContain("55P03", "no lock timeout ended it");
        waited.Should().BeTrue($"{name}'s run reached the database and its statement waited on the held lock");
        stillWaiting.Should().Be(0, $"no statement of {name}'s run waits once the run has ended (none left behind on its own token)");
        elapsed.Should().BeGreaterThanOrEqualTo(RunTimeout - TimeSpan.FromMilliseconds(20))
            .And.BeLessThan(WellBefore, $"the run's token ended {name}'s statement at its {RunTimeout.TotalSeconds:F0} s, not the {CommandTimeoutSeconds} s command timeout");
        await run();
    }

    /// <summary>
    /// The runtime login's own bounds on a statement, which would end a lock wait without the run's token: no lock timeout, and
    /// no statement timeout under the command timeout (script 0063 sets 360 s).
    /// </summary>
    private async Task NoOtherBoundAsync(string database)
    {
        await using var runtime = new NpgsqlConnection(fixture.ConnectionString(database, RuntimeLogin, RuntimePassword));
        await runtime.OpenAsync(Ct);
        await using var read = new NpgsqlCommand(
            "SELECT current_setting('lock_timeout'), (SELECT setting::bigint FROM pg_settings WHERE name = 'statement_timeout')", runtime);
        await using var reader = await read.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).Should().BeTrue();
        reader.GetString(0).Should().Be("0", "a lock timeout would end the wait without the run's token");
        var statementTimeoutMs = reader.GetInt64(1);
        (statementTimeoutMs == 0 || statementTimeoutMs >= CommandTimeoutSeconds * 1000L).Should().BeTrue("a statement timeout under the command timeout would end the wait first");
    }

    /// <summary>How many statements of the runtime login wait on a lock in the test's database now (read from the maintenance database).</summary>
    private async Task<long> WaitingAsync(string database)
    {
        await using var monitor = new NpgsqlConnection(fixture.ConnectionString("postgres"));
        await monitor.OpenAsync(Ct);
        await using var waiting = new NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE datname = @database AND usename = @login AND wait_event_type = 'Lock'", monitor);
        waiting.Parameters.AddWithValue("database", database);
        waiting.Parameters.AddWithValue("login", RuntimeLogin);
        return (long)(await waiting.ExecuteScalarAsync(Ct) ?? 0L);
    }

    private static IEnumerable<Exception> Causes(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            yield return e;
            if (e is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions.SelectMany(Causes))
                    yield return inner;
        }
    }

    /// <summary>
    /// Whether a statement of the runtime login waits on a lock while the run is under way. Read from the maintenance database:
    /// in the test's database every statement waits while TimescaleDB's catalog is held, this one too.
    /// </summary>
    private async Task<bool> WaitedOnTheLockAsync(string database, Task running)
    {
        await using var monitor = new NpgsqlConnection(fixture.ConnectionString("postgres"));
        await monitor.OpenAsync(Ct);
        await using var waiting = new NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE datname = @database AND usename = @login AND wait_event_type = 'Lock'", monitor);
        waiting.Parameters.AddWithValue("database", database);
        waiting.Parameters.AddWithValue("login", RuntimeLogin);
        while (!running.IsCompleted)
        {
            if ((long)(await waiting.ExecuteScalarAsync(Ct) ?? 0L) > 0)
                return true;
            await Task.Delay(25, Ct);
        }

        return false;
    }

    private static void ShortenRunTimeout(object instance) =>
        instance.GetType().GetProperty(nameof(IBoundedRuns.RunTimeout)).SetValue(instance, RunTimeout);

    /// <summary>
    /// A host on the test's database through the runtime login, without its hosted services (the loops are built by the
    /// test), and with the loops' optional parts on: Stream's engine services (<c>Stream:Enabled</c>).
    /// </summary>
    private TestHost Host(string name, string database)
    {
        var settings = Settings(database);
        settings["Stream:Enabled"] = "true";
        settings["Database:AllowSchemaUpdate"] = "false";
        settings["Database:VerifySchemaOnStartup"] = "false";
        settings["Database:CommandTimeoutSeconds"] = CommandTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        static void WithoutHostedServices(IServiceCollection services)
        {
            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService) &&
                         d.ImplementationType?.FullName != "Microsoft.AspNetCore.Hosting.GenericWebHostService").ToList())
                services.Remove(descriptor);
        }

        static TestHost Of<TEntryPoint>(DeployedHost<TEntryPoint> host) where TEntryPoint : class => new(host, () => host.Services);
        return name switch
        {
            "Main" => Of(new DeployedHost<Ariva.Api.Main._IAssemblyMark>(settings, "vm-local", WithoutHostedServices)),
            "Stream" => Of(new DeployedHost<Ariva.Api.Stream._IAssemblyMark>(settings, "vm-local", WithoutHostedServices)),
            "Integration" => Of(new DeployedHost<Ariva.Api.Integration._IAssemblyMark>(settings, "vm-local", WithoutHostedServices)),
            "Cronz" => Of(new DeployedHost<Ariva.Api.Cronz._IAssemblyMark>(settings, "vm-local", WithoutHostedServices)),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown Ariva host.")
        };
    }

    /// <summary>A started host's services (the factory starts the host on first use) and its disposal.</summary>
    private sealed class TestHost(IAsyncDisposable factory, Func<IServiceProvider> services) : IAsyncDisposable
    {
        public IServiceProvider Services => services();

        public ValueTask DisposeAsync() => factory.DisposeAsync();
    }

    /// <summary>The database every test here uses, migrated once by the migration job as vm-local, with the runtime login.</summary>
    private async Task<string> MigratedAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            if (_database is null)
            {
                var database = await fixture.CreateDatabaseAsync(TestDatabase.RunTimeoutStatements);
                (await DatabaseMigration.RunAsync(new ConfigurationBuilder().AddInMemoryCollection(Settings(database)).Build(), Ct)).Should().Be(0);
                _database = database;
            }

            return _database;
        }
        finally
        {
            Gate.Release();
        }
    }

    private Dictionary<string, string> Settings(string database) => new()
    {
        ["Application:Environment"] = "vm-local",
        ["Database:Host"] = fixture.Hostname,
        ["Database:Port"] = fixture.Port.ToString(CultureInfo.InvariantCulture),
        ["Database:Name"] = database,
        ["Database:Migration:Username"] = fixture.AdminUsername,
        ["Database:Migration:Password"] = fixture.AdminPassword,
        ["Database:Username"] = RuntimeLogin,
        ["Database:Password"] = RuntimePassword
    };

    #endregion
}
