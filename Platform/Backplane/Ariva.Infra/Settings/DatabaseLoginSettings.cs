using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Ariva.Infra.Settings;

/// <summary>
/// One database login's name and password (ARV-083a): a host's login under Database:HostLogins, which the migration job alone
/// reads, or the availability ledger's (Database:Availability). The password comes from a secret, never from a committed file.
/// </summary>
public sealed partial class DatabaseLoginSettings
{
    #region Constants

    /// <summary>As the validation reader's (script 0049): the migration job refuses anything shorter.</summary>
    public const int MinimumPasswordLength = 16;

    /// <summary>Ariva's own roles (scripts 0001, 0043 and 0074): never a login's name.</summary>
    internal static readonly string[] ReservedNames =
    [
        "ariva_migration", "ariva_runtime", "ariva_validation_reader", "ariva_main", "ariva_ingest", "ariva_stream", "ariva_cronz",
        "ariva_integration", "ariva_availability"
    ];

    /// <summary>
    /// The runtime login name the committed appsettings files give every host (Database:Username of appsettings.base.json and
    /// its environment files), whose password base-appsettings-secret holds and every pod mounts. In a cluster it is never a
    /// host's login or the availability ledger's (the ARV-083a review, CWE-269): a host login under that name, with the base
    /// file's password, would give every pod that host's grants. A vm-local machine's hosts share it (the development login).
    /// </summary>
    public const string CommittedRuntimeName = "ariva_app";

    #endregion

    #region Properties

    public string Username { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;

    /// <summary>A name is set.</summary>
    public bool IsConfigured => !string.IsNullOrEmpty(Username);

    #endregion

    #region Methods

    /// <summary>
    /// What is wrong with this login on its own (its name and password, never repeated), under <paramref name="section"/>: a name
    /// that is not a plain lower-case identifier, starts with pg_ or is one of Ariva's roles, or, in a cluster, is
    /// <see cref="CommittedRuntimeName"/>; a password missing, shorter than 16 characters or not printable ASCII (the migration
    /// job derives the login's SCRAM-SHA-256 verifier from it, which is exact without SASLprep only for printable ASCII); a
    /// password without a name. Empty when the login is not configured at all.
    /// </summary>
    /// <param name="section">The setting's section, which each problem names.</param>
    /// <param name="cluster">The process runs in a cluster (k8s-dev, k8s-demo, k8s-prd): a host's or the ledger's login there.</param>
    public IReadOnlyList<string> Problems(string section, bool cluster = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(section);
        var problems = new List<string>();
        if (!IsConfigured)
        {
            if (!string.IsNullOrEmpty(Password))
                problems.Add($"{section}:Password is set without {section}:Username.");
            return problems;
        }

        if (!NamePattern().IsMatch(Username) || Username.StartsWith("pg_", StringComparison.Ordinal))
            problems.Add($"{section}:Username must be a plain lower-case PostgreSQL identifier (a to z, digits, underscore; at most 63 characters; not pg_).");
        if (ReservedNames.Contains(Username, StringComparer.OrdinalIgnoreCase))
            problems.Add($"{section}:Username names one of Ariva's database roles; give the login a name of its own.");
        if (cluster && Names(CommittedRuntimeName))
            problems.Add($"{section}:Username is the runtime login name the committed appsettings files give every host (Database:Username), whose password " +
                         "every pod mounts from base-appsettings-secret; in a cluster this login has a name of its own (CWE-269).");

        if (string.IsNullOrEmpty(Password) || Password.Length < MinimumPasswordLength)
            problems.Add($"{section}:Password must have at least {MinimumPasswordLength} characters.");
        else if (Password.Any(c => c is < ' ' or > '~'))
            problems.Add($"{section}:Password must be printable ASCII characters (space to tilde) and is not: look for a control " +
                         "character, a trailing newline from a password file for example (create the secret from a file without one).");
        return problems;
    }

    /// <summary>Whether this login has the given name, compared without regard to case (PostgreSQL folds an unquoted name).</summary>
    public bool Names(string username) => IsConfigured && string.Equals(Username, username, StringComparison.OrdinalIgnoreCase);

    /// <summary>Equal passwords defeat the separation of the logins; compared in constant time like every secret (CWE-208).</summary>
    public bool SharesPasswordWith(string other) =>
        !string.IsNullOrEmpty(Password) && !string.IsNullOrEmpty(other) &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Password), Encoding.UTF8.GetBytes(other));

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}\\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex NamePattern();

    #endregion
}
