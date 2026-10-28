using System.Security.Cryptography;
using Ariva.Infra.Retention;
using Ariva.Infra.Settings;
using Ariva.Infra.Timescale;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Ariva.IntegrationTests.Persistence;

/// <summary>
/// ARV-259c against TimescaleDB with the scripts applied, in a container of its own: TimescaleDB runs a database's jobs from one
/// background scheduler per database, within <c>timescaledb.max_background_workers</c> (16 in the image), and the shared
/// container's many test databases take every one, so a job planted there may never run (a story gate's full suite); and
/// <c>run_job</c> runs a job in the session without recording it in <c>job_stats</c>. The scripts' policy jobs belong to the
/// superuser that applied them (the migration login in a cluster); the hosts' runtime login reads their last runs through
/// <c>timescaledb_information.job_stats</c>, as <see cref="TimescalePolicyJobs"/> does.
/// </summary>
public sealed class PolicyJobsTests : IAsyncLifetime
{
    private const string Database = "ariva_policy_jobs";

    private const string RuntimeLogin = "it_policy_jobs_app";

    private readonly string _password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private readonly string _runtimePassword = "rt-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

    private PostgreSqlContainer _container;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _container = new PostgreSqlBuilder()
            .WithImage(PostgresFixture.Image)
            .WithDatabase(Database)
            .WithUsername("postgres")
            .WithPassword(_password)
            .WithEnvironment("TIMESCALEDB_TELEMETRY", "off")
            .Build();
        await _container.StartAsync(Ct);
        await new SqlScriptRunner(Admin, NullLogger<SqlScriptRunner>.Instance).ApplyAsync(SqlScriptCatalog.Embedded());
        await using var admin = new NpgsqlConnection(Admin);
        await admin.OpenAsync(Ct);
        await using var ensure = new NpgsqlCommand("SELECT ariva_ensure_runtime_login(@login, @password)", admin);
        ensure.Parameters.AddWithValue("login", RuntimeLogin);
        ensure.Parameters.AddWithValue("password", _runtimePassword);
        await ensure.ExecuteNonQueryAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    private string Admin => _container.GetConnectionString();

    private DatabaseSettings Runtime => new()
    {
        Host = _container.Hostname, Port = _container.GetMappedPublicPort(PostgreSqlBuilder.PostgreSqlPort), Name = Database,
        Username = RuntimeLogin, Password = _runtimePassword
    };

    [Fact]
    public async Task PolicyJobs_Should_CountTheFailedPolicyJobsByKind_When_TheRuntimeLoginReadsThem()
    {
        var policyJobs = new TimescalePolicyJobs(Runtime);
        (await policyJobs.FailedAsync(Ct)).Should().Equal([("compression", 0), ("refresh", 0), ("retention", 0)], "the scripts' policies, none failed");

        // A retention policy job whose hypertable does not exist, and two failing jobs that are not TimescaleDB's policies: another
        // procedure in TimescaleDB's own schema, and one in public that has a policy's name. The scheduler runs the three now.
        await ExecuteAsync("""
            CREATE PROCEDURE _timescaledb_functions.it_planted_failure(job_id integer, config jsonb) LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'planted failure'; END $$;
            CREATE PROCEDURE public.policy_compression(job_id integer, config jsonb) LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'planted failure'; END $$;
            SELECT add_job('_timescaledb_functions.policy_retention', INTERVAL '1 hour', config => '{"hypertable_id": 0, "drop_after": "1 day"}', initial_start => now());
            SELECT add_job('_timescaledb_functions.it_planted_failure', INTERVAL '1 hour', initial_start => now());
            SELECT add_job('public.policy_compression', INTERVAL '1 hour', initial_start => now());
            """);
        var ran = 0L;
        for (var attempt = 0; attempt < 240 && ran < 3; attempt++)
        {
            await Task.Delay(500, Ct);
            ran = await ScalarAsync<long>("""
                SELECT count(*) FROM timescaledb_information.job_stats s JOIN timescaledb_information.jobs j ON j.job_id = s.job_id
                 WHERE (j.proc_name = 'it_planted_failure' OR j.proc_schema = 'public' OR j.config ->> 'hypertable_id' = '0') AND s.last_run_status = 'Failed'
                """);
        }

        ran.Should().Be(3, "TimescaleDB's scheduler ran the three jobs, and each failed");
        (await ScalarAsync<long>("""
            SELECT count(*) FROM timescaledb_information.job_errors e JOIN timescaledb_information.jobs j ON j.job_id = e.job_id
             WHERE j.config ->> 'hypertable_id' = '0'
            """)).Should().BeGreaterThan(0, "wiki 10 section 4.8m sends the operator to job_errors for the reason");
        var failed = await policyJobs.FailedAsync(Ct);
        failed.Should().Equal([("compression", 0), ("refresh", 0), ("retention", 1)], "the failed policy counts; the other procedures' jobs do not");

        using var metrics = new RetentionMetrics();
        using var reader = new InstrumentReader(metrics.Meter);
        metrics.PolicyJobs(failed, TimeSpan.FromMinutes(25));
        reader.Observe(RetentionMetrics.PolicyJobsFailedName).ToDictionary(m => m.Tags["kind"], m => m.Value, StringComparer.Ordinal)
            .Should().BeEquivalentTo(new Dictionary<string, double> { ["compression"] = 0, ["refresh"] = 0, ["retention"] = 1 });

        await ExecuteAsync("""
            SELECT delete_job(job_id) FROM timescaledb_information.jobs
             WHERE proc_name = 'it_planted_failure' OR proc_schema = 'public' OR config ->> 'hypertable_id' = '0'
            """);
        (await policyJobs.FailedAsync(Ct)).Should().Equal([("compression", 0), ("refresh", 0), ("retention", 0)], "a deleted job counts no more");
    }

    private async Task ExecuteAsync([System.Diagnostics.CodeAnalysis.ConstantExpected] string sql)
    {
        await using var connection = new NpgsqlConnection(Admin);
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // every caller passes a literal (ConstantExpected)
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<T> ScalarAsync<T>([System.Diagnostics.CodeAnalysis.ConstantExpected] string sql)
    {
        await using var connection = new NpgsqlConnection(Admin);
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // every caller passes a literal (ConstantExpected)
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        return (T)await command.ExecuteScalarAsync(Ct);
    }
}
