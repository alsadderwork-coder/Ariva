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

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
