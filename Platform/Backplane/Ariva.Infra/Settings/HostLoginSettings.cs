using Ariva.Infra.Timescale;
using Microsoft.Extensions.Configuration;

namespace Ariva.Infra.Settings;

/// <summary>
/// Database:HostLogins (ARV-083a; CWE-269, CWE-285, CWE-287): each host's own database login, which the migration job alone
/// reads. It creates or updates each from a SCRAM-SHA-256 verifier it computes, so no password reaches the server
/// (ariva_ensure_host_login, script 0074), as a member of ariva_runtime and of that host's role only, then sets the runtime
/// limits on it. A host connects with its own login as Database:Username and Database:Password, which the chart takes from the
/// host's own secret; the migration job gets every host's secret and nothing else new. No committed appsettings file carries
/// the section.
/// <para>
/// A cluster (k8s-dev, k8s-demo, k8s-prd) needs all five and the availability ledger's login (Database:Availability). A
/// vm-local process, or one without an environment (the integration tests), may leave them all out: the migration job then
/// gives Database:Username every host role (ariva_ensure_development_login), so the hosts of a developer machine keep sharing
/// it. Some but not all is refused, so no host is left on a login the job did not ensure.
/// </para>
/// Internal: only Ariva.Infra and the composition root (Ariva.Di) reach it.
/// </summary>
internal sealed class HostLoginSettings
{
    #region Constants

    public const string SectionName = "Database:HostLogins";

    #endregion

    #region Properties

    public DatabaseLoginSettings Main { get; init; } = new();

    public DatabaseLoginSettings Ingest { get; init; } = new();

    public DatabaseLoginSettings Stream { get; init; } = new();

    public DatabaseLoginSettings Cronz { get; init; } = new();

    public DatabaseLoginSettings Integration { get; init; } = new();

    /// <summary>Each host's login with its section and its role (script 0074), in the order the scripts name the roles.</summary>
    public IReadOnlyList<HostLogin> All =>
    [
        new($"{SectionName}:Main", HostDatabaseAccess.MainRole, Main ?? new DatabaseLoginSettings()),
        new($"{SectionName}:Ingest", HostDatabaseAccess.IngestRole, Ingest ?? new DatabaseLoginSettings()),
        new($"{SectionName}:Stream", HostDatabaseAccess.StreamRole, Stream ?? new DatabaseLoginSettings()),
        new($"{SectionName}:Cronz", HostDatabaseAccess.CronzRole, Cronz ?? new DatabaseLoginSettings()),
        new($"{SectionName}:Integration", HostDatabaseAccess.IntegrationRole, Integration ?? new DatabaseLoginSettings())
    ];

    /// <summary>Any host's login is named (or a password is given): the migration job ensures host logins, not the development one.</summary>
    public bool IsConfigured => All.Any(h => h.Login.IsConfigured || !string.IsNullOrEmpty(h.Login.Password));

    #endregion

    #region Methods

    public static HostLoginSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.GetSection(SectionName).Get<HostLoginSettings>() ?? new HostLoginSettings();
    }

    /// <summary>
    /// What is wrong with the host logins and the availability ledger's next to each other and to the migration and the
    /// validation reader's logins (no password is ever repeated): each login's own problems; in a cluster, a login missing or
    /// named as the committed runtime login (<see cref="DatabaseLoginSettings.CommittedRuntimeName"/>, whose password every pod
    /// mounts); some configured and not all, or no availability login beside them; two logins with one name or one password; a
    /// login that is the migration login or the validation reader's, or shares their password. Empty when none is configured
    /// outside a cluster.
    /// </summary>
    /// <param name="database">The job's Database section (the migration login).</param>
    /// <param name="availability">Database:Availability.</param>
    /// <param name="environment">Application:Environment, or null for a process without one.</param>
    /// <param name="readerUsername">The validation reader's login name, empty when it is not configured.</param>
    /// <param name="readerPassword">The validation reader's password, empty when it is not configured.</param>
    public IReadOnlyList<string> Problems(DatabaseSettings database, AvailabilityLoginSettings availability, string environment, string readerUsername, string readerPassword)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(availability);
        var problems = new List<string>();
        var cluster = environment?.StartsWith("k8s-", StringComparison.Ordinal) == true;
        var hosts = All;
        foreach (var host in hosts)
            problems.AddRange(host.Login.Problems(host.Section, cluster));
        problems.AddRange(availability.Login.Problems(AvailabilityLoginSettings.SectionName, cluster));
        if (!IsConfigured && !cluster)
            return problems;

        foreach (var host in hosts.Where(h => !h.Login.IsConfigured))
            problems.Add($"{host.Section}:Username is not set: {(cluster ? "a cluster's migration job ensures every host's login" : "configure every host's login or none")} (ARV-083a).");
        if (!availability.IsConfigured)
            problems.Add($"{AvailabilityLoginSettings.SectionName}:Username is not set: the availability ledger has a login of its own beside the hosts' (ARV-083a).");

        var logins = hosts.Select(h => (Section: h.Section, Login: h.Login)).Append((Section: AvailabilityLoginSettings.SectionName, Login: availability.Login))
            .Where(l => l.Login.IsConfigured).ToList();
        for (var i = 0; i < logins.Count; i++)
        {
            for (var j = i + 1; j < logins.Count; j++)
            {
                if (logins[i].Login.Names(logins[j].Login.Username))
                    problems.Add($"{logins[i].Section}:Username and {logins[j].Section}:Username name one login; each host has a login of its own (CWE-269).");
                else if (logins[i].Login.SharesPasswordWith(logins[j].Login.Password))
                    problems.Add($"{logins[i].Section}:Password and {logins[j].Section}:Password are the same; each login has a password of its own (CWE-287).");
            }
        }

        var migrationLogin = string.IsNullOrWhiteSpace(database.Migration?.Username) ? database.Username : database.Migration.Username;
        foreach (var (section, login) in logins)
        {
            if (login.Names(migrationLogin))
                problems.Add($"{section}:Username is the migration login; a host login is a login of its own (CWE-269).");
            if (login.Names(readerUsername))
                problems.Add($"{section}:Username is the validation reader's login; a host login is a login of its own (CWE-269).");
            if (login.SharesPasswordWith(database.Migration?.Password) || login.SharesPasswordWith(readerPassword))
                problems.Add($"{section}:Password must not be the migration login's or the validation reader's password (CWE-287).");
        }

        return problems;
    }

    /// <summary>Throws with every problem (no secret in the message) when <see cref="Problems"/> finds any.</summary>
    public void EnsureValid(DatabaseSettings database, AvailabilityLoginSettings availability, string environment, string readerUsername, string readerPassword)
    {
        var problems = Problems(database, availability, environment, readerUsername, readerPassword);
        if (problems.Count > 0)
            throw new InvalidOperationException($"The host database logins are misconfigured (ARV-083a): {string.Join(" ", problems)}");
    }

    #endregion
}
