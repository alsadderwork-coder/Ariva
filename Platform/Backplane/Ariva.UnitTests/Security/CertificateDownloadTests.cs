using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Ariva.Infra.Caching;
using Ariva.Infra.NHibernate;
using Ariva.Infra.NHibernate.Drivers;
using Ariva.Infra.Notifications;
using Ariva.Infra.Settings;
using Ariva.UnitTests.Logging.Fixtures;
using Confluent.Kafka;
using FluentAssertions;
using Npgsql;
using StackExchange.Redis;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-226 (CWE-918, ASVS 5.0 V12.3.2, V12.3.4): no certificate is downloaded while a TLS chain is built for Redis, PostgreSQL,
/// Kafka or the SMTP relay (the MQTT listener's client certificates: <c>MqttTransportTests</c>; the collectors: <c>CollectorTrustTests</c>). Each server here sends its own certificate alone, which an intermediate issued and which names a loopback address as
/// its issuer's: a client that downloads would ask that address during the handshake, before the server is authenticated.
/// Redis's handshake follows Ariva's policy (<see cref="RedisSettings.Options"/>), where StackExchange.Redis's own handshake
/// does fetch that address; PostgreSQL connections do so through <see cref="PostgresConnections"/>, where Npgsql's own do fetch
/// it; librdkafka (OpenSSL) fetches nothing; the SMTP transport's client (<see cref="SmtpRelayClient"/>), where MailKit's own
/// fetches it.
/// </summary>
public sealed partial class CertificateDownloadTests : IDisposable
{
    #region Fields

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("ariva-certificate-downloads-");

    #endregion

    #region Tests

    [Fact]
    public async Task Redis_Should_DownloadNoCertificate_When_ItsServerLeavesOutItsIntermediate()
    {
        await using var issuer = await IssuerSite.StartAsync(Ct);
        await using var server = await TlsCollector.StartBehindAnIntermediateAsync("Ariva test Redis CA", issuer.Address, Ct);
        var port = new Uri(server.Address).Port;
        var settings = new RedisSettings
        {
            ConnectionString = $"localhost:{port},ssl=true,connectTimeout=2000",
            CaCertificatePath = TlsCollector.WritePem(server.Authority, _folder)
        };
        var options = settings.Options();
        options.SslClientAuthenticationOptions.Should().NotBeNull("the handshake takes Ariva's options");

        var handshake = () => HandshakeAsync(port, options.SslClientAuthenticationOptions("localhost"));
        await handshake.Should().ThrowAsync<AuthenticationException>("the chain cannot be built from what the server sent");
        await using (var connection = await ConnectionMultiplexer.ConnectAsync(options))
            await WhileConnectingAsync(connection);

        issuer.Requests.Should().BeEmpty("CWE-918: no request leaves for the address the certificate names, in the handshake or after it");
        if (OperatingSystem.IsLinux())
        {
            // The control: StackExchange.Redis's own handshake fetches that address while it builds the chain, before any validation
            // event of the caller's sees the result (this one refuses whatever the platform did not trust).
            var own = ConfigurationOptions.Parse(settings.ConnectionString);
            own.AbortOnConnectFail = false;
            own.CertificateValidation += (_, _, _, errors) => errors == SslPolicyErrors.None;
            await using (var connection = await ConnectionMultiplexer.ConnectAsync(own))
                await WhileConnectingAsync(connection);
            issuer.Requests.Should().NotBeEmpty("a handshake allowed to download fetches the address the certificate names");
        }
    }

    [Fact]
    public async Task Redis_Should_CompleteTheHandshake_When_ItsServerChainsToThePinnedCa_And_RefuseAnotherCa()
    {
        await using var server = await TlsCollector.StartAsync("Ariva test Redis CA", Ct);
        var port = new Uri(server.Address).Port;
        using var other = TlsCollector.NewAuthority("Ariva test other CA");
        RedisSettings Pinned(X509Certificate2 authority, string name) => new()
        {
            ConnectionString = $"localhost:{port},ssl=true",
            CaCertificatePath = TlsCollector.WritePem(authority, _folder, name)
        };

        var trusted = Pinned(server.Authority, "redis-ca.crt").Options().SslClientAuthenticationOptions("localhost");
        var refused = Pinned(other, "other-ca.crt").Options().SslClientAuthenticationOptions("localhost");

        await HandshakeAsync(port, trusted);
        var wrong = () => HandshakeAsync(port, refused);
        await wrong.Should().ThrowAsync<AuthenticationException>("the server chains to another CA");
        trusted.CertificateChainPolicy.DisableCertificateDownloads.Should().BeTrue();
        trusted.CertificateChainPolicy.TrustMode.Should().Be(X509ChainTrustMode.CustomRootTrust);
        trusted.CertificateRevocationCheckMode.Should().Be(X509RevocationMode.NoCheck);
        trusted.TargetHost.Should().Be("localhost");
    }

    [Fact]
    public async Task Redis_Should_TrustTheSystemCasWithoutDownloading_When_NoCaIsNamed()
    {
        await using var issuer = await IssuerSite.StartAsync(Ct);
        await using var server = await TlsCollector.StartBehindAnIntermediateAsync("Ariva test Redis CA", issuer.Address, Ct);
        var port = new Uri(server.Address).Port;
        var options = new RedisSettings { ConnectionString = $"localhost:{port},ssl=true,sslHost=localhost" }.Options().SslClientAuthenticationOptions("127.0.0.1");

        var handshake = () => HandshakeAsync(port, options);

        await handshake.Should().ThrowAsync<AuthenticationException>("no CA of the system's issued it");
        issuer.Requests.Should().BeEmpty();
        options.CertificateChainPolicy.TrustMode.Should().Be(X509ChainTrustMode.System);
        options.CertificateChainPolicy.DisableCertificateDownloads.Should().BeTrue();
        options.TargetHost.Should().Be("localhost", "the connection string's sslHost names the server");
        new RedisSettings { ConnectionString = "localhost:6379" }.Options().SslClientAuthenticationOptions.Should().BeNull("without TLS there is no handshake");
    }

    [Fact]
    public async Task PostgreSql_Should_DownloadNoCertificate_When_ItsServerLeavesOutItsIntermediate()
    {
        // Npgsql gives its handshake no chain policy, so the platform's default downloads the missing intermediate; a connection
        // Ariva makes (PostgresConnections) downloads nothing, and Npgsql's own check (VerifyFull) still refuses the server.
        await using var issuer = await IssuerSite.StartAsync(Ct);
        var (authority, intermediate, certificate) = TlsCollector.IssueBehindAnIntermediate("Ariva test database CA", issuer.Address);
        using var _ = authority;
        using var __ = intermediate;
        await using var server = TlsStub.Start(certificate, Preamble.Postgres);
        var connectionString = DatabaseConnection(server.Port, TlsCollector.WritePem(authority, _folder));

        await using (var connection = PostgresConnections.Create(connectionString))
        {
            var open = () => connection.OpenAsync(Ct);
            (await open.Should().ThrowAsync<NpgsqlException>()).Which.InnerException.Should().BeOfType<AuthenticationException>(
                "the chain cannot be built from what the server sent");
        }

        server.Handshakes.Should().BePositive("the client reached the TLS handshake");
        issuer.Requests.Should().BeEmpty("CWE-918: no request leaves for the address the certificate names");
        if (OperatingSystem.IsLinux())
        {
            // The control: a connection Npgsql makes on its own fetches that address during the handshake.
            await using var plain = new NpgsqlConnection(connectionString);
            var open = () => plain.OpenAsync(Ct);
            await open.Should().ThrowAsync<NpgsqlException>();
            issuer.Requests.Should().NotBeEmpty("a handshake with the platform's default policy downloads the intermediate");
        }
    }

    [Fact]
    public async Task PostgreSql_Should_CompleteTheHandshake_When_ItsServerChainsToTheRootCertificate()
    {
        // The policy changes nothing for a server the root certificate issued: the client accepts it (the stub then closes, so the
        // open fails after the handshake, not in it).
        using var authority = TlsCollector.NewAuthority("Ariva test database CA");
        var certificate = Issue(authority);
        await using var server = TlsStub.Start(certificate, Preamble.Postgres);
        await using var connection = PostgresConnections.Create(DatabaseConnection(server.Port, TlsCollector.WritePem(authority, _folder)));

        var open = () => connection.OpenAsync(Ct);

        var failure = (await open.Should().ThrowAsync<Exception>()).Which;
        failure.Should().NotBeOfType<AuthenticationException>();
        failure.InnerException.Should().NotBeOfType<AuthenticationException>("the client accepted the server; only the protocol after it failed");
        server.Handshakes.Should().BePositive();
    }

    [Fact]
    public void PostgreSql_Should_TakeThePolicyOnEveryConnection_When_NHibernateOrAriva_MakesIt()
    {
        using var made = PostgresConnections.Create("Host=localhost");
        using var driven = (NpgsqlConnection)new NHibernatePostgresClientDriver().CreateConnection();
        var options = new SslClientAuthenticationOptions();

        made.SslClientAuthenticationOptionsCallback.Should().NotBeNull();
        driven.SslClientAuthenticationOptionsCallback.Should().NotBeNull("NHibernate's connections are hardened too");
        driven.SslClientAuthenticationOptionsCallback(options);
        options.CertificateChainPolicy.DisableCertificateDownloads.Should().BeTrue();
        options.CertificateChainPolicy.RevocationMode.Should().Be(X509RevocationMode.NoCheck);
        options.CertificateRevocationCheckMode.Should().Be(X509RevocationMode.NoCheck);
    }

    [Fact]
    public void PostgreSql_Should_BeOpenedThroughPostgresConnectionsOnly_When_TheHostsAndJobsConnect()
    {
        // Every connection takes the policy only if nothing else constructs one: a new call site uses PostgresConnections.Create.
        var root = Path.Combine(ArivaRoot(), "Platform");
        var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                        !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                        !f.Contains("Ariva.UnitTests", StringComparison.Ordinal) && !f.Contains("Ariva.IntegrationTests", StringComparison.Ordinal) &&
                        !f.EndsWith("PostgresConnections.cs", StringComparison.Ordinal))
            .Where(f => OpensItsOwnConnection().IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();

        offenders.Should().BeEmpty("a connection made elsewhere would download certificates while its handshake builds the chain");
        OpensItsOwnConnection().IsMatch("NpgsqlConnection c = new(text);").Should().BeTrue("the scan reads a target-typed new too");
        OpensItsOwnConnection().IsMatch("NpgsqlFactory.Instance.CreateConnection()").Should().BeTrue();
        OpensItsOwnConnection().IsMatch("new NpgsqlConnection { ConnectionString = text }").Should().BeTrue("and an initializer without parentheses");
    }

    [Theory]
    [InlineData("NHibernate.Driver.NpgsqlDriver, NHibernate", null)]
    [InlineData(null, "NHibernate.Connection.UserSuppliedConnectionProvider, NHibernate")]
    public void NHibernate_Should_RefuseToBuild_When_TheSettingsNameAnotherDriverOrProvider(string driver, string provider)
    {
        // ARV-226 review (L4): NHibernate's stock driver would open connections Ariva did not harden.
        var defaults = new NHibernateSettings();
        var settings = new DatabaseSettings
        {
            NHibernate = new NHibernateSettings { ConnectionDriver = driver ?? defaults.ConnectionDriver, ConnectionProvider = provider ?? defaults.ConnectionProvider }
        };

        var build = () => new NHibernateSessionFactoryProvider(settings).SessionFactory;

        build.Should().Throw<InvalidOperationException>().WithMessage("*NHibernatePostgresClientDriver*DriverConnectionProvider*");
        new NHibernateSessionFactoryProvider(new DatabaseSettings()).SessionFactory.Should().NotBeNull("Ariva's own driver and provider build");
    }

    [Theory]
    [InlineData("SslOnConnect")]
    [InlineData("StartTls")]
    public async Task Smtp_Should_DownloadNoCertificate_When_TheRelayLeavesOutItsIntermediate(string security)
    {
        // MailKit checks revocation online by default and gives SslStream no chain policy; the transport's client gives it one, for
        // TLS on connect and for STARTTLS (the default) alike.
        await using var issuer = await IssuerSite.StartAsync(Ct);
        var (authority, intermediate, certificate) = TlsCollector.IssueBehindAnIntermediate("Ariva test SMTP CA", issuer.Address);
        using var _ = authority;
        using var __ = intermediate;
        await using var relay = TlsStub.Start(certificate, security == "StartTls" ? Preamble.SmtpStartTls : Preamble.None);
        var transport = new MailKitTransport(new EmailSettings { Enabled = true, FromAddress = "ariva@example.com" },
            new SmtpSettings { Host = "localhost", Port = relay.Port, Security = security, TimeoutSeconds = 5 });

        var batch = await transport.SendAsync([new OutgoingEmail(Guid.NewGuid(), "duty@example.com", "Probe", "Probe")], Ct);

        (batch.Failures.Count + batch.NotTried.Count).Should().Be(1, "the relay's chain cannot be built from what it sent");
        relay.Handshakes.Should().BePositive("the client reached the TLS handshake");
        issuer.Requests.Should().BeEmpty("CWE-918: no request leaves for the address the relay's certificate names");
        if (OperatingSystem.IsLinux())
        {
            // The control: MailKit's own client fetches that address during the handshake.
            using var own = new MailKit.Net.Smtp.SmtpClient { Timeout = 5_000 };
            var connect = () => own.ConnectAsync("localhost", relay.Port, Enum.Parse<MailKit.Security.SecureSocketOptions>(security), Ct);
            await connect.Should().ThrowAsync<Exception>();
            issuer.Requests.Should().NotBeEmpty("a handshake allowed to download fetches the address the certificate names");
        }
    }

    [Fact]
    public async Task Kafka_Should_DownloadNoCertificate_When_VerifyingTheBrokerAgainstItsCaLocation()
    {
        // librdkafka's own behaviour (OpenSSL), recorded in the crypto inventory (A15): SSL against Kafka:SslCaLocation.
        await using var issuer = await IssuerSite.StartAsync(Ct);
        var (authority, intermediate, certificate) = TlsCollector.IssueBehindAnIntermediate("Ariva test Kafka CA", issuer.Address);
        using var _ = authority;
        using var __ = intermediate;
        await using var server = TlsStub.Start(certificate, Preamble.None);
        using var admin = new AdminClientBuilder(new AdminClientConfig
            {
                BootstrapServers = $"localhost:{server.Port}",
                SecurityProtocol = SecurityProtocol.Ssl,
                SslCaLocation = TlsCollector.WritePem(authority, _folder),
                SslEndpointIdentificationAlgorithm = SslEndpointIdentificationAlgorithm.Https,
                SocketTimeoutMs = 5_000
            })
            .SetLogHandler((_, _) => { })
            .SetErrorHandler((_, _) => { })
            .Build();

        var metadata = () => admin.GetMetadata(TimeSpan.FromSeconds(4));

        metadata.Should().Throw<KafkaException>("the chain cannot be built from what the broker sent");
        server.Handshakes.Should().BePositive("the client reached the TLS handshake");
        issuer.Requests.Should().BeEmpty("librdkafka downloads no certificate");
    }

    #endregion

    #region Methods

    public void Dispose() => _folder.Delete(recursive: true);

    /// <summary>A PostgreSQL connection made other than by PostgresConnections: constructed, target-typed, from a data source or a factory.</summary>
    [GeneratedRegex(@"new\s+NpgsqlConnection\b|NpgsqlConnection\s+\w+\s*=\s*new\s*\(|NpgsqlDataSource|NpgsqlFactory|DbProviderFactories")]
    private static partial Regex OpensItsOwnConnection();

    /// <summary>A connection string that verifies the server on loopback <paramref name="port"/> against <paramref name="root"/>.</summary>
    private static string DatabaseConnection(int port, string root) => new NpgsqlConnectionStringBuilder
    {
        Host = "localhost",
        Port = port,
        Database = "ariva",
        Username = "ariva",
        Password = "not-a-secret",
        SslMode = SslMode.VerifyFull,
        RootCertificate = root,
        Timeout = 5,
        Pooling = false
    }.ConnectionString;

    /// <summary>A server certificate for localhost that <paramref name="authority"/> issued itself, with its key.</summary>
    private static X509Certificate2 Issue(X509Certificate2 authority)
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=localhost", key, System.Security.Cryptography.HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1")], false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var issuerKey = authority.GetECDsaPrivateKey();
        using var issued = request.Create(authority.SubjectName, X509SignatureGenerator.CreateForECDsa(issuerKey), DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1), System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        return X509CertificateLoader.LoadPkcs12(issued.CopyWithPrivateKey(key).Export(X509ContentType.Pfx), null);
    }

    /// <summary>The repository's root: the folder that holds Ariva.slnx, above the test's own folder.</summary>
    private static string ArivaRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "Ariva.slnx")))
                return folder.FullName;
        }

        throw new InvalidOperationException("Ariva.slnx not found above the test's folder.");
    }

    /// <summary>The client side of a TLS handshake with the server on loopback <paramref name="port"/>, with these options.</summary>
    private static async Task HandshakeAsync(int port, SslClientAuthenticationOptions options)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, Ct);
        await using var tls = new SslStream(client.GetStream());
        await tls.AuthenticateAsClientAsync(options, Ct);
    }

    /// <summary>Gives a connection that never connects time for its handshakes (its connect timeout is two seconds).</summary>
    private static async Task WhileConnectingAsync(ConnectionMultiplexer connection)
    {
        var until = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < until && !connection.IsConnected)
            await Task.Delay(100, Ct);
    }

    #endregion

    #region Types

    /// <summary>
    /// A TLS server on loopback that speaks no protocol: it answers a PostgreSQL SSLRequest with 'S' when asked to, runs the server
    /// side of the handshake with its certificate alone (no intermediate sent, no chain built: offline), counts the handshakes it
    /// began and closes the connection.
    /// </summary>
    private sealed class TlsStub : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly SslStreamCertificateContext _context;
        private readonly Preamble _preamble;
        private readonly X509Certificate2 _certificate;
        private Task _loop;
        private int _handshakes;

        private TlsStub(X509Certificate2 certificate, Preamble preamble)
        {
            _certificate = certificate;
            _context = SslStreamCertificateContext.Create(certificate, [], offline: true);
            _preamble = preamble;
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Handshakes => Volatile.Read(ref _handshakes);

        public static TlsStub Start(X509Certificate2 certificate, Preamble preamble)
        {
            var stub = new TlsStub(certificate, preamble);
            stub._listener.Start();
            stub._loop = stub.AcceptAsync();
            return stub;
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
                // Stopped.
            }

            _stop.Dispose();
            _certificate.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                _ = ServeAsync(client);
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    if (_preamble == Preamble.Postgres)
                    {
                        var request = new byte[8];
                        await stream.ReadExactlyAsync(request, _stop.Token);
                        await stream.WriteAsync("S"u8.ToArray(), _stop.Token);
                    }
                    else if (_preamble == Preamble.SmtpStartTls)
                    {
                        await stream.WriteAsync("220 localhost ESMTP\r\n"u8.ToArray(), _stop.Token);
                        await ReadLineAsync(stream, _stop.Token);
                        await stream.WriteAsync("250-localhost\r\n250 STARTTLS\r\n"u8.ToArray(), _stop.Token);
                        await ReadLineAsync(stream, _stop.Token);
                        await stream.WriteAsync("220 Ready to start TLS\r\n"u8.ToArray(), _stop.Token);
                    }

                    Interlocked.Increment(ref _handshakes);
                    await using var tls = new SslStream(stream);
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificateContext = _context }, _stop.Token)
                        .WaitAsync(TimeSpan.FromSeconds(10), _stop.Token);
                }
                catch (Exception e) when (e is IOException or AuthenticationException or OperationCanceledException or TimeoutException or SocketException)
                {
                    // The client refused the certificate or went away: the handshake was all there was to see.
                }
            }
        }

        /// <summary>Reads one command line byte by byte, so nothing of the TLS handshake after it is consumed; at most 512 bytes.</summary>
        private static async Task ReadLineAsync(NetworkStream stream, CancellationToken ct)
        {
            var one = new byte[1];
            for (var read = 0; read < 512; read++)
            {
                await stream.ReadExactlyAsync(one, ct);
                if (one[0] == (byte)'\n')
                    return;
            }
        }
    }

    /// <summary>What a <see cref="TlsStub"/> says before the TLS handshake: nothing, PostgreSQL's SSLRequest answer, or SMTP up to STARTTLS.</summary>
    private enum Preamble
    {
        None,
        Postgres,
        SmtpStartTls
    }

    #endregion
}
