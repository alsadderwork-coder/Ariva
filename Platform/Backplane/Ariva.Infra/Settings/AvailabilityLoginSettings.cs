using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Ariva.Infra.Settings;

/// <summary>
/// Database:Availability (ARV-083a; CWE-269, owner decision 2026-10-10): the availability ledger's own database login, a member
/// of ariva_runtime and ariva_availability (script 0074), the only role that may record availability minutes or move a site's
/// cursor. Ariva.Api.Cronz's AvailabilityLedger opens every connection of its run with it, so the Cronz host's own login, and
/// every other host's, cannot record minutes ahead of the ledger or move a cursor past minutes. The migration job creates or
/// updates the login from the same section (ariva_ensure_host_login). In the clusters the name and password come from the
/// secret the chart gives to api-cronz and the migration job only; no committed appsettings file carries the section.
/// <para>
/// Not configured (no name): the ledger uses the host's runtime login, which may hold the role only on a vm-local database
/// whose hosts share the development login (the E2E suites, the AppHost, the integration tests). A cluster's Cronz host
/// refuses to start without it (AvailabilityExtensions), and the migration job without it once host logins are configured.
/// </para>
/// </summary>
public sealed class AvailabilityLoginSettings
{
    #region Constants

    public const string SectionName = "Database:Availability";

    /// <summary>The application name the ledger's connections carry (pg_stat_activity), apart from the host's own (ariva-cronz).</summary>
    public const string ApplicationName = "ariva-availability";

    /// <summary>
    /// Connections the ledger's own pool may hold per process (CWE-400): a run decides one site at a time, in one connection,
    /// after the run's list of sites in another.
    /// </summary>
    public const int MaxPoolSize = 2;

    #endregion

    #region Properties

    public string Username { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;

    /// <summary>A name is set: the ledger connects with this login.</summary>
    public bool IsConfigured => !string.IsNullOrEmpty(Username);

    /// <summary>The name and password as one login.</summary>
    public DatabaseLoginSettings Login => new() { Username = Username, Password = Password };

    #endregion

    #region Methods

    public static AvailabilityLoginSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.GetSection(SectionName).Get<AvailabilityLoginSettings>() ?? new AvailabilityLoginSettings();
    }

    /// <summary>
    /// What is wrong with this login (no password is ever repeated): <see cref="DatabaseLoginSettings.Problems"/>, and a name or
    /// password that is the host's runtime login's (Database:Username) or the migration login's. Empty when not configured.
    /// </summary>
    /// <param name="database">The host's Database section (its runtime login and the migration login).</param>
    /// <param name="cluster">The process runs in a cluster: the committed runtime login name is refused too.</param>
    public IReadOnlyList<string> Problems(DatabaseSettings database, bool cluster = false)
    {
        ArgumentNullException.ThrowIfNull(database);
        var login = Login;
        var problems = login.Problems(SectionName, cluster).ToList();
        if (!IsConfigured)
            return problems;

        var migrationLogin = string.IsNullOrWhiteSpace(database.Migration?.Username) ? database.Username : database.Migration.Username;
        if (login.Names(database.Username))
            problems.Add($"{SectionName}:Username is the host's runtime login (Database:Username); the availability ledger has a login of its own (CWE-269).");
        if (login.Names(migrationLogin))
            problems.Add($"{SectionName}:Username is the migration login; the availability ledger has a login of its own (CWE-269).");
        if (login.SharesPasswordWith(database.Password) || login.SharesPasswordWith(database.Migration?.Password))
            problems.Add($"{SectionName}:Password must not be the runtime or the migration login's password (CWE-287).");
        return problems;
    }

    /// <summary>Throws with every problem (no secret in the message) when <see cref="Problems"/> finds any.</summary>
    public void EnsureValid(DatabaseSettings database, bool cluster = false)
    {
        var problems = Problems(database, cluster);
        if (problems.Count > 0)
            throw new InvalidOperationException($"The availability ledger's login is misconfigured (ARV-083a): {string.Join(" ", problems)}");
    }

    /// <summary>
    /// The ledger's connection: host, port, database, TLS and timeouts from <paramref name="database"/>, the login from this
    /// section, its own application name and a small pool of its own. Built with Npgsql's builder, so a password holding ';'
    /// cannot add options.
    /// </summary>
    public string BuildConnectionString(DatabaseSettings database)
    {
        EnsureValid(database);
        if (!IsConfigured)
            throw new InvalidOperationException($"The availability ledger's login is not configured ({SectionName}).");

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = database.Host,
            Port = database.Port,
            Database = database.Name,
            Username = Username,
            Password = Password,
            SslMode = database.UseEncryption ? SslMode.VerifyFull : SslMode.Disable,
            ApplicationName = ApplicationName,
            CommandTimeout = database.CommandTimeoutSeconds,
            Timeout = database.ConnectTimeoutSeconds,
            MaxPoolSize = MaxPoolSize
        };
        database.ApplyRootCertificate(builder);
        return builder.ConnectionString;
    }

    #endregion
}
