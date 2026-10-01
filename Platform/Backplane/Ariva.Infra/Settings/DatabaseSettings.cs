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

    public NHibernateSettings NHibernate { get; init; } = new();

    public static DatabaseSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.GetSection(SectionName).Get<DatabaseSettings>() ?? new DatabaseSettings();
    }

    public string BuildConnectionString()
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = Host,
            Port = Port,
            Database = Name,
            Username = Username,
            Password = Password,
            SslMode = UseEncryption ? SslMode.VerifyFull : SslMode.Disable,
            ApplicationName = "ariva"
        };
        return builder.ConnectionString;
    }
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
