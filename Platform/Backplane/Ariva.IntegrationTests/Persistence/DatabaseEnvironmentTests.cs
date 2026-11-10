using System.Globalization;
using System.Security.Cryptography;
using Ariva.Core;
using Ariva.Di;
using Ariva.Di.Extensions;
using Ariva.Infra.Timescale;
using Ariva.IntegrationTests.Operations;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ariva.IntegrationTests.Persistence;

/// <summary>
/// ARV-180 (CWE-269, CWE-287) against PostgreSQL with script 0064: the migration job records its environment in the
/// database (<c>database_environment</c>), which the runtime login reads and cannot write, and a vm-local process (a developer's
/// host, the E2E suites), which migrates the database it is given and seeds the development accounts with their known
/// passwords, refuses a database that names another environment before doing either: the migration command, and Ariva.Api.Main
/// itself whether it migrates at start or only seeds. A vm-local database stays usable by vm-local.
/// <para>
/// ARV-083a: a cluster's migration job (k8s-dev, k8s-demo) ensures each host's own login and the availability ledger's
/// (<see cref="ClusterLogins"/>), and its hosts connect with them; a vm-local migration gives its shared development login
/// (<see cref="RuntimeLogin"/>) every host role. A login is one or the other, never both (ariva_ensure_host_login refuses a
/// login that holds another host's role), so a vm-local process sent to the deployed database here reaches it with that
/// database's own login, Ariva.Api.Main's (or Ariva.Api.Stream's for a replay), as a tunnel or a port forward to a deployed
/// store would.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DatabaseEnvironmentTests(PostgresFixture fixture)
{
    #region Fields

    private const string RuntimeLogin = "it_environment_app";
    private const string DevelopmentUser = "it.environment.dev";

    private static readonly string RuntimePassword = "rt-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

    /// <summary>
    /// ARV-083a: the cluster's host logins and the ledger's (Database:HostLogins, Database:Availability), made once for the class
    /// so every migration of a deployed database here sets the same passwords and a test can sign in with them afterwards.
    /// </summary>
    private static readonly Dictionary<string, string> ClusterLogins = HostLogins.ClusterSettings("it_env_");

    /// <summary>The hosts and the ledger, each with a login of its own in a cluster.</summary>
    private static readonly string[] ClusterLoginOwners = ["Main", "Ingest", "Stream", "Cronz", "Integration", "Availability"];

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string _deployed;

    #endregion

    #region Tests

    [Fact]
    public async Task Migration_Should_RecordItsEnvironment_And_TheRuntimeLoginOnlyReadIt()
    {
        var database = await DeployedAsync();

        // ARV-083a: the runtime logins of a cluster are each host's own and the ledger's (ariva_runtime and one host role): every
        // one reads the record (the base's grant) and none may change it (no host role adds a write).
        foreach (var owner in ClusterLoginOwners)
        {
            var (login, password) = ClusterLogin(owner);
            await using var runtime = new NpgsqlConnection(fixture.ConnectionString(database, login, password));
            await runtime.OpenAsync(Ct);
            await using (var read = new NpgsqlCommand("SELECT environment FROM database_environment WHERE id = 1", runtime))
                (await read.ExecuteScalarAsync(Ct)).Should().Be("k8s-dev", $"the runtime login of {owner} reads the record");

            foreach (var sql in new[]
                     {
                         "UPDATE database_environment SET environment = 'vm-local'",
                         "INSERT INTO database_environment (id, environment, set_on, set_by) VALUES (2, 'vm-local', now(), 'x')",
                         "DELETE FROM database_environment",
                         "TRUNCATE database_environment"
                     })
            {
#pragma warning disable CA2100 // literal statements from the array above
                await using var command = new NpgsqlCommand(sql, runtime);
#pragma warning restore CA2100
                var change = () => command.ExecuteNonQueryAsync(Ct);
                (await change.Should().ThrowAsync<PostgresException>($"{owner}: {sql}")).Which.SqlState.Should().Be("42501", $"permission denied to {owner}: {sql}");
            }
        }

        (await RecordedAsync(database)).Should().Be("k8s-dev");
    }

    [Fact]
    public async Task LocalMigration_Should_RefuseTheDatabase_When_ItRecordsAnotherEnvironment()
    {
        var database = await DeployedAsync();

        (await MigrateAsync(database, "vm-local", "Main")).Should().Be(1, "a vm-local migration stops on another environment's database");
        await using (var provider = new ServiceCollection().AddLogging().AddArivaPersistence(Configuration(Settings(database, "vm-local", "Main"))).BuildServiceProvider())
        {
            var migrate = () => provider.GetRequiredService<DatabaseMigrator>().MigrateAsync(Ct);
            await migrate.Should().ThrowAsync<InvalidOperationException>().WithMessage("The database belongs to the environment 'k8s-dev'*vm-local*ARV-180*",
                "the refusal is the record's, not another failure");
        }

        (await RecordedAsync(database)).Should().Be("k8s-dev", "nothing changed");
    }

    /// <summary>
    /// The guard checks in StartingAsync, before any hosted service starts, and the hosts start their services one after
    /// another whatever <c>servicesStartConcurrently</c> says: with it true, before the review's finding H1 was fixed, both
    /// refusals were raised while the seed ran beside them and wrote its account.
    /// </summary>
    [Theory]
    [InlineData(true, false, false)]  // migrates at start: refused before SchemaUpdate and before the seed
    [InlineData(false, false, false)] // only seeds the development accounts, with the runtime login
    [InlineData(true, true, false)]   // the same with the services asked to start at once
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]  // only seeds the demo topology (no development account)
    public async Task LocalMain_Should_RefuseToStartAndSeedNothing_When_TheDatabaseRecordsAnotherEnvironment(bool migrates, bool atOnce, bool demoOnly)
    {
        var database = await DeployedAsync();
        var user = $"{DevelopmentUser}.{(migrates ? "m" : "s")}{(atOnce ? "c" : "o")}{(demoOnly ? "d" : "u")}";
        var settings = Settings(database, "vm-local", "Main");
        settings["Database:AllowSchemaUpdate"] = migrates ? "true" : "false";
        settings["Database:VerifySchemaOnStartup"] = "false";
        settings["servicesStartConcurrently"] = atOnce ? "true" : "false";
        if (demoOnly)
        {
            settings["Seed:DemoTopology"] = "true";
        }
        else
        {
            settings["Auth:DevelopmentUsers:0:UserName"] = user;
            settings["Auth:DevelopmentUsers:0:Password"] = "known-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
            settings["Auth:DevelopmentUsers:0:Roles:0"] = RoleCodes.SystemAdministrator;
        }

        await using var main = new DeployedHost<Ariva.Api.Main._IAssemblyMark>(settings, "vm-local");
        var start = () => main.Client();

        start.Should().Throw<InvalidOperationException>().WithMessage("*belongs to the environment 'k8s-dev'*vm-local*");
        (await AccountsNamedAsync(database, user)).Should().Be(0, "no development account was seeded");
        (await SitesNamedAsync(database, "DMO")).Should().Be(0, "nor the demo topology");
        (await RecordedAsync(database)).Should().Be("k8s-dev");
    }

    [Fact]
    public async Task LocalMigration_Should_UseItsOwnDatabase_And_StopOnceAnotherEnvironmentRecordsIt()
    {
        var database = await fixture.CreateDatabaseAsync(TestDatabase.EnvironmentLocal);

        (await MigrateAsync(database, "vm-local")).Should().Be(0);
        (await RecordedAsync(database)).Should().Be("vm-local", "a fresh database records the environment that migrated it");
        (await MigrateAsync(database, "vm-local")).Should().Be(0, "its own database");

        (await MigrateAsync(database, "k8s-demo")).Should().Be(0, "another environment's job records itself (and warns of the change)");
        (await RecordedAsync(database)).Should().Be("k8s-demo");
        (await MigrateAsync(database, "vm-local")).Should().Be(1, "from then on vm-local stops on it");
    }

    [Fact]
    public async Task LocalReplay_Should_StopBeforeReadingOrWriting_When_TheDatabaseRecordsAnotherEnvironment()
    {
        // ARV-260: the replay command writes its run into the database it is given. Run as vm-local it stops on another
        // environment's database (exit 4) before it reads the site's profile; the same replay as the database's own environment
        // gets as far as the profile, which this migrated database lacks (exit 1, nothing written either way).
        var database = await DeployedAsync();
        string[] replay = ["--replay", "--replay-site=ENV", "--replay-from=2026-09-28T17:00:00Z", "--replay-to=2026-09-28T17:10:00Z"];

        using var local = new StringWriter();
        (await ReplayCommand.RunAsync(Configuration(Settings(database, "vm-local", "Stream")), replay, local, Ct)).Should().Be(4,
            "a vm-local replay stops on another environment's database");
        using var own = new StringWriter();
        (await ReplayCommand.RunAsync(Configuration(Settings(database, "k8s-dev", "Stream")), replay, own, Ct)).Should().Be(1,
            "the database's own environment reads on and fails on the missing profile");

        local.ToString().Should().BeEmpty("no run summary");
        (await ReplayRunsAsync(database)).Should().Be(0, "no run was written");
        (await RecordedAsync(database)).Should().Be("k8s-dev");
    }

    [Fact]
    public async Task LocalReplay_Should_ReadOn_When_TheDatabaseIsVmLocals()
    {
        var database = await fixture.CreateDatabaseAsync(TestDatabase.EnvironmentReplay);
        (await MigrateAsync(database, "vm-local")).Should().Be(0);
        string[] replay = ["--replay", "--replay-site=ENV", "--replay-from=2026-09-28T17:00:00Z", "--replay-to=2026-09-28T17:10:00Z"];

        using var output = new StringWriter();
        (await ReplayCommand.RunAsync(Configuration(Settings(database, "vm-local")), replay, output, Ct)).Should().Be(1,
            "its own database: the replay reads on and fails on the missing profile, not on the environment");
        // The check itself, on its own: no problem with vm-local's own database, and the record's problem with another's.
        await using (var own = new ServiceCollection().AddLogging().AddArivaPersistence(Configuration(Settings(database, "vm-local"))).BuildServiceProvider())
            (await ReplayCommand.AnotherEnvironmentsAsync(own, Ct)).Should().BeNull("vm-local may replay into its own database");
        await using (var deployed = new ServiceCollection().AddLogging().AddArivaPersistence(Configuration(Settings(await DeployedAsync(), "vm-local", "Stream"))).BuildServiceProvider())
            (await ReplayCommand.AnotherEnvironmentsAsync(deployed, Ct)).Should().Contain("belongs to the environment 'k8s-dev'");
    }

    #endregion

    #region Helpers

    private async Task<long> ReplayRunsAsync(string database)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(database));
        await connection.OpenAsync(Ct);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM replay_run", connection);
        return (long)(await count.ExecuteScalarAsync(Ct) ?? 0L);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A database the k8s-dev migration job migrated, once for the class.</summary>
    private async Task<string> DeployedAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            if (_deployed is null)
            {
                var database = await fixture.CreateDatabaseAsync(TestDatabase.EnvironmentDeployed);
                (await MigrateAsync(database, "k8s-dev")).Should().Be(0);
                _deployed = database;
            }

            return _deployed;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// The migration job (api-main --migrate) as <paramref name="environment"/>, with the fixture's superuser as the migration
    /// login; <paramref name="host"/> as in <see cref="Settings"/>.
    /// </summary>
    private Task<int> MigrateAsync(string database, string environment, string host = null) =>
        DatabaseMigration.RunAsync(new ConfigurationBuilder().AddInMemoryCollection(Settings(database, environment, host)).Build(), Ct);

    /// <summary>
    /// A process's settings as <paramref name="environment"/>. ARV-083a: in a cluster (k8s-*) the migration job is given every
    /// host's login and the ledger's (<see cref="ClusterLogins"/>), and the process connects as <paramref name="host"/>'s login
    /// (Ariva.Api.Main's by default, whose job migrates). A vm-local process connects with the shared development login, or with
    /// <paramref name="host"/>'s cluster login when it is sent to the deployed database.
    /// </summary>
    private Dictionary<string, string> Settings(string database, string environment, string host = null)
    {
        var cluster = environment?.StartsWith("k8s-", StringComparison.Ordinal) == true;
        var (login, password) = cluster || host is not null ? ClusterLogin(host ?? "Main") : (RuntimeLogin, RuntimePassword);
        var settings = new Dictionary<string, string>
        {
            ["Application:Environment"] = environment,
            ["Database:Host"] = fixture.Hostname,
            ["Database:Port"] = fixture.Port.ToString(CultureInfo.InvariantCulture),
            ["Database:Name"] = database,
            ["Database:Migration:Username"] = fixture.AdminUsername,
            ["Database:Migration:Password"] = fixture.AdminPassword,
            ["Database:Username"] = login,
            ["Database:Password"] = password
        };
        if (cluster)
        {
            foreach (var (key, value) in ClusterLogins)
                settings[key] = value;
        }

        return settings;
    }

    /// <summary>The cluster login of a host (Main, Ingest, Stream, Cronz, Integration) or of the availability ledger (Availability).</summary>
    private static (string Login, string Password) ClusterLogin(string owner)
    {
        var section = owner == "Availability" ? "Database:Availability" : $"Database:HostLogins:{owner}";
        return (ClusterLogins[$"{section}:Username"], ClusterLogins[$"{section}:Password"]);
    }

    private static IConfiguration Configuration(Dictionary<string, string> settings) => new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private async Task<long> SitesNamedAsync(string database, string code)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(database));
        await connection.OpenAsync(Ct);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM site WHERE code = @code", connection);
        count.Parameters.AddWithValue("code", code);
        return (long)(await count.ExecuteScalarAsync(Ct) ?? 0L);
    }

    private async Task<string> RecordedAsync(string database)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(database));
        await connection.OpenAsync(Ct);
        await using var read = new NpgsqlCommand("SELECT environment FROM database_environment WHERE id = 1", connection);
        return await read.ExecuteScalarAsync(Ct) as string;
    }

    private async Task<long> AccountsNamedAsync(string database, string name)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(database));
        await connection.OpenAsync(Ct);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM \"user\" WHERE user_name = @name", connection);
        count.Parameters.AddWithValue("name", name);
        return (long)(await count.ExecuteScalarAsync(Ct) ?? 0L);
    }

    #endregion
}
