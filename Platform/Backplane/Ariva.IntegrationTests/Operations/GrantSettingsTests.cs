using Ariva.Di;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Ariva.IntegrationTests.Operations;

/// <summary>
/// ARV-290 (CWE-400, CWE-269): the runtime login's temporary files capped on a server started as the TimescaleDB chart starts one
/// (<see cref="ChartDatabase"/>). The chart's init script gives the migration login SET on temp_file_limit, so the release's
/// migration job, as that non-superuser owner, caps the runtime login and the check passes. On a server first started before
/// ARV-290 (here: the grant taken back) the check fails and the job stops with the grant to run, setting nothing;
/// <c>ariva-db.sh grant-settings</c> (no option) then grants it to ariva_migration as the superuser, after which the check passes
/// and the job caps the runtime login again. Its review: the check names any other privilege on a parameter and the runtime
/// login's own caps, and a server prepared without the grant stops the job before its first script.
/// </summary>
public sealed class GrantSettingsTests : IAsyncLifetime
{
    private const string RuntimeLogin = "ariva_app";
    private const string Superuser = "postgres";

    private static readonly string RuntimePassword = "rt-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _workDirectory = Path.Combine(Path.GetTempPath(), "ariva-it-grant-settings-" + Guid.NewGuid().ToString("N"));
    private ChartDatabase _server;

    public async ValueTask InitializeAsync()
    {
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

    [Fact(Timeout = 600_000)]
    public async Task GrantSettings_Should_LetTheMigrationCapTheRuntimeLogin_When_TheServerStartedBeforeArv290()
    {
        // A new volume: the init script granted it, so the job caps the runtime login and the check passes.
        (await MigrateAsync()).Should().Be(0);
        (await RuntimeScalarAsync("SHOW temp_file_limit")).Should().Be("1GB");
        var granted = await _server.OpsAsync(Ct, "check", "--runtime-login", RuntimeLogin);
        granted.Stdout.Should().Contain("temp file cap|the migration login may set temp_file_limit on the runtime login (ARV-290)")
            .And.Contain("role settings|none but the runtime login's statement timeout, temporary file cap, idle transaction limit and keepalives")
            .And.Contain("ok    parameter privileges|SET on temp_file_limit to the migration login or ariva_migration only")
            .And.Contain($"ok    runtime caps|{RuntimeLogin} in this database: statement_timeout ").And.Contain(", temp_file_limit 1024MB").And.NotContain("FAIL", granted.Stdout);

        // A server first started before ARV-290: neither the login nor ariva_migration holds SET on it, and no cap is set.
        await SuperuserAsync("REVOKE SET ON PARAMETER temp_file_limit FROM ariva");
        await SuperuserAsync("ALTER ROLE ariva_app IN DATABASE ariva RESET temp_file_limit");
        (await SuperuserScalarAsync("SELECT has_parameter_privilege('ariva', 'temp_file_limit', 'SET')")).Should().Be("f");
        NpgsqlConnection.ClearAllPools();

        var before = await _server.OpsAsync(Ct, "check", "--runtime-login", RuntimeLogin);
        before.Stdout.Should().Contain("FAIL  temp file cap|the migration login may not set temp_file_limit, so the migration job stops (ARV-290): run grant-settings")
            .And.Contain($"FAIL  runtime caps|{RuntimeLogin} in this database: temp_file_limit not set (the migration job sets 301s to 3600s, 64MB to 102400MB, 300s to 7200s and keepalives 60/10/6; ARV-096d, ARV-290, ARV-292): run the migration job");
        // The job logs "The migration login ariva may not set temp_file_limit ... GRANT SET ON PARAMETER ... grant-settings" (Critical)
        // and exits 1, as for any failure, so the release's hook fails before a host starts.
        (await MigrateAsync()).Should().Be(1, "the job stops when its login may not set the cap");
        (await RuntimeScalarAsync("SHOW temp_file_limit")).Should().Be("-1", "the stopped job capped nothing");

        // grant-settings takes no option, then grants SET to ariva_migration, whose privileges the migration login holds.
        (await _server.OpsAsync(Ct, "grant-settings", "--login", "ariva", "--to", "someone")).ExitCode.Should().Be(2);
        var grant = await _server.OpsAsync(Ct, "grant-settings");
        grant.ExitCode.Should().Be(0, grant.Stderr);
        grant.Stderr.Should().Contain("granting ariva_migration, and so ariva, SET on temp_file_limit (superuser over the local socket)")
            .And.Contain("done: run the migration job again");
        (await SuperuserScalarAsync("SELECT has_parameter_privilege('ariva_migration', 'temp_file_limit', 'SET')")).Should().Be("t");
        (await SuperuserScalarAsync("SELECT has_parameter_privilege('ariva_app', 'temp_file_limit', 'SET')")).Should().Be("f", "never the runtime login");

        var after = await _server.OpsAsync(Ct, "check", "--runtime-login", RuntimeLogin);
        after.Stdout.Should().Contain("temp file cap|the migration login may set temp_file_limit").And.NotContain("FAIL  temp file cap")
            .And.Contain("ok    parameter privileges|SET on temp_file_limit to the migration login or ariva_migration only", "grant-settings' own grant passes");
        (await MigrateAsync()).Should().Be(0);
        (await RuntimeScalarAsync("SHOW temp_file_limit")).Should().Be("1GB");
        (await _server.OpsAsync(Ct, "check", "--runtime-login", RuntimeLogin)).Stdout.Should().Contain($"ok    runtime caps|{RuntimeLogin} in this database: ")
            .And.NotContain("FAIL", "the job capped the runtime login again");

        // Its review: the migration login, which may set the cap, raises both past what the job writes (well formed, out of the
        // settings' bounds): the check names each, and the next migration sets them back.
        await MigrationLoginAsync("ALTER ROLE ariva_app IN DATABASE ariva SET temp_file_limit = '102401MB'");
        await MigrationLoginAsync("ALTER ROLE ariva_app IN DATABASE ariva SET statement_timeout = '3601s'");
        (await _server.OpsAsync(Ct, "check", "--runtime-login", RuntimeLogin)).Stdout.Should()
            .Contain($"FAIL  runtime caps|{RuntimeLogin} in this database: statement_timeout 3601s, temp_file_limit 102401MB (the migration job sets ");
        (await MigrateAsync()).Should().Be(0);
        (await _server.OpsAsync(Ct, "check", "--runtime-login", RuntimeLogin)).Stdout.Should().Contain($"ok    runtime caps|{RuntimeLogin} in this database: ");
        (await _server.OpsAsync(Ct, "grant-settings")).ExitCode.Should().Be(0, "a second grant changes nothing");
    }

    [Fact(Timeout = 600_000)]
    public async Task Migration_Should_StopBeforeAnyScript_When_TheMigrationLoginMayNotSetTheCap()
    {
        // A server prepared without the chart's grant: the job stops before its first script, so nothing changes, and with the
        // release's pre-upgrade hook failed the hosts already running keep serving.
        await SuperuserAsync("REVOKE SET ON PARAMETER temp_file_limit FROM ariva");
        (await MigrateAsync()).Should().Be(1, "the job stops when its login may not set the cap");
        (await SuperuserScalarAsync("SELECT to_regclass('public.schema_version') IS NULL")).Should().Be("t", "no script ran");
        (await SuperuserScalarAsync("SELECT count(*) FROM pg_roles WHERE rolname IN ('ariva_app', 'ariva_migration', 'ariva_runtime')")).Should().Be("0",
            "no login or group role was created");

        // grant-settings grants ariva_migration, which no migration has created yet: it says what to do instead and grants nothing.
        var grant = await _server.OpsAsync(Ct, "grant-settings");
        grant.ExitCode.Should().NotBe(0);
        grant.Stderr.Should().Contain("ariva_migration does not exist: no migration has run on this server yet");
        (await SuperuserScalarAsync("SELECT count(*) FROM pg_parameter_acl p CROSS JOIN LATERAL aclexplode(p.paracl) a WHERE p.parname = 'temp_file_limit' AND a.grantee <> 10"))
            .Should().Be("0");

        // The superuser's grant to the migration login, as the chart's init script gives it, lets the job run.
        await SuperuserAsync("GRANT SET ON PARAMETER temp_file_limit TO ariva");
        (await MigrateAsync()).Should().Be(0);
        (await RuntimeScalarAsync("SHOW temp_file_limit")).Should().Be("1GB");
    }

    private Task<int> MigrateAsync()
    {
        NpgsqlConnection.ClearAllPools();
        return DatabaseMigration.RunAsync(new ConfigurationBuilder().AddInMemoryCollection(_server.MigrationSettings(RuntimeLogin, RuntimePassword)).Build(), Ct);
    }

    private async Task<string> RuntimeScalarAsync([System.Diagnostics.CodeAnalysis.ConstantExpected] string sql)
    {
        await using var connection = new NpgsqlConnection(_server.ConnectionString(RuntimeLogin, RuntimePassword));
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // test helper: every caller passes a literal (ConstantExpected, CA1857 is an error)
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        return Convert.ToString(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task MigrationLoginAsync([System.Diagnostics.CodeAnalysis.ConstantExpected] string sql)
    {
        await using var connection = new NpgsqlConnection(_server.ConnectionString(ChartDatabase.MigrationLogin, _server.MigrationPassword));
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // test helper: every caller passes a literal (ConstantExpected, CA1857 is an error)
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task SuperuserAsync(string sql)
    {
        var result = await Psql(sql);
        result.ExitCode.Should().Be(0, $"{sql}: {result.Stderr}");
    }

    private async Task<string> SuperuserScalarAsync(string sql)
    {
        var result = await Psql(sql);
        result.ExitCode.Should().Be(0, result.Stderr);
        return result.Stdout.Trim();
    }

    private Task<ExecResult> Psql(string sql) => _server.ExecAsync(Ct, "psql", "--no-psqlrc", "--quiet", "--tuples-only", "--no-align", "--set=ON_ERROR_STOP=1",
        "--host=/var/run/postgresql", "--username=" + Superuser, "--dbname=" + ChartDatabase.Database, "--command=" + sql);
}
