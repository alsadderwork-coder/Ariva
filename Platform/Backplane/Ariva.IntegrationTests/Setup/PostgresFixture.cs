using Ariva.IntegrationTests.Persistence.Samples;
using Testcontainers.PostgreSql;

namespace Ariva.IntegrationTests.Setup;

/// <summary>
/// One PostgreSQL container per test run, on the same TimescaleDB image as docker-compose.dev.yml, with the sample
/// schema created through the production persistence registration.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>Keep in step with docker-compose.dev.yml.</summary>
    public const string Image = "timescale/timescaledb-ha:pg17-ts2.30";

    private const string Database = "ariva_it";
    private const string Username = "ariva";

    // A throwaway password for a container that lives for one test run and listens on a random local port.
    private readonly string _password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

    private PostgreSqlContainer _container;

    public PersistenceHost Host { get; private set; }

    public string Hostname => _container.Hostname;

    public int Port => _container.GetMappedPublicPort(PostgreSqlBuilder.PostgreSqlPort);

    /// <summary>The container's superuser, which plays the migration login in the script runner tests.</summary>
    public string AdminUsername => Username;

    public string AdminPassword => _password;

    /// <summary>
    /// A fresh, empty database for a test that owns its whole schema (the script runner keeps its state per
    /// database). CREATE DATABASE takes no bind parameters, so every name is a constant here.
    /// </summary>
    public async Task<string> CreateDatabaseAsync(TestDatabase database)
    {
        // A switch over string literals keeps the command text constant for CA2100.
        var sql = database switch
        {
            TestDatabase.RunnerOrder => "CREATE DATABASE it_runner_order",
            TestDatabase.RunnerRerun => "CREATE DATABASE it_runner_rerun",
            TestDatabase.RunnerTamper => "CREATE DATABASE it_runner_tamper",
            TestDatabase.RunnerFailure => "CREATE DATABASE it_runner_failure",
            TestDatabase.RunnerVerify => "CREATE DATABASE it_runner_verify",
            TestDatabase.RunnerConcurrent => "CREATE DATABASE it_runner_concurrent",
            TestDatabase.Roles => "CREATE DATABASE it_roles",
            TestDatabase.DataProtection => "CREATE DATABASE it_dataprotection",
            TestDatabase.RuntimeLogin => "CREATE DATABASE it_runtime_login",
            TestDatabase.Accounts => "CREATE DATABASE it_accounts",
            TestDatabase.Administration => "CREATE DATABASE it_administration",
            TestDatabase.Messaging => "CREATE DATABASE it_messaging",
            TestDatabase.Kafka => "CREATE DATABASE it_kafka",
            TestDatabase.DemoSeed => "CREATE DATABASE it_demo_seed",
            TestDatabase.DemoSeedConflict => "CREATE DATABASE it_demo_seed_conflict",
            TestDatabase.DemoSeedDraft => "CREATE DATABASE it_demo_seed_draft",
            TestDatabase.DeviceHealth => "CREATE DATABASE it_device_health",
            TestDatabase.SensingArchive => "CREATE DATABASE it_sensing_archive",
            TestDatabase.StreamStraight => "CREATE DATABASE it_stream_straight",
            TestDatabase.StreamRestart => "CREATE DATABASE it_stream_restart",
            TestDatabase.StreamStore => "CREATE DATABASE it_stream_store",
            TestDatabase.StreamDeadLetter => "CREATE DATABASE it_stream_dead_letter",
            TestDatabase.StreamRoles => "CREATE DATABASE it_stream_roles",
            TestDatabase.StreamMisplaced => "CREATE DATABASE it_stream_misplaced",
            TestDatabase.StreamScaleOut => "CREATE DATABASE it_stream_scale_out",
            TestDatabase.Replay => "CREATE DATABASE it_replay",
            TestDatabase.AlertRules => "CREATE DATABASE it_alert_rules",
            TestDatabase.AlertEvaluation => "CREATE DATABASE it_alert_evaluation",
            TestDatabase.AlertLifecycle => "CREATE DATABASE it_alert_lifecycle",
            TestDatabase.AlertEmails => "CREATE DATABASE it_alert_emails",
            TestDatabase.Flights => "CREATE DATABASE it_flights",
            TestDatabase.IntegrationClients => "CREATE DATABASE it_integration_clients",
            _ => throw new ArgumentOutOfRangeException(nameof(database))
        };
        var name = sql["CREATE DATABASE ".Length..];

        await using var connection = new Npgsql.NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
#pragma warning disable CA2100 // every branch of the switch above is a string literal; no input reaches the statement
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync();
        return name;
    }

    public string ConnectionString(string database, string username = null, string password = null) =>
        new Npgsql.NpgsqlConnectionStringBuilder
        {
            Host = Hostname,
            Port = Port,
            Database = database,
            Username = username ?? Username,
            Password = password ?? _password,
            Pooling = false
        }.ConnectionString;

    public async ValueTask InitializeAsync()
    {
        _container = new PostgreSqlBuilder()
            .WithImage(Image)
            .WithDatabase(Database)
            .WithUsername(Username)
            .WithPassword(_password)
            .WithEnvironment("TIMESCALEDB_TELEMETRY", "off")
            .Build();
        await _container.StartAsync();

        Host = new PersistenceHost(_container.Hostname, _container.GetMappedPublicPort(PostgreSqlBuilder.PostgreSqlPort), Database, Username, _password);
        Host.UpdateSchema();
    }

    public async ValueTask DisposeAsync()
    {
        if (Host is not null)
            await Host.DisposeAsync();
        if (_container is not null)
            await _container.DisposeAsync();
    }
}

/// <summary>One database per script runner test; each name is created once per container.</summary>
public enum TestDatabase
{
    RunnerOrder,
    RunnerRerun,
    RunnerTamper,
    RunnerFailure,
    RunnerVerify,
    RunnerConcurrent,
    Roles,
    DataProtection,
    RuntimeLogin,
    Accounts,
    Administration,
    Messaging,
    Kafka,
    DemoSeed,
    DemoSeedConflict,
    DemoSeedDraft,
    DeviceHealth,
    SensingArchive,
    StreamStraight,
    StreamRestart,
    StreamStore,
    StreamDeadLetter,
    StreamRoles,
    StreamMisplaced,
    StreamScaleOut,
    Replay,
    AlertRules,
    AlertEvaluation,
    AlertLifecycle,
    AlertEmails,
    Flights,
    IntegrationClients
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
