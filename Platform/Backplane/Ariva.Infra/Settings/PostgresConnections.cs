using Npgsql;

namespace Ariva.Infra.Settings;

/// <summary>
/// ARV-226 (CWE-918, ASVS 5.0 V12.3.2): every PostgreSQL connection Ariva opens is made here. Npgsql gives its TLS handshake no
/// chain policy of its own, so the handshake builds the server's chain with the platform's default, which on Linux downloads a
/// missing intermediate from the address the server's certificate names, before the server is authenticated (four requests in
/// one open in the test). A connection made here gives the handshake a policy that downloads no certificate and fetches no
/// revocation; Npgsql's own check (VerifyFull against Database:RootCertificate) decides the trust as before and downloads
/// nothing itself. The NHibernate driver's connections take the same policy, and nothing else constructs a connection
/// (<c>CertificateDownloadTests.PostgreSql_Should_BeOpenedThroughPostgresConnectionsOnly_When_TheHostsAndJobsConnect</c>).
/// </summary>
public static class PostgresConnections
{
    /// <summary>A connection for <paramref name="connectionString"/>, not yet open, whose handshake downloads no certificate.</summary>
    /// <param name="connectionString">The connection string (<see cref="DatabaseSettings.BuildConnectionString"/> and its kin).</param>
    public static NpgsqlConnection Create(string connectionString) => Harden(new NpgsqlConnection(connectionString));

    /// <summary>Gives <paramref name="connection"/>'s TLS handshake a chain policy that downloads no certificate.</summary>
    /// <param name="connection">A connection not yet open.</param>
    /// <returns>The same connection.</returns>
    public static NpgsqlConnection Harden(NpgsqlConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        connection.SslClientAuthenticationOptionsCallback = CertificateDownloads.Refuse;
        return connection;
    }
}
