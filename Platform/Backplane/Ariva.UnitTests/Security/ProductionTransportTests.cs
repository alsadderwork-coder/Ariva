using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Ariva.Di.Extensions;
using Ariva.Infra.Caching;
using Ariva.Infra.Settings;
using Ariva.ServiceDefaults;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Ariva.UnitTests.Setup;
using StackExchange.Redis;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-082a (CWE-319, CWE-295; ASVS V12.3.1, V12.3.3, V12.3.4): in k8s-prd a host does not start unless PostgreSQL and Redis
/// are reached over TLS verified against a pinned internal CA whose file is there, and the OTLP collector over https (as
/// Kafka over SaslSsl, ARV-020), nor when its logs would reach Loki or the OTLP collector in clear. Elsewhere the same
/// settings are accepted as configured. PostgreSQL's and Redis's connections carry the pinned CA (Redis's for the cache and
/// the live hub's backplane alike): a server certificate from another CA is refused even when the system store would trust it.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class ProductionTransportTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("ariva-transport-").FullName;
    private readonly X509Certificate2 _ca = Authority("CN=Ariva internal CA");
    private readonly string _caPath;

    public ProductionTransportTests()
    {
        _caPath = Path.Combine(_folder, "ca.crt");
        File.WriteAllText(_caPath, _ca.ExportCertificatePem());
    }

    public void Dispose()
    {
        _ca.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private static IConfiguration Configuration(string environment, Dictionary<string, string> values)
    {
        values["Application:Environment"] = environment;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    #region PostgreSQL

    public static TheoryData<string, string, string, bool, string> DatabaseCases => new()
    {
        { "k8s-prd", "false", "", true, "*UseEncryption must be true*" },
        { "k8s-prd", "true", "", true, "*RootCertificate must name*" },
        { "k8s-prd", "true", "missing", true, "*does not exist*" },
        { "k8s-prd", "true", "ca", false, null },
        { "k8s-dev", "false", "", false, null },
        { "k8s-demo", "true", "missing", false, null }
    };

    [Theory]
    [MemberData(nameof(DatabaseCases))]
    public void AddArivaPersistence_Should_RefuseUnpinnedOrPlainPostgres_When_InProduction(string environment, string encryption, string root, bool refused, string message)
    {
        var configuration = Configuration(environment, new Dictionary<string, string>
        {
            ["Database:UseEncryption"] = encryption,
            ["Database:RootCertificate"] = root switch { "ca" => _caPath, "missing" => Path.Combine(_folder, "absent.crt"), _ => "" },
            ["Database:Password"] = "runtime-secret-value"
        });

        var add = () => new ServiceCollection().AddArivaPersistence(configuration);

        if (refused)
            add.Should().Throw<InvalidOperationException>().WithMessage(message).Which.Message.Should().NotContain("runtime-secret-value");
        else
            add.Should().NotThrow();
    }

    [Fact]
    public void BuildConnectionString_Should_PinTheInternalCa_When_EncryptionIsOn()
    {
        var settings = new DatabaseSettings
        {
            UseEncryption = true,
            RootCertificate = _caPath,
            Migration = new MigrationLoginSettings { Username = "ariva", Password = "owner-secret-value" }
        };

        foreach (var connection in new[] { settings.BuildConnectionString(), settings.BuildMigrationConnectionString() })
        {
            var parsed = new NpgsqlConnectionStringBuilder(connection);
            parsed.SslMode.Should().Be(SslMode.VerifyFull, "the certificate and the host name are both verified");
            parsed.RootCertificate.Should().Be(_caPath, "the server is verified against the internal CA alone");
        }

        var reader = new ValidationReaderSettings { Username = "ariva_validation", Password = "reader-secret-value-that-is-long-enough-01" };
        new NpgsqlConnectionStringBuilder(reader.BuildReaderConnectionString(settings)).RootCertificate.Should().Be(_caPath, "every login's connection");
        new NpgsqlConnectionStringBuilder(new DatabaseSettings { RootCertificate = _caPath }.BuildConnectionString()).RootCertificate
            .Should().BeNullOrEmpty("no CA is named for a connection without TLS");
    }

    [Fact]
    public void ProductionSettings_Should_PinEveryBackendTheChartMounts_When_TheBaseFileIsRead()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.base.json"))
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.base.k8s-prd.json"))
            .Build();

        var database = DatabaseSettings.FromConfiguration(configuration);
        database.UseEncryption.Should().BeTrue();
        database.RootCertificate.Should().Be("/app/secrets/database-ca/ca.crt", "the chart mounts ariva-database-ca there");
        database.ProductionTransportProblem(path => path == "/app/secrets/database-ca/ca.crt").Should().BeNull();

        var redis = RedisSettings.From(configuration);
        redis.CaCertificatePath.Should().Be("/app/secrets/redis-ca/ca.crt", "the chart mounts ariva-redis-ca there");
        redis.ProductionTransportProblem(path => path == "/app/secrets/redis-ca/ca.crt").Should().BeNull();
    }

    #endregion

    #region Redis

    public static TheoryData<string, string, string, string, bool, string> RedisCases => new()
    {
        { "k8s-prd", "true", "redis:6379,password=redis-secret-value", "ca", true, "*ssl=true*" },
        { "k8s-prd", "true", "redis:6379,ssl=true,password=redis-secret-value", "", true, "*CaCertificatePath must name*" },
        { "k8s-prd", "true", "redis:6379,ssl=true,password=redis-secret-value", "missing", true, "*does not exist*" },
        { "k8s-prd", "true", "redis:6379,ssl=true,password=redis-secret-value", "ca", false, null },
        { "k8s-prd", "false", "redis:6379", "", false, null },
        { "k8s-dev", "true", "redis:6379", "", false, null }
    };

    [Theory]
    [MemberData(nameof(RedisCases))]
    public void AddArivaCaching_Should_RefuseUnpinnedOrPlainRedis_When_InProduction(string environment, string enabled, string connection, string ca, bool refused,
        string message)
    {
        var configuration = Configuration(environment, new Dictionary<string, string>
        {
            ["Redis:Enabled"] = enabled,
            ["Redis:ConnectionString"] = connection,
            ["Redis:CaCertificatePath"] = ca switch { "ca" => _caPath, "missing" => Path.Combine(_folder, "absent.crt"), _ => "" }
        });

        var add = () => new ServiceCollection().AddArivaCaching(configuration);

        if (refused)
            add.Should().Throw<InvalidOperationException>().WithMessage(message).Which.Message.Should().NotContain("redis-secret-value");
        else
            add.Should().NotThrow();
    }

    public static TheoryData<string, SslPolicyErrors, bool, string> PinnedCases => new()
    {
        { "ours", SslPolicyErrors.RemoteCertificateChainErrors, true, "issued by the internal CA, which the system store does not know" },
        { "ours", SslPolicyErrors.None, true, "issued by the internal CA, had the system store known it" },
        { "theirs", SslPolicyErrors.None, false, "a certificate the system store trusts but no pinned CA issued" },
        { "theirs", SslPolicyErrors.RemoteCertificateChainErrors, false, "a certificate from any other CA" },
        { "ours", SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors, false, "issued for another host" },
        { "ours", SslPolicyErrors.RemoteCertificateNotAvailable, false, "no certificate sent" }
    };

    [Theory]
    [MemberData(nameof(PinnedCases))]
    public void Options_Should_TrustOnlyTheInternalCa_When_RedisUsesTls(string issuer, SslPolicyErrors errors, bool accepted, string reason)
    {
        var options = new RedisSettings { ConnectionString = "redis:6379,ssl=true", CaCertificatePath = _caPath }.Options();
        using var stranger = Authority("CN=Another CA");
        using var certificate = Leaf(issuer == "ours" ? _ca : stranger, "CN=redis");

        options.Ssl.Should().BeTrue();
        options.AbortOnConnectFail.Should().BeFalse();
        var callback = CallbackOf(options);
        callback.Should().NotBeNull("the pinned CA decides, not the system store");
        Validate(callback, certificate, errors).Should().Be(accepted, reason);
    }

    [Fact]
    public void Options_Should_TrustEveryCaOfTheFile_When_ItHoldsMore()
    {
        using var next = Authority("CN=Ariva internal CA next");
        var both = Path.Combine(_folder, "both.crt");
        File.WriteAllText(both, _ca.ExportCertificatePem() + "\n" + next.ExportCertificatePem());
        using var renewed = Leaf(next, "CN=redis");

        var callback = CallbackOf(new RedisSettings { ConnectionString = "redis:6379,ssl=true", CaCertificatePath = both }.Options());

        Validate(callback, renewed, SslPolicyErrors.RemoteCertificateChainErrors).Should().BeTrue("a CA rotation names the old and the new CA in one file");
        CallbackOf(new RedisSettings { ConnectionString = "redis:6379", CaCertificatePath = _caPath }.Options()).Should().BeNull("no TLS, nothing to pin");
    }

    [Fact]
    public void Options_Should_RefuseAChainTheSystemStoreTrusts_When_ItsRootIsAmongThePresentedCertificates()
    {
        using var stranger = Authority("CN=Another CA");
        using var theirs = Leaf(stranger, "CN=redis");
        var callback = CallbackOf(new RedisSettings { ConnectionString = "redis:6379,ssl=true", CaCertificatePath = _caPath }.Options());
        // The handshake's own chain, as with a public CA: built and trusted, its root among its elements.
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(stranger);
        chain.Build(theirs).Should().BeTrue();
        chain.ChainElements.Select(e => e.Certificate.Thumbprint).Should().Contain(stranger.Thumbprint);

        callback(new object(), theirs, chain, SslPolicyErrors.None).Should().BeFalse("a root the server sends is never trusted for being sent");
    }

    [Fact]
    public async Task LiveHubBackplane_Should_UseThePinnedRedisOptions_When_RedisIsOn()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Main, configure: builder =>
        {
            builder.UseSetting("Redis:Enabled", "true");
            builder.UseSetting("Redis:ConnectionString", "localhost:1,ssl=true");
            builder.UseSetting("Redis:CaCertificatePath", _caPath);
        });
        using var stranger = Authority("CN=Another CA");
        using var theirs = Leaf(stranger, "CN=redis");
        using var ours = Leaf(_ca, "CN=redis");

        var options = app.Services.GetRequiredService<IOptions<RedisOptions>>().Value.Configuration;

        options.Ssl.Should().BeTrue();
        options.ChannelPrefix.ToString().Should().Be("ariva:signalr", "the hub's own channels, apart from the cache's");
        var callback = CallbackOf(options);
        callback.Should().NotBeNull("the live hub's backplane is pinned like the cache");
        Validate(callback, ours, SslPolicyErrors.RemoteCertificateChainErrors).Should().BeTrue();
        Validate(callback, theirs, SslPolicyErrors.None).Should().BeFalse("a certificate the system store trusts but no pinned CA issued");
    }

    /// <summary>The validation the options hand the TLS handshake (an event of ConfigurationOptions, kept in a private member).</summary>
    private static RemoteCertificateValidationCallback CallbackOf(ConfigurationOptions options) =>
        typeof(ConfigurationOptions).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(f => f.FieldType == typeof(RemoteCertificateValidationCallback))
            .Select(f => (RemoteCertificateValidationCallback)f.GetValue(options))
            .SingleOrDefault(c => c is not null);

    private static bool Validate(RemoteCertificateValidationCallback callback, X509Certificate2 certificate, SslPolicyErrors errors)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.Build(certificate);
        // What the handshake made of the certificate with the system store: the callback must decide on its own.
        return callback(new object(), certificate, chain, errors);
    }

    #endregion

    #region OTLP

    [Theory]
    [InlineData("k8s-prd", "http://collector:4317", true)]
    [InlineData("k8s-prd", "https://collector:4317", false)]
    [InlineData("k8s-prd", "", false)]
    [InlineData("k8s-dev", "http://collector:4317", false)]
    public void AddArivaTelemetry_Should_RefusePlainOtlp_When_InProduction(string environment, string endpoint, bool refused)
    {
        var configuration = Configuration(environment, new Dictionary<string, string> { ["OTEL_EXPORTER_OTLP_ENDPOINT"] = endpoint });

        var add = () => new ServiceCollection().AddArivaTelemetry(configuration, new ArivaServiceDefaultsOptions { ServiceName = "test" });

        if (refused)
            add.Should().Throw<InvalidOperationException>().WithMessage("*OTLP endpoint must be https*");
        else
            add.Should().NotThrow();
    }

    [Theory]
    [InlineData("k8s-prd", "true", "http://loki:3100", null, true, "*Loki:Uri must be https*")]
    [InlineData("k8s-prd", "true", "https://loki:3100", null, false, null)]
    [InlineData("k8s-prd", "false", "http://loki:3100", null, false, null)]
    [InlineData("k8s-dev", "true", "http://loki:3100", null, false, null)]
    [InlineData("k8s-prd", "false", "", "http://collector:4317", true, "*OTEL_EXPORTER_OTLP_ENDPOINT must be https*")]
    [InlineData("k8s-prd", "false", "", "https://collector:4317", false, null)]
    public void ArivaLogging_Should_RefusePlainLogShipping_When_InProduction(string environment, string loki, string lokiUri, string variable, bool refused,
        string message)
    {
        var values = new Dictionary<string, string> { ["Loki:Enabled"] = loki, ["Loki:Uri"] = lokiUri, ["OTEL_EXPORTER_OTLP_ENDPOINT"] = variable };
        if (variable is not null)
            values["Otlp:Endpoint"] = "https://collector:4317";
        var configuration = Configuration(environment, values);

        var configure = () => Ariva.Api.Common.Logging.ArivaLogging.Configure(new Serilog.LoggerConfiguration(), configuration, environment);

        if (refused)
            configure.Should().Throw<InvalidOperationException>().WithMessage(message, "the sink reads the variable after Ariva's own setting");
        else
            configure.Should().NotThrow();
    }

    [Fact]
    public async Task StoredNameCheck_Should_StopBeforeConnecting_When_ProductionPostgresIsNotPinned()
    {
        // A listener stands in for the database: a check that connected would leave a connection waiting on it, while an
        // unreachable host would fail the same way as the guard and prove nothing.
        var database = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        database.Start();
        try
        {
            var configuration = Configuration("k8s-prd", new Dictionary<string, string>
            {
                ["Database:Host"] = "127.0.0.1",
                ["Database:Port"] = ((System.Net.IPEndPoint)database.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Database:UseEncryption"] = "false",
                ["Database:Password"] = "runtime-secret-value"
            });
            using var output = new StringWriter();

            var exit = await Ariva.Di.StoredNameCheckCommand.RunAsync(configuration, output, TestContext.Current.CancellationToken);

            exit.Should().Be(1);
            database.Pending().Should().BeFalse("the check stops before any connection to the database");
            output.ToString().Should().NotContain("runtime-secret-value");
        }
        finally
        {
            database.Stop();
        }
    }

    #endregion

    private static X509Certificate2 Authority(string name)
    {
        // Not disposed here: the certificate signs leaves with it.
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(name, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static X509Certificate2 Leaf(X509Certificate2 issuer, string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(name, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        var serial = RandomNumberGenerator.GetBytes(16);
        return request.Create(issuer, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(12), serial);
    }
}
