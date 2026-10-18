using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;

namespace Ariva.Infra.Caching;

/// <summary>
/// One Redis connection per process, shared by the FusionCache distributed level and its backplane (ARV-008).
/// Redis:ConnectionString uses the StackExchange.Redis format ("host:port,password=..."), parsed rather than split
/// (AMAN split on ':' and lost the password). The connection never aborts on a failed first connect: FusionCache keeps
/// serving from memory with fail-safe while Redis is away, and reconnects.
/// </summary>
public sealed class RedisConnection : IAsyncDisposable
{
    private readonly Lazy<Task<IConnectionMultiplexer>> _connection;

    public RedisConnection(RedisSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Settings = settings;
        _connection = new Lazy<Task<IConnectionMultiplexer>>(ConnectAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public RedisSettings Settings { get; }

    public Task<IConnectionMultiplexer> GetAsync() => _connection.Value;

    private async Task<IConnectionMultiplexer> ConnectAsync()
    {
        return await ConnectionMultiplexer.ConnectAsync(Settings.Options());
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection.IsValueCreated)
            await CloseQuietlyAsync(_connection.Value);
    }

    /// <summary>
    /// Ends the connection at shutdown without throwing: StackExchange.Redis 2.10 can throw a NullReferenceException from
    /// inside its own close while a reconnect is in flight (ConnectionMultiplexer.AllComplete), and a connection that
    /// never connected has nothing to close. Either way the process is letting go of Redis, so nothing is left to do.
    /// </summary>
    internal static async ValueTask CloseQuietlyAsync(Task<IConnectionMultiplexer> connection)
    {
        try
        {
            await (await connection).DisposeAsync();
        }
        catch (NullReferenceException)
        {
            // The library's own close race at shutdown; whatever it did not release goes with the process.
        }
        catch (RedisConnectionException)
        {
            // The first connect failed: there is no connection to end.
        }
    }
}

/// <summary>The "Redis" section. Off in the base file (vm-local without dev-up, E2E); on in the cluster files.</summary>
public sealed class RedisSettings
{
    public bool Enabled { get; init; }
    public string ConnectionString { get; init; } = "localhost:6379";

    /// <summary>Key and channel prefix, so Ariva never collides with AMAN on a shared Redis.</summary>
    public string InstanceName { get; init; } = "ariva:";

    /// <summary>
    /// The PEM file of the internal CA that issued the Redis server's certificate (ARV-082a): with ssl=true in the connection
    /// string the server is verified against this CA alone, never the system store. Required in k8s-prd.
    /// </summary>
    public string CaCertificatePath { get; init; } = string.Empty;

    /// <summary>
    /// The connection's options, for the cache and the live hub's backplane alike: never aborting on a failed first connect,
    /// and with TLS pinned to the internal CA when one is named (<see cref="IsIssuedByPinnedCa"/>).
    /// </summary>
    public ConfigurationOptions Options()
    {
        var options = ConfigurationOptions.Parse(ConnectionString);
        options.AbortOnConnectFail = false;
        options.ClientName ??= "ariva";
        if (options.Ssl && !string.IsNullOrWhiteSpace(CaCertificatePath))
        {
            var roots = new X509Certificate2Collection();
            roots.ImportFromPemFile(CaCertificatePath);
            options.CertificateValidation += (_, certificate, presented, errors) => IsIssuedByPinnedCa(certificate, presented, errors, roots);
        }

        return options;
    }

    /// <summary>
    /// ARV-082a (CWE-295): the Redis server's certificate is accepted only when it chains to one of the pinned CAs, whatever the
    /// system store makes of it (StackExchange.Redis's own TrustIssuer accepts any certificate the system store trusts, and
    /// reads the first CA of the file only). Any handshake error other than an unknown chain, a host name mismatch above
    /// all, refuses it; so does a certificate not meant for a server. Revocation is not checked, as for every outbound
    /// connection (no request leaves for a CRL or OCSP address the certificate names).
    /// </summary>
    internal static bool IsIssuedByPinnedCa(X509Certificate certificate, X509Chain presented, SslPolicyErrors errors, X509Certificate2Collection roots)
    {
        if (certificate is null || roots is not { Count: > 0 } || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
            return false;
        using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(roots);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        // The server's own intermediates, if it sent any; they help build the chain but are never trusted on their own.
        if (presented is not null)
            foreach (var element in presented.ChainElements)
                chain.ChainPolicy.ExtraStore.Add(element.Certificate);
        return chain.Build(leaf);
    }

    /// <summary>
    /// Why this configuration is not fit for k8s-prd, or null (ARV-082a, CWE-319, CWE-295): with Redis on, the connection uses
    /// TLS (ssl=true), verified against the internal CA in <see cref="CaCertificatePath"/>, which must be there. Never names
    /// the connection string, which may hold a password.
    /// </summary>
    public string ProductionTransportProblem(Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        if (!Enabled)
            return null;
        if (!ConfigurationOptions.Parse(ConnectionString).Ssl)
            return "Redis:ConnectionString must carry ssl=true in k8s-prd: every connection to Redis uses TLS.";
        if (string.IsNullOrWhiteSpace(CaCertificatePath))
            return "Redis:CaCertificatePath must name the internal CA's PEM file in k8s-prd: the server is verified against it, not the system store.";
        return fileExists(CaCertificatePath) ? null : "Redis:CaCertificatePath names a file that does not exist: mount the Redis CA secret.";
    }

    public static RedisSettings From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.GetSection("Redis").Get<RedisSettings>() ?? new RedisSettings();
    }
}
