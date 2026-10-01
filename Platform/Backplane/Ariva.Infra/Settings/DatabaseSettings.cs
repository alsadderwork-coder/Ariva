using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Ariva.Infra.Settings;

/// <summary>
/// The "Database" configuration section. The connection string is built with <see cref="NpgsqlConnectionStringBuilder"/>
/// from its parts rather than by text substitution, so a password containing ';' or '=' cannot add connection
/// options. The password comes from the environment or a Kubernetes secret (Database__Password), never from a
/// committed file.
/// </summary>
public sealed class DatabaseSettings
{
    public const string SectionName = "Database";

    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 5432;
    public string Name { get; init; } = "ariva";
    public string Username { get; init; } = "ariva";
    public string Password { get; init; } = string.Empty;

    /// <summary>TLS to PostgreSQL with full certificate and host name verification (CWE-295).</summary>
    public bool UseEncryption { get; init; }

    /// <summary>
    /// Lets NHibernate SchemaUpdate change the schema at startup. Only appsettings.base.vm-local.json sets it; a unit
    /// test fails the build if any other environment does. Production schema comes from versioned scripts (ARV-006).
    /// </summary>
    public bool AllowSchemaUpdate { get; init; }

    /// <summary>
    /// Hosts check at startup that every shipped script is applied unchanged and stop otherwise (ARV-006). Off in
    /// vm-local, where hosts migrate the development database themselves.
    /// </summary>
    public bool VerifySchemaOnStartup { get; init; }

    /// <summary>The login the migration job uses (DDL). Hosts never use it.</summary>
    public MigrationLoginSettings Migration { get; init; } = new();

    public NHibernateSettings NHibernate { get; init; } = new();

    /// <summary>False when no separate migration login is configured or it is the same login as the hosts'.</summary>
    public bool UsesSeparateRuntimeLogin =>
        !string.IsNullOrWhiteSpace(Migration.Username) &&
        !string.Equals(Migration.Username, Username, StringComparison.Ordinal);

    public static DatabaseSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.GetSection(SectionName).Get<DatabaseSettings>() ?? new DatabaseSettings();
    }

    /// <summary>The hosts' connection: the runtime login, DML only once the migration job has run.</summary>
    public string BuildConnectionString() => Build(Username, Password, "ariva");

    /// <summary>The migration job's connection; falls back to the runtime login when no migration login is set.</summary>
    public string BuildMigrationConnectionString() =>
        string.IsNullOrWhiteSpace(Migration.Username)
            ? Build(Username, Password, "ariva-migration")
            : Build(Migration.Username, Migration.Password, "ariva-migration");

    private string Build(string username, string password, string applicationName)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = Host,
            Port = Port,
            Database = Name,
            Username = username,
            Password = password,
            SslMode = UseEncryption ? SslMode.VerifyFull : SslMode.Disable,
            ApplicationName = applicationName
        };
        return builder.ConnectionString;
    }
}

/// <summary>Database:Migration. The password comes from a secret (Database__Migration__Password).</summary>
public sealed class MigrationLoginSettings
{
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
}

/// <summary>NHibernate options under Database:NHibernate, with AMAN's production defaults.</summary>
public sealed class NHibernateSettings
{
    public string ConnectionDriver { get; init; } = "Ariva.Infra.NHibernate.Drivers.NHibernatePostgresClientDriver, Ariva.Infra";
    public string ConnectionProvider { get; init; } = "NHibernate.Connection.DriverConnectionProvider, NHibernate";
    public string Dialect { get; init; } = "NHibernate.Dialect.PostgreSQL83Dialect";
    public short BatchSize { get; init; } = 25;

    /// <summary>Logs SQL text at Debug. Parameter values are never logged (they can hold personal data).</summary>
    public bool ShowSql { get; init; }
}
