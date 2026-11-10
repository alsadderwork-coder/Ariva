using System.Security.Cryptography;
using Ariva.Infra.Timescale;
using Npgsql;

namespace Ariva.IntegrationTests.Setup;

/// <summary>
/// ARV-083a: a host's database login as the migration job creates it (script 0074's ariva_ensure_host_login, from a
/// SCRAM-SHA-256 verifier, never the password): a member of ariva_runtime and of that host's role only, with CONNECT on the
/// database. Tests that act as a host connect with one, so what they may do is what that host's grants allow
/// (HostDatabaseAccess), not the base alone.
/// </summary>
internal static class HostLogins
{
    #region Methods

    /// <summary>A password of a host login (printable ASCII, 16 characters or more).</summary>
    public static string NewPassword(string prefix = "hl-") => prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

    /// <summary>Creates or updates <paramref name="login"/> as the login of the host that holds <paramref name="role"/>.</summary>
    public static async Task EnsureAsync(NpgsqlConnection admin, string login, string password, string role, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(admin);
        await using var ensure = new NpgsqlCommand("SELECT ariva_ensure_host_login(@login, @verifier, @role)", admin);
        ensure.Parameters.AddWithValue("login", login);
        ensure.Parameters.AddWithValue("verifier", ScramVerifier.For(password));
        ensure.Parameters.AddWithValue("role", role);
        await ensure.ExecuteNonQueryAsync(ct);
    }

    /// <summary>As <see cref="EnsureAsync"/> on a connection of its own to <paramref name="connectionString"/> (the migration login's).</summary>
    public static async Task EnsureAsync(string connectionString, string login, string password, string role, CancellationToken ct)
    {
        await using var admin = new NpgsqlConnection(connectionString);
        await admin.OpenAsync(ct);
        await EnsureAsync(admin, login, password, role, ct);
    }

    /// <summary>
    /// The settings of a cluster's migration job for every host's login and the availability ledger's (Database:HostLogins and
    /// Database:Availability), each a login of its own named <paramref name="prefix"/> and the host, each with a password of
    /// its own; <paramref name="main"/> and <paramref name="mainPassword"/> name Ariva.Api.Main's when given.
    /// </summary>
    public static Dictionary<string, string> ClusterSettings(string prefix, string main = null, string mainPassword = null)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var host in new[] { "Main", "Ingest", "Stream", "Cronz", "Integration" })
        {
            var isMain = host == "Main" && main is not null;
            settings[$"Database:HostLogins:{host}:Username"] = isMain ? main : prefix + host.ToLowerInvariant();
            settings[$"Database:HostLogins:{host}:Password"] = isMain && mainPassword is not null ? mainPassword : NewPassword();
        }

        settings["Database:Availability:Username"] = prefix + "availability";
        settings["Database:Availability:Password"] = NewPassword();
        return settings;
    }

    #endregion
}
