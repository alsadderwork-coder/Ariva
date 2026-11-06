using Ariva.Infra.Timescale;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Ariva.IntegrationTests.Operations;

/// <summary>
/// ARV-263 review (CWE-269, CWE-345): what a forged dump could make TimescaleDB do before <c>ariva-db.sh check</c> runs, on two
/// servers started as the TimescaleDB chart starts one (<see cref="ChartDatabase"/>). A job runs as its owner as soon as the
/// restore leaves TimescaleDB's restore mode, so the restore judges the jobs it brought while that mode still keeps the
/// scheduler away: a dump whose retention job runs as the bootstrap superuser, or drops rows sooner than its hypertable's
/// bounds, is refused with each job named, and the database stays in restore mode, so neither runs. And the dump check takes
/// only the TimescaleDB tables a dump of TimescaleDB carries: a job's run state (here written into the dump by marking its
/// table as the extension's configuration, as a forger could) is refused before anything is restored. Its second round: a
/// restore that fails partway (here a dump whose last data block is cut short) also leaves the database in restore mode,
/// since the jobs it brought were never judged, and a job the gate cannot place (a compression policy on no hypertable) is
/// refused, not passed.
/// </summary>
public sealed class RestoreGateTests : IAsyncLifetime
{
    private const string StateDump = ChartDatabase.BackupDirectory + "/ariva-job-state.dump";
    private const string JobsDump = ChartDatabase.BackupDirectory + "/ariva-jobs.dump";
    private const string CutDump = ChartDatabase.BackupDirectory + "/ariva-cut.dump";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _workDirectory = Path.Combine(Path.GetTempPath(), "ariva-it-restore-gate-" + Guid.NewGuid().ToString("N"));
    private ChartDatabase _source;
    private ChartDatabase _target;
    private ChartDatabase _failing;

    public async ValueTask InitializeAsync()
    {
        var backup = Path.Combine(_workDirectory, "backup");
        Directory.CreateDirectory(backup);
        File.SetUnixFileMode(backup, (UnixFileMode)0b111_111_111);
        var servers = await Task.WhenAll(ChartDatabase.StartAsync(backup, Ct), ChartDatabase.StartAsync(backup, Ct), ChartDatabase.StartAsync(backup, Ct));
        (_source, _target, _failing) = (servers[0], servers[1], servers[2]);
    }

    public async ValueTask DisposeAsync()
    {
        if (_source is not null)
            await _source.DisposeAsync();
        if (_target is not null)
            await _target.DisposeAsync();
        if (_failing is not null)
            await _failing.DisposeAsync();
        if (Directory.Exists(_workDirectory))
            Directory.Delete(_workDirectory, recursive: true);
    }

    [Fact(Timeout = 900_000)]
    public async Task Restore_Should_RefuseAndKeepRestoreMode_When_TheDumpHoldsJobsThatWouldRunOtherwiseThanThisReleaseMakesThem()
    {
        var applied = await new SqlScriptRunner(_source.ConnectionString(ChartDatabase.MigrationLogin, _source.MigrationPassword), NullLogger<SqlScriptRunner>.Instance)
            .ApplyAsync(SqlScriptCatalog.Embedded(), Ct);
        applied.Should().HaveCount(SqlScriptCatalog.Embedded().Count);
        var superuser = _source.ConnectionString("postgres", _source.SuperuserPassword);

        // A dump that carries a job's run state, which pg_dump of TimescaleDB never writes: refused before anything is restored.
        await ExecuteAsync(superuser, MarkJobStateAsConfiguration);
        (await _source.OpsAsync(Ct, "backup", "--output", StateDump)).ExitCode.Should().Be(0);
        await ExecuteAsync(superuser, UnmarkJobStateAsConfiguration);
        var state = await _target.OpsAsync(Ct, "check-dump", "--input", StateDump);
        state.ExitCode.Should().NotBe(0, state.Stderr);
        state.Stderr.Should().Contain("refused: data of _timescaledb_internal.bgw_job_stat, which is not a chunk or a hypertable of TimescaleDB's")
            .And.Contain("nothing was restored");

        // A clean dump cut short in its data: the restore fails partway, and the database stays in restore mode,
        // since the jobs it brought were never judged (a forger could make any data block malformed after a forged job's row).
        await ExecuteAsync(_source.ConnectionString(ChartDatabase.MigrationLogin, _source.MigrationPassword), SeedData);
        (await _source.OpsAsync(Ct, "backup", "--output", CutDump)).ExitCode.Should().Be(0);
        var size = long.Parse((await _source.ExecAsync(Ct, "stat", "-c", "%s", CutDump)).Stdout.Trim(), System.Globalization.CultureInfo.InvariantCulture);
        // The table of contents is at the start of a custom-format dump and the data blocks follow it, so the last bytes are the
        // last data block's: the dump still lists, and pg_restore fails on that block.
        size.Should().BeGreaterThan(4096);
        (await _source.ExecAsync(Ct, "truncate", "-s", (size - 64).ToString(System.Globalization.CultureInfo.InvariantCulture), CutDump)).ExitCode.Should().Be(0);
        var cut = await _failing.OpsAsync(Ct, "restore", "--input", CutDump);
        cut.ExitCode.Should().NotBe(0, cut.Stderr);
        cut.Stderr.Should().Contain("the database stays in TimescaleDB's restore mode").And.Contain("the restore did not finish")
            .And.NotContain("leaving TimescaleDB's restore mode");
        (await ScalarAsync(_failing.ConnectionString("postgres", _failing.SuperuserPassword), "SHOW timescaledb.restoring")).Should().Be("on",
            "a restore that failed partway never ends restore mode");

        // A dump whose jobs run otherwise than this release makes them: refused once restored, before the scheduler starts.
        await ExecuteAsync(superuser, ForgeJobs);
        (await _source.OpsAsync(Ct, "backup", "--output", JobsDump)).ExitCode.Should().Be(0);
        var restore = await _target.OpsAsync(Ct, "restore", "--input", JobsDump);
        restore.ExitCode.Should().NotBe(0, restore.Stdout);
        restore.Stderr.Should().Contain("the dump holds TimescaleDB jobs this release does not create, which would run once restore mode ends: ")
            .And.Contain("_timescaledb_functions.policy_retention, owner postgres)", "a retention job made to run as the bootstrap superuser")
            .And.Contain("_timescaledb_functions.policy_retention, owner ariva)", "a retention job that drops rows sooner than its hypertable's bounds")
            .And.Contain("_timescaledb_functions.policy_compression, owner ariva)", "a compression job on no hypertable, which the gate cannot place")
            .And.Contain("the database stays in TimescaleDB's restore mode, so that no job of the dump runs")
            .And.NotContain("leaving TimescaleDB's restore mode");
        restore.Stdout.Should().BeEmpty("check does not run on a refused restore");
        (await ScalarAsync(_target.ConnectionString("postgres", _target.SuperuserPassword), "SHOW timescaledb.restoring")).Should().Be("on",
            "a new session of the database is still in restore mode, so TimescaleDB's scheduler runs none of its jobs");
    }

    #region Helpers

    // The bootstrap superuser's own forgery: TimescaleDB's configuration list grows by the job state table (pg_dump then writes
    // its data, an entry even when it has no rows), and is put back after the backup.
    private const string MarkJobStateAsConfiguration = """
        UPDATE pg_extension SET extconfig = array_append(extconfig, '_timescaledb_internal.bgw_job_stat'::regclass::oid), extcondition = array_append(extcondition, '')
         WHERE extname = 'timescaledb';
        """;

    private const string UnmarkJobStateAsConfiguration = """
        UPDATE pg_extension SET extconfig = extconfig[1:array_length(extconfig, 1) - 1], extcondition = extcondition[1:array_length(extcondition, 1) - 1]
         WHERE extname = 'timescaledb';
        """;

    private const string ForgeJobs = """
        UPDATE _timescaledb_catalog.bgw_job SET owner = 'postgres'
         WHERE proc_name = 'policy_retention' AND hypertable_id = (SELECT id FROM _timescaledb_catalog.hypertable WHERE table_name = 'sensing_event');
        UPDATE _timescaledb_catalog.bgw_job SET config = jsonb_set(config, '{drop_after}', '"1 day"')
         WHERE proc_name = 'policy_retention' AND hypertable_id = (SELECT id FROM _timescaledb_catalog.hypertable WHERE table_name = 'sensing_batch');
        INSERT INTO _timescaledb_catalog.bgw_job (application_name, schedule_interval, max_runtime, max_retries, retry_period, proc_schema, proc_name, owner,
                                                  scheduled, fixed_schedule, config, check_schema, check_name)
        VALUES ('It Compression', INTERVAL '1 day', INTERVAL '0', -1, INTERVAL '1 hour', '_timescaledb_functions', 'policy_compression', 'ariva',
                false, false, '{"compress_after": "1 day"}', '_timescaledb_functions', 'policy_compression_check');
        """;

    // Rows in a few hypertables, so that the dump has data blocks after its table of contents to cut.
    private const string SeedData = """
        INSERT INTO sensing_event (time_utc, site_code, queue_zone_name, kind, batch_id, ordinal, device_id, device_code, received_utc, flags, commissioned, track_id, x, y)
        SELECT t, 'DMO', 'IT-GATE', 'Track', gen_random_uuid(), 0, gen_random_uuid(), 'S-01', t, 0, true, 'trk', 1.0, 2.0
          FROM generate_series(now() - interval '3 days', now(), interval '1 minute') t;
        """;

    private static async Task ExecuteAsync(string connectionString, [System.Diagnostics.CodeAnalysis.ConstantExpected] string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // every caller passes one of the constants above (ConstantExpected)
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<string> ScalarAsync(string connectionString, [System.Diagnostics.CodeAnalysis.ConstantExpected] string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // the caller passes a constant (ConstantExpected)
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        return (string)await command.ExecuteScalarAsync(Ct);
    }

    #endregion
}
