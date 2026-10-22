using System.Globalization;
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

    /// <summary>The bounds of <see cref="CommandTimeoutSeconds"/>.</summary>
    public const int MinCommandTimeoutSeconds = 5;

    /// <inheritdoc cref="MinCommandTimeoutSeconds"/>
    public const int MaxCommandTimeoutSeconds = 300;

    /// <summary>
    /// The longest the runtime login's client waits for one statement anywhere in Ariva: the replay and archive reads, the device
    /// health archive statement and the stored name check take this as their own command timeout. Npgsql's command timeout bounds
    /// each wait for the server, not a whole streamed read; these readers buffer their rows (and the COPY writers write from batches
    /// in memory), so no statement of theirs runs past it. A reader that streams to a slow consumer would need its own allowance.
    /// </summary>
    public const int LongestRuntimeCommandSeconds = 300;

    /// <summary>
    /// The bounds of <see cref="StatementTimeoutSeconds"/>: above <see cref="LongestRuntimeCommandSeconds"/>, so the server never ends a
    /// statement a host still waits for.
    /// </summary>
    public const int MinStatementTimeoutSeconds = LongestRuntimeCommandSeconds + 1;

    /// <inheritdoc cref="MinStatementTimeoutSeconds"/>
    public const int MaxStatementTimeoutSeconds = 3600;

    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 5432;
    public string Name { get; init; } = "ariva";
    public string Username { get; init; } = "ariva";
    public string Password { get; init; } = string.Empty;

    /// <summary>TLS to PostgreSQL with full certificate and host name verification (CWE-295).</summary>
    public bool UseEncryption { get; init; }

    /// <summary>
    /// The PEM file of the internal CA that issued the database server's certificate (ARV-082a): with <see cref="UseEncryption"/>
    /// the server is verified against this CA alone, never the system store. Required in k8s-prd (<see cref="ProductionTransportProblem"/>).
    /// </summary>
    public string RootCertificate { get; init; } = string.Empty;

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

    /// <summary>
    /// ARV-096d (CWE-400, ASVS 5.0 V15.2.2): how long a host waits for one database statement before it cancels it, in seconds: the
    /// default of every command on a connection built here (NHibernate's and <c>ExecuteSqlAsync</c>'s among them) unless the command
    /// sets its own. 30, Npgsql's own default, which every host used without saying so before ARV-096d. A host and the migration job
    /// refuse to start outside <see cref="MinCommandTimeoutSeconds"/> to <see cref="MaxCommandTimeoutSeconds"/>.
    /// </summary>
    public int CommandTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// ARV-096d: the server's own limit on one statement of the runtime login (<c>statement_timeout</c>, set on the login by the
    /// migration job through script 0063), in seconds. A backstop for a statement whose client stopped waiting without the server
    /// knowing (a host gone, a cancel lost), above every client timeout the runtime login uses
    /// (<see cref="MinStatementTimeoutSeconds"/> to <see cref="MaxStatementTimeoutSeconds"/>).
    /// </summary>
    public int StatementTimeoutSeconds { get; init; } = 360;

    /// <summary>The bounds of <see cref="ConnectTimeoutSeconds"/>.</summary>
    public const int MinConnectTimeoutSeconds = 2;

    /// <inheritdoc cref="MinConnectTimeoutSeconds"/>
    public const int MaxConnectTimeoutSeconds = 60;

    /// <summary>
    /// ARV-096e: how long a host waits for the server to accept a new connection (Npgsql's <c>Timeout</c>), in seconds. A job's or a
    /// loop's run timeout cannot cut this wait (Npgsql does not take the token while it opens a connection), so a server that
    /// accepts and never answers holds a run this long at the most. 15, Npgsql's own default, said explicitly; a host and the
    /// migration job refuse to start outside <see cref="MinConnectTimeoutSeconds"/> to <see cref="MaxConnectTimeoutSeconds"/>.
    /// </summary>
    public int ConnectTimeoutSeconds { get; init; } = 15;

    /// <summary>The login the migration job uses (DDL). Hosts never use it.</summary>
    public MigrationLoginSettings Migration { get; init; } = new();

    public NHibernateSettings NHibernate { get; init; } = new();

    /// <summary>
    /// Database:Retention (ARV-111a): the retention periods the migration job applies per environment (hypertable policies and
    /// the relational periods the Cronz retention job reads). Hosts never apply them.
    /// </summary>
    public RetentionSettings Retention { get; init; } = new();

    /// <summary>False when no separate migration login is configured or it is the same login as the hosts'.</summary>
    public bool UsesSeparateRuntimeLogin =>
        !string.IsNullOrWhiteSpace(Migration.Username) &&
        !string.Equals(Migration.Username, Username, StringComparison.Ordinal);

    public static DatabaseSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.GetSection(SectionName).Get<DatabaseSettings>() ?? new DatabaseSettings();
    }

    /// <summary>
    /// Why this configuration is not fit for k8s-prd, or null (ARV-082a, CWE-319, CWE-295): every connection to PostgreSQL uses
    /// TLS, verified against the internal CA in <see cref="RootCertificate"/>, which must be there. Never names the password.
    /// </summary>
    public string ProductionTransportProblem(Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        if (!UseEncryption)
            return "Database:UseEncryption must be true in k8s-prd: every connection to PostgreSQL uses TLS.";
        if (string.IsNullOrWhiteSpace(RootCertificate))
            return "Database:RootCertificate must name the internal CA's PEM file in k8s-prd: the server is verified against it, not the system store.";
        return fileExists(RootCertificate) ? null : "Database:RootCertificate names a file that does not exist: mount the database CA secret.";
    }

    /// <summary>What is out of range among the timeouts (ARV-096d, ARV-096e); empty when all may be used.</summary>
    public IEnumerable<string> TimeoutProblems()
    {
        if (CommandTimeoutSeconds is < MinCommandTimeoutSeconds or > MaxCommandTimeoutSeconds)
            yield return Range(nameof(CommandTimeoutSeconds), MinCommandTimeoutSeconds, MaxCommandTimeoutSeconds);
        if (StatementTimeoutSeconds is < MinStatementTimeoutSeconds or > MaxStatementTimeoutSeconds)
            yield return Range(nameof(StatementTimeoutSeconds), MinStatementTimeoutSeconds, MaxStatementTimeoutSeconds);
        if (ConnectTimeoutSeconds is < MinConnectTimeoutSeconds or > MaxConnectTimeoutSeconds)
            yield return Range(nameof(ConnectTimeoutSeconds), MinConnectTimeoutSeconds, MaxConnectTimeoutSeconds, "ARV-096e");
    }

    /// <summary>Stops a host or the migration job at start when a timeout is out of range.</summary>
    public void EnsureValidTimeouts()
    {
        var problems = TimeoutProblems().ToList();
        if (problems.Count > 0)
            throw new InvalidOperationException(string.Join(" ", problems));
    }

    private static string Range(string name, int min, int max, string story = "ARV-096d") =>
        string.Create(CultureInfo.InvariantCulture, $"{SectionName}:{name} is a whole number of seconds from {min:N0} to {max:N0} ({story}).");

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
            ApplicationName = applicationName,
            CommandTimeout = CommandTimeoutSeconds,
            Timeout = ConnectTimeoutSeconds
        };
        ApplyRootCertificate(builder);
        return builder.ConnectionString;
    }

    /// <summary>With TLS, the server is verified against the pinned internal CA when one is named (every login's connection).</summary>
    internal void ApplyRootCertificate(NpgsqlConnectionStringBuilder builder)
    {
        if (UseEncryption && !string.IsNullOrWhiteSpace(RootCertificate))
            builder.RootCertificate = RootCertificate;
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
