using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Ariva.Infra.Settings;
using Ariva.IntegrationTests.Setup;
using DotNet.Testcontainers.Configurations;
using FluentAssertions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Ariva.IntegrationTests.Persistence;

/// <summary>
/// ARV-082a (CWE-295, ASVS V12.3.2, V12.3.4) against a PostgreSQL that serves TLS with a certificate from a test internal CA,
/// on the production image, its key mounted as the chart mounts it: a host's connection (<see cref="DatabaseSettings"/> with TLS and <c>RootCertificate</c>) is
/// encrypted and verified against the pinned CA alone. A CA file that did not issue the server's certificate, or a host
/// name the certificate does not name, refuses the connection; so does the image's own system store, which never knew the
/// internal CA. Its own container, so the shared one keeps serving plain connections to the other tests.
/// </summary>
public sealed class DatabaseTlsTests : IAsyncLifetime
{
    private const string Username = "ariva";

    /// <summary>The image's postgres user and group.</summary>
    private const uint PostgresUser = 1000;

    private const uint Root = 0;
    private readonly string _password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private readonly string _folder = Directory.CreateTempSubdirectory("ariva-db-tls-").FullName;
    private PostgreSqlContainer _container;
    private string _ourCa;
    private string _otherCa;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        using var ca = Authority("CN=Ariva test internal CA");
        using var other = Authority("CN=Another CA");
        // The certificate names localhost only, so 127.0.0.1 is a host name it does not name.
        using var server = Server(ca, "localhost");
        _ourCa = Pem("ca.crt", ca.ExportCertificatePem());
        _otherCa = Pem("other.crt", other.ExportCertificatePem());

        // The key as the chart mounts it (a secret volume with fsGroup 1000): root's, group 1000 (postgres's), mode 0440, which
        // PostgreSQL accepts for a key root owns (u=rw,g=r or less) and reads through its group.
        _container = new PostgreSqlBuilder()
            .WithImage(PostgresFixture.Image)
            .WithDatabase("ariva_tls")
            .WithUsername(Username)
            .WithPassword(_password)
            .WithEnvironment("TIMESCALEDB_TELEMETRY", "off")
            .WithResourceMapping(Encoding.ASCII.GetBytes(server.ExportCertificatePem()), "/tmp/tls/server.crt", PostgresUser, PostgresUser,
                UnixFileModes.UserRead | UnixFileModes.GroupRead | UnixFileModes.OtherRead)
            .WithResourceMapping(Encoding.ASCII.GetBytes(server.GetECDsaPrivateKey()!.ExportPkcs8PrivateKeyPem()), "/tmp/tls/server.key", Root,
                PostgresUser, UnixFileModes.UserRead | UnixFileModes.GroupRead)
            // The chart's TLS settings (TLS 1.2 at least, its TLS 1.2 suites ECDHE with AEAD only).
            .WithCommand("-c", "ssl=on", "-c", "ssl_cert_file=/tmp/tls/server.crt", "-c", "ssl_key_file=/tmp/tls/server.key", "-c",
                "ssl_min_protocol_version=TLSv1.2", "-c", "ssl_ciphers=ECDHE+AESGCM:ECDHE+CHACHA20:!aNULL")
            .Build();
        await _container.StartAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
        Directory.Delete(_folder, recursive: true);
    }

    private string Pem(string name, string text)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, text);
        return path;
    }

    private DatabaseSettings Settings(string host, string rootCertificate) => new()
    {
        Host = host,
        Port = _container.GetMappedPublicPort(PostgreSqlBuilder.PostgreSqlPort),
        Name = "ariva_tls",
        Username = Username,
        Password = _password,
        UseEncryption = true,
        RootCertificate = rootCertificate
    };

    [Fact]
    public async Task Connection_Should_UseTlsVerifiedAgainstThePinnedCa_When_TheServerCertificateIsTheInternalCas()
    {
        await using var connection = new NpgsqlConnection(Settings("localhost", _ourCa).BuildConnectionString());
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT ssl, version FROM pg_stat_ssl WHERE pid = pg_backend_pid()", connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);

        (await reader.ReadAsync(Ct)).Should().BeTrue();
        reader.GetBoolean(0).Should().BeTrue("the connection is encrypted");
        reader.GetString(1).Should().BeOneOf("TLSv1.2", "TLSv1.3");
    }

    public static TheoryData<string, string> Refused => new()
    {
        { "other", "a CA file that did not issue the server's certificate" },
        { "name", "a host name the certificate does not name" },
        { "system", "no CA named: the image's system store, which never knew the internal CA" }
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task Connection_Should_BeRefused_When_TheServerIsNotTheOneThePinnedCaVouchesFor(string kind, string reason)
    {
        var settings = kind switch
        {
            "other" => Settings("localhost", _otherCa),
            "name" => Settings("127.0.0.1", _ourCa),
            _ => Settings("localhost", "")
        };
        await using var connection = new NpgsqlConnection(settings.BuildConnectionString());

        var open = () => connection.OpenAsync(Ct);

        (await open.Should().ThrowAsync<Exception>(reason)).Which.Should().Match<Exception>(e =>
            e is NpgsqlException || e is System.Security.Authentication.AuthenticationException, reason);
    }

    private static X509Certificate2 Authority(string name)
    {
        // Not disposed here: the certificate signs the server's with it.
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(name, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static X509Certificate2 Server(X509Certificate2 issuer, string dnsName)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={dnsName}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(dnsName);
        request.CertificateExtensions.Add(names.Build());
        using var signed = request.Create(issuer, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(12), RandomNumberGenerator.GetBytes(16));
        return signed.CopyWithPrivateKey(key);
    }
}
