using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Ariva.Infra.Settings;

/// <summary>
/// Database:ValidationReader (ARV-104g1, CWE-269, CWE-863, CWE-287): the validation service's own database login, the only
/// login that holds <c>ariva_validation_reader</c> (script 0043) and so the only one that reads the shadow nowcast. It is a
/// section of its own, never a member of <see cref="DatabaseSettings"/>, so no runtime connection (NHibernate, the stream
/// store, the Timescale writers, the outbox) is ever built from it.
/// <para>
/// Who uses it: the validation results service (<c>SvcValidationResults</c>, its shadow read; ARV-104g2 and ARV-104g serve
/// the results from Ariva.Api.Main) is the only code that opens a connection with it, through
/// <see cref="BuildReaderConnectionString"/>; the migration job creates or updates the login from it
/// (<c>ariva_ensure_validation_reader_login</c>, script 0049) and never connects as the reader; every host's start-up guard
/// (<c>ValidationReaderGuard</c>) compares its name with the runtime and migration logins. In the clusters the name and the
/// password come from the secret <c>ariva-validation-reader</c>, which the chart gives to api-main and the migration job
/// only; no committed appsettings file carries the section (unit tests pin all of this).
/// </para>
/// Not configured (no name) is allowed: no login then reads the shadow nowcast, and the validation results cannot show it.
/// Internal (ARV-104g1 review): only Ariva.Infra and the composition root (Ariva.Di) can reach it.
/// </summary>
internal sealed partial class ValidationReaderSettings
{
    #region Constants

    public const string SectionName = "Database:ValidationReader";

    /// <summary>The NOLOGIN role of script 0043: SELECT on the shadow nowcast's table, USAGE on schema public, nothing else.</summary>
    public const string ReaderRole = "ariva_validation_reader";

    /// <summary>As the runtime login (0001): the migration's function refuses anything shorter.</summary>
    public const int MinimumPasswordLength = 16;

    /// <summary>The application name the reader's connections carry (pg_stat_activity), apart from the hosts' "ariva".</summary>
    public const string ApplicationName = "ariva-validation-reader";

    /// <summary>
    /// Connections the reader's own pool may hold per process (CWE-400): the campaign comparison reads its slices one after
    /// another (ARV-104g2, single-flight per campaign).
    /// </summary>
    public const int MaxPoolSize = 4;

    /// <summary>Ariva's group roles (scripts 0001 and 0043): never a login's name.</summary>
    private static readonly string[] ReservedNames = ["ariva_migration", "ariva_runtime", ReaderRole];

    #endregion

    #region Properties

    public string Username { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;

    /// <summary>A name is set: the migration job creates the login, and the validation service may read the shadow.</summary>
    public bool IsConfigured => !string.IsNullOrEmpty(Username);

    #endregion

    #region Methods

    public static ValidationReaderSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.GetSection(SectionName).Get<ValidationReaderSettings>() ?? new ValidationReaderSettings();
    }

    /// <summary>
    /// What is wrong with this login next to the hosts' logins (no password and no other value is ever repeated): a name that
    /// is not a plain lower-case identifier or is one of Ariva's roles; the runtime login (Database:Username) or the
    /// migration login (Database:Migration:Username, or the runtime login when none is set), compared without regard to case;
    /// a password missing, shorter than 16 characters, not printable ASCII (the migration job derives the login's SCRAM-SHA-256
    /// verifier from it, which is exact without SASLprep only for printable ASCII), or the same as the runtime or the migration
    /// login's; a password without a name. Empty when the login is not configured at all.
    /// </summary>
    public IReadOnlyList<string> Problems(DatabaseSettings database)
    {
        ArgumentNullException.ThrowIfNull(database);
        var problems = new List<string>();
        if (!IsConfigured)
        {
            if (!string.IsNullOrEmpty(Password))
                problems.Add($"{SectionName}:Password is set without {SectionName}:Username.");
            return problems;
        }

        if (!NamePattern().IsMatch(Username) || Username.StartsWith("pg_", StringComparison.Ordinal))
            problems.Add($"{SectionName}:Username must be a plain lower-case PostgreSQL identifier (a to z, digits, underscore; at most 63 characters; not pg_).");
        if (ReservedNames.Contains(Username, StringComparer.OrdinalIgnoreCase))
            problems.Add($"{SectionName}:Username names one of Ariva's database roles; give the validation reader a login of its own.");

        var migrationLogin = string.IsNullOrWhiteSpace(database.Migration?.Username) ? database.Username : database.Migration.Username;
        if (string.Equals(Username, database.Username, StringComparison.OrdinalIgnoreCase))
            problems.Add($"{SectionName}:Username is the runtime login (Database:Username); the validation reader must be a login of its own (CWE-269).");
        if (string.Equals(Username, migrationLogin, StringComparison.OrdinalIgnoreCase))
            problems.Add($"{SectionName}:Username is the migration login (Database:Migration:Username); the validation reader must be a login of its own (CWE-269).");

        if (string.IsNullOrEmpty(Password) || Password.Length < MinimumPasswordLength)
            problems.Add($"{SectionName}:Password must have at least {MinimumPasswordLength} characters.");
        else if (Password.Any(c => c is < ' ' or > '~'))
            problems.Add($"{SectionName}:Password must be printable ASCII characters (space to tilde) and is not: look for a control " +
                         "character, a trailing newline from a password file for example (create the secret from a file without one).");
        else if (SameSecret(Password, database.Password) || SameSecret(Password, database.Migration?.Password))
            problems.Add($"{SectionName}:Password must not be the runtime or the migration login's password (CWE-287).");

        return problems;
    }

    /// <summary>Throws with every problem (no secret in the message) when <see cref="Problems"/> finds any.</summary>
    public void EnsureValid(DatabaseSettings database)
    {
        var problems = Problems(database);
        if (problems.Count > 0)
            throw new InvalidOperationException($"The validation reader login is misconfigured (ARV-104g1): {string.Join(" ", problems)}");
    }

    /// <summary>
    /// The reader's connection: host, port, database and TLS from <paramref name="database"/>, the login from this section,
    /// its own application name and a small pool of its own. Built with Npgsql's builder, so a password holding ';' cannot add
    /// options. Only the validation service's shadow read calls it (a source scan pins the caller).
    /// </summary>
    internal string BuildReaderConnectionString(DatabaseSettings database)
    {
        EnsureValid(database);
        if (!IsConfigured)
            throw new InvalidOperationException($"The validation reader login is not configured ({SectionName}).");

        return new NpgsqlConnectionStringBuilder
        {
            Host = database.Host,
            Port = database.Port,
            Database = database.Name,
            Username = Username,
            Password = Password,
            SslMode = database.UseEncryption ? SslMode.VerifyFull : SslMode.Disable,
            ApplicationName = ApplicationName,
            MaxPoolSize = MaxPoolSize
        }.ConnectionString;
    }

    // Equal passwords defeat the separation of the logins; compared in constant time like every secret (CWE-208).
    private static bool SameSecret(string candidate, string other) =>
        !string.IsNullOrEmpty(other) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), Encoding.UTF8.GetBytes(other));

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}\\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex NamePattern();

    #endregion
}
