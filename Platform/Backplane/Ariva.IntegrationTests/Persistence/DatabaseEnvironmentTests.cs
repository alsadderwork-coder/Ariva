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
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DatabaseEnvironmentTests(PostgresFixture fixture)
{
    #region Fields

    private const string RuntimeLogin = "it_environment_app";
    private const string DevelopmentUser = "it.environment.dev";

    private static readonly string RuntimePassword = "rt-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string _deployed;

    #endregion

    #region Tests

    [Fact]
    public async Task Migration_Should_RecordItsEnvironment_And_TheRuntimeLoginOnlyReadIt()
    {
        var database = await DeployedAsync();

        await using var runtime = new NpgsqlConnection(fixture.ConnectionString(database, RuntimeLogin, RuntimePassword));
        await runtime.OpenAsync(Ct);
        await using (var read = new NpgsqlCommand("SELECT environment FROM database_environment WHERE id = 1", runtime))
            (await read.ExecuteScalarAsync(Ct)).Should().Be("k8s-dev", "the runtime login reads the record");

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
            (await change.Should().ThrowAsync<PostgresException>(sql)).Which.SqlState.Should().Be("42501", $"permission denied: {sql}");
        }

        (await RecordedAsync(database)).Should().Be("k8s-dev");
    }

    [Fact]
    public async Task LocalMigration_Should_RefuseTheDatabase_When_ItRecordsAnotherEnvironment()
    {
        var database = await DeployedAsync();

        (await MigrateAsync(database, "vm-local")).Should().Be(1, "a vm-local migration stops on another environment's database");
        await using (var provider = new ServiceCollection().AddLogging().AddArivaPersistence(Configuration(Settings(database, "vm-local"))).BuildServiceProvider())
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
        var settings = Settings(database, "vm-local");
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

    #endregion

    #region Helpers

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

    /// <summary>The migration job (api-main --migrate) as <paramref name="environment"/>, with the fixture's superuser as the migration login.</summary>
    private Task<int> MigrateAsync(string database, string environment) =>
        DatabaseMigration.RunAsync(new ConfigurationBuilder().AddInMemoryCollection(Settings(database, environment)).Build(), Ct);

    private Dictionary<string, string> Settings(string database, string environment) => new()
    {
        ["Application:Environment"] = environment,
        ["Database:Host"] = fixture.Hostname,
        ["Database:Port"] = fixture.Port.ToString(CultureInfo.InvariantCulture),
        ["Database:Name"] = database,
        ["Database:Migration:Username"] = fixture.AdminUsername,
        ["Database:Migration:Password"] = fixture.AdminPassword,
        ["Database:Username"] = RuntimeLogin,
        ["Database:Password"] = RuntimePassword
    };

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
