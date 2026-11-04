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
/// and the job caps the runtime login again.
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
            .And.Contain("role settings|none but the runtime login's statement timeout and temporary file cap").And.NotContain("FAIL", granted.Stdout);

        // A server first started before ARV-290: neither the login nor ariva_migration holds SET on it, and no cap is set.
        await SuperuserAsync("REVOKE SET ON PARAMETER temp_file_limit FROM ariva");
        await SuperuserAsync("ALTER ROLE ariva_app IN DATABASE ariva RESET temp_file_limit");
        (await SuperuserScalarAsync("SELECT has_parameter_privilege('ariva', 'temp_file_limit', 'SET')")).Should().Be("f");
        NpgsqlConnection.ClearAllPools();

        var before = await _server.OpsAsync(Ct, "check", "--runtime-login", RuntimeLogin);
        before.Stdout.Should().Contain("FAIL  temp file cap|the migration login may not set temp_file_limit, so the migration job stops (ARV-290): run grant-settings");
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
        after.Stdout.Should().Contain("temp file cap|the migration login may set temp_file_limit").And.NotContain("FAIL  temp file cap");
        (await MigrateAsync()).Should().Be(0);
        (await RuntimeScalarAsync("SHOW temp_file_limit")).Should().Be("1GB");
        (await _server.OpsAsync(Ct, "grant-settings")).ExitCode.Should().Be(0, "a second grant changes nothing");
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
