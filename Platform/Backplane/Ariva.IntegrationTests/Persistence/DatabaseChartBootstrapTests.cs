using Ariva.Di;
using Ariva.IntegrationTests.Operations;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Ariva.IntegrationTests.Persistence;

/// <summary>
/// ARV-062: the TimescaleDB chart's first start, reproduced on the chart's own inputs. The server is a <see cref="ChartDatabase"/>
/// (ARV-182: the helper the backup and restore tests start theirs with): the image by the digest in Charts/timescaledb/values.yaml,
/// the initdb arguments (statefulset.yaml) and the init script (init-configmap.yaml) read from the chart, and the container run
/// as the pod runs: uid 1000, read-only root filesystem, every capability dropped, writable only where the pod mounts emptyDirs
/// and the data volume. Then: no login works without a password over TCP, loopback included (a port-forward arrives there); the
/// migration login owns the database with CREATEROLE and is not a superuser; the real migration (api-main --migrate) runs as that
/// login, twice; the runtime login it creates cannot change the schema; and (ARV-104g1) the validation reader login it creates
/// reads the shadow nowcast and nothing else, which the runtime login cannot. ARV-111a: the non-superuser owner sets the
/// configured hypertable retention and relational periods, and the runtime login cannot remove a retention policy.
/// </summary>
public sealed class DatabaseChartBootstrapTests : IAsyncLifetime
{
    private const string MigrationLogin = ChartDatabase.MigrationLogin;
    private const string Database = ChartDatabase.Database;
    private static readonly string RuntimePassword = "rt-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
    private static readonly string ReaderPassword = "vr-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _workDirectory = Path.Combine(Path.GetTempPath(), "ariva-it-chart-bootstrap-" + Guid.NewGuid().ToString("N"));
    private ChartDatabase _server;

    public async ValueTask InitializeAsync()
    {
        // ChartDatabase shares a folder with the server as its /backup (writable by uid 1000); this test writes nothing there.
        var backup = Path.Combine(_workDirectory, "backup");
        Directory.CreateDirectory(backup);
        File.SetUnixFileMode(backup, (UnixFileMode)0b111_111_111);
        _server = await ChartDatabase.StartAsync(backup, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
            await _server.DisposeAsync();
        if (Directory.Exists(_workDirectory))
            Directory.Delete(_workDirectory, recursive: true);
    }

    private string ConnectionString(string username, string password) => _server.ConnectionString(username, password);

    private enum Query
    {
        One,
        IsSuperuser,
        CanCreateRoles,
        DatabaseOwner,
        TimescaleExtension,
        Telemetry,
        AppliedScripts,
        CreateTable,
        ShadowRows,
        QueueMinuteRows,
        ReaderMemberships,
        RawRetentionDays,
        AlertEmailPeriod,
        RemoveRetentionPolicy,
        PurgeEmails,
        PurgeIntegrationCalls,
        PurgeDeliveries,
        PurgeAsInvoker,
        PurgeAsDefiner,
        RuntimeColumnUpdates,
        PurgeGrantees,
        PublicMayPurge,
        MaySetTempFileLimit,
        TempFileLimit,
        IdleLimit
    }

    // A switch over string literals keeps every command text constant (CA2100).
    private static string Sql(Query query) => query switch
    {
        Query.One => "SELECT 1",
        Query.IsSuperuser => "SELECT rolsuper FROM pg_roles WHERE rolname = current_user",
        Query.CanCreateRoles => "SELECT rolcreaterole FROM pg_roles WHERE rolname = current_user",
        Query.DatabaseOwner => "SELECT pg_get_userbyid(datdba)::text FROM pg_database WHERE datname = current_database()",
        Query.TimescaleExtension => "SELECT extname::text FROM pg_extension WHERE extname = 'timescaledb'",
        Query.Telemetry => "SHOW timescaledb.telemetry_level",
        Query.MaySetTempFileLimit => "SELECT has_parameter_privilege('temp_file_limit', 'SET')",
        Query.TempFileLimit => "SHOW temp_file_limit",
        Query.IdleLimit => "SHOW idle_in_transaction_session_timeout",
        Query.AppliedScripts => "SELECT count(*) FROM schema_version",
        Query.CreateTable => "CREATE TABLE it_not_allowed (id int)",
        // A value column: the runtime role may count rows by their key columns (the stream's upsert needs them), never read a value.
        Query.ShadowRows => "SELECT count(nowcast_degraded) FROM queue_minute_shadow",
        Query.QueueMinuteRows => "SELECT count(*) FROM queue_minute",
        Query.ReaderMemberships => "SELECT string_agg(g.rolname, ',' ORDER BY g.rolname) FROM pg_auth_members m JOIN pg_roles g ON g.oid = m.roleid WHERE m.member = 'ariva_validation'::regrole",
        // ARV-111a: the raw sensing policies (three tables, one period) and a relational period, as the job set them.
        Query.RawRetentionDays => """
            SELECT string_agg(DISTINCT (extract(epoch FROM (config ->> 'drop_after')::interval) / 86400)::int::text, ',')
              FROM timescaledb_information.jobs WHERE proc_name = 'policy_retention' AND hypertable_name IN ('sensing_event', 'sensing_batch', 'device_health_event')
            """,
        Query.AlertEmailPeriod => "SELECT days FROM retention_period WHERE data_set = 'email_message'",
        Query.RemoveRetentionPolicy => "SELECT remove_retention_policy('sensing_event')",
        // ARV-111a: the purge as the hosts call it, and who may call it (grantee 0 is PUBLIC, shown as "-").
        Query.PurgeEmails => "SELECT ariva_retention_purge('email_message', 100)",
        Query.PurgeIntegrationCalls => "SELECT ariva_retention_purge('integration_call', 100)",
        Query.PurgeDeliveries => "SELECT ariva_retention_purge('report_delivery', 100)",
        Query.PurgeAsInvoker => "ALTER FUNCTION ariva_retention_purge(text, integer) SECURITY INVOKER",
        Query.PurgeAsDefiner => "ALTER FUNCTION ariva_retention_purge(text, integer) SECURITY DEFINER",
        // ARV-083a: each column judged for the host role that writes the table (Ariva.Api.Cronz the deliveries, Ariva.Api.Integration
        // the emails' delivery columns and the call record).
        Query.RuntimeColumnUpdates => """
            SELECT string_agg(t || '.' || c || ':' || has_column_privilege(r, t, c, 'UPDATE')::text, ',' ORDER BY t, c)
              FROM (VALUES ('ariva_cronz', 'report_delivery', 'created_utc'), ('ariva_integration', 'email_message', 'created_utc'),
                           ('ariva_integration', 'integration_call', 'at_utc'), ('ariva_cronz', 'report_delivery', 'status')) v(r, t, c)
            """,
        Query.PurgeGrantees => """
            SELECT string_agg(a.grantee::regrole::text || ':' || a.privilege_type || CASE WHEN a.is_grantable THEN '+grant' ELSE '' END, ',' ORDER BY a.grantee::regrole::text)
              FROM pg_proc p, aclexplode(p.proacl) a WHERE p.oid = 'ariva_retention_purge(text,integer)'::regprocedure
            """,
        Query.PublicMayPurge => "SELECT has_function_privilege('public', 'ariva_retention_purge(text,integer)', 'EXECUTE')",
        _ => throw new ArgumentOutOfRangeException(nameof(query))
    };

    private async Task<T> ScalarAsync<T>(string connectionString, Query query)
    {
        var sql = Sql(query);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // every command text is a literal from Sql(Query) above
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        return (T)await command.ExecuteScalarAsync(Ct);
    }

    [Fact(Timeout = 300_000)]
    public async Task FirstStart_Should_RefusePasswordFreeLoginsAndHandTheDatabaseToANonSuperuserOwner_When_RunLikeThePod()
    {
        (await _server.ExecAsync(Ct, "id", "-u")).Stdout.Trim().Should().Be("1000");

        // Over TCP without a password: refused from outside the pod and on loopback inside it (where a port-forward lands).
        var outside = async () => await ScalarAsync<int>(ConnectionString("postgres", null), Query.One);
        await outside.Should().ThrowAsync<NpgsqlException>();
        var loopback = await _server.ExecAsync(Ct, "psql", "-w", "-h", "127.0.0.1", "-U", "postgres", "-d", Database, "-c", "SELECT 1");
        loopback.ExitCode.Should().NotBe(0, $"loopback TCP must need a password ({loopback.Stderr.Trim()})");
        var wrong = async () => await ScalarAsync<int>(ConnectionString(MigrationLogin, "not-the-password-123"), Query.One);
        await wrong.Should().ThrowAsync<PostgresException>();

        // The migration login: owner, CREATEROLE, never superuser; the extension was created by the superuser.
        var owner = ConnectionString(MigrationLogin, _server.MigrationPassword);
        (await ScalarAsync<bool>(owner, Query.IsSuperuser)).Should().BeFalse();
        (await ScalarAsync<bool>(owner, Query.CanCreateRoles)).Should().BeTrue();
        (await ScalarAsync<string>(owner, Query.DatabaseOwner)).Should().Be(MigrationLogin);
        (await ScalarAsync<string>(owner, Query.TimescaleExtension)).Should().Be("timescaledb");
        (await ScalarAsync<string>(owner, Query.Telemetry)).Should().Be("off");
        // ARV-290: the init script gave it SET on temp_file_limit, a superuser setting, so the job can cap the runtime login.
        (await ScalarAsync<bool>(owner, Query.MaySetTempFileLimit)).Should().BeTrue();

        // The release's migration job, as that login (ChartDatabase's settings for this server and the runtime login).
        var settings = _server.MigrationSettings("ariva_app", RuntimePassword);
        // ARV-104g1: the validation reader login, created by the same non-superuser owner (it administers the roles it created).
        settings["Database:ValidationReader:Username"] = "ariva_validation";
        settings["Database:ValidationReader:Password"] = ReaderPassword;
        // ARV-111a: retention periods as the chart passes them to the job (Database__Retention__*).
        settings["Database:Retention:RawSensingDays"] = "60";
        settings["Database:Retention:AlertEmailDays"] = "120";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        (await DatabaseMigration.RunAsync(configuration, Ct)).Should().Be(0, "the migration runs as the non-superuser owner");
        (await DatabaseMigration.RunAsync(configuration, Ct)).Should().Be(0, "and again, updating both logins it created");

        var runtime = ConnectionString("ariva_app", RuntimePassword);
        (await ScalarAsync<long>(runtime, Query.AppliedScripts)).Should().BeGreaterThan(0);
        (await ScalarAsync<string>(runtime, Query.TempFileLimit)).Should().Be("1GB", "ARV-290: the job capped the runtime login's temporary files (Database:TempFileLimitMegabytes, 1,024 by default)");
        (await ScalarAsync<string>(owner, Query.TempFileLimit)).Should().Be("-1", "the migration login keeps no cap");
        (await ScalarAsync<string>(runtime, Query.IdleLimit)).Should().Be("5min", "ARV-292: the job limited the runtime login's idle transactions (300 s by default since ARV-294)");
        (await ScalarAsync<string>(owner, Query.IdleLimit)).Should().Be("0", "the migration login keeps no limit");
        var ddl = async () => await ScalarAsync<object>(runtime, Query.CreateTable);
        (await ddl.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);

        // The reader reads the shadow nowcast's table and nothing else; the runtime login cannot read it.
        var reader = ConnectionString("ariva_validation", ReaderPassword);
        (await ScalarAsync<long>(reader, Query.ShadowRows)).Should().Be(0);
        var readerElsewhere = async () => await ScalarAsync<long>(reader, Query.QueueMinuteRows);
        (await readerElsewhere.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        var runtimeShadow = async () => await ScalarAsync<long>(runtime, Query.ShadowRows);
        (await runtimeShadow.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        (await ScalarAsync<string>(owner, Query.ReaderMemberships)).Should().Be("ariva_validation_reader");

        // ARV-111a: the owner (not a superuser) replaced the scripts' 90 days with the configured period, and the runtime login
        // cannot remove a policy.
        (await ScalarAsync<string>(owner, Query.RawRetentionDays)).Should().Be("60");
        (await ScalarAsync<int>(owner, Query.AlertEmailPeriod)).Should().Be(120);
        var removePolicy = async () => await ScalarAsync<object>(runtime, Query.RemoveRetentionPolicy);
        (await removePolicy.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        (await ScalarAsync<string>(owner, Query.RawRetentionDays)).Should().Be("60");

        // ARV-111a: the purge runs as this non-superuser owner, so its privileges are checked as in the clusters. The runtime
        // login may call it for each of the three data sets (ARV-189: each branch's DELETE is checked as the statement starts, so
        // nothing old enough here, 0 rows, still proves it), and only because it runs as the owner: as SECURITY INVOKER every
        // branch is refused to the runtime login, which may delete none of these rows itself. The reader login may not call it;
        // EXECUTE belongs to the owner and ariva_cronz only (ARV-083a: the Cronz retention job's role; this login is the vm-local
        // development login, which holds every host role), without grant option, and not to PUBLIC.
        foreach (var purge in new[] { Query.PurgeEmails, Query.PurgeIntegrationCalls, Query.PurgeDeliveries })
            (await ScalarAsync<int>(runtime, purge)).Should().Be(0, purge.ToString());
        await ScalarAsync<object>(owner, Query.PurgeAsInvoker);
        foreach (var purge in new[] { Query.PurgeEmails, Query.PurgeIntegrationCalls, Query.PurgeDeliveries })
        {
            var asInvoker = async () => await ScalarAsync<int>(runtime, purge);
            (await asInvoker.Should().ThrowAsync<PostgresException>(purge.ToString())).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        }

        await ScalarAsync<object>(owner, Query.PurgeAsDefiner);
        (await ScalarAsync<int>(runtime, Query.PurgeDeliveries)).Should().Be(0, "the shipped SECURITY DEFINER again");
        // ARV-189 (L4): the owner's view of the column grants scripts 0023, 0025 and 0054 leave: the runtime role cannot move a
        // row's age (so the purge's cutoff) and may still mark a delivery.
        (await ScalarAsync<string>(owner, Query.RuntimeColumnUpdates)).Should().Be(
            "email_message.created_utc:false,integration_call.at_utc:false,report_delivery.created_utc:false,report_delivery.status:true");
        var readerPurge = async () => await ScalarAsync<int>(reader, Query.PurgeIntegrationCalls);
        (await readerPurge.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        (await ScalarAsync<string>(owner, Query.PurgeGrantees)).Should().Be($"{MigrationLogin}:EXECUTE,ariva_cronz:EXECUTE");
        (await ScalarAsync<bool>(owner, Query.PublicMayPurge)).Should().BeFalse();
    }
}
