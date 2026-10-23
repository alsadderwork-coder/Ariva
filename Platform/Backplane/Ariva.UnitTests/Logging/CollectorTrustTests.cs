using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Ariva.Api.Common.Hosting;
using Ariva.Api.Common.Logging;
using Ariva.ServiceDefaults;
using Ariva.UnitTests.Logging.Fixtures;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;

namespace Ariva.UnitTests.Logging;

/// <summary>
/// ARV-200 (ASVS 5.0 V12.3.1, V12.3.2, V12.3.4, V16.4.3): a collector whose certificate comes from the site PKI is reached
/// when its CA file (<c>Otlp:RootCertificate</c>, <c>Loki:RootCertificate</c>) names the CA that issued it, and never when it
/// names another CA, or when the certificate names another host: the trace and metric exporters over gRPC and http/protobuf,
/// Serilog's Loki sink and its OTLP sink, each against a loopback collector over TLS. A collector that leaves out its
/// intermediate is never helped by a download (CWE-918), and the pinned clients' shape is checked (the CA file's certificates
/// alone, server authentication, no revocation check, no download, no callback, no redirect, no cookie). The pin stays on the
/// collectors' clients (every other client keeps the public roots), a CA file that cannot serve stops the host, a CA file with
/// an http address does too, and so do OpenTelemetry's own certificate variables outside vm-local.
/// </summary>
public sealed class CollectorTrustTests : IDisposable
{
    private const string Source = "Ariva.UnitTests.CollectorTrustProbe";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>How long a trusted collector may take to get its first request.</summary>
    private static TimeSpan Patience => TimeSpan.FromSeconds(20);

    /// <summary>How long an untrusted collector is watched after the export: its handshake fails at once.</summary>
    private static TimeSpan Silence => TimeSpan.FromSeconds(2);

    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("ariva-collector-ca-");

    public static TheoryData<string, string> TraceProtocols => new()
    {
        { "http/protobuf", "POST /v1/traces" },
        { "grpc", "POST /opentelemetry.proto.collector.trace.v1.TraceService/Export" }
    };

    public static TheoryData<string, string> MetricProtocols => new()
    {
        { "http/protobuf", "POST /v1/metrics" },
        { "grpc", "POST /opentelemetry.proto.collector.metrics.v1.MetricsService/Export" }
    };

    public static TheoryData<string, string> LogProtocols => new()
    {
        { "http/protobuf", "POST /v1/logs" },
        { "grpc", "POST /opentelemetry.proto.collector.logs.v1.LogsService/Export" }
    };

    public void Dispose() => _folder.Delete(recursive: true);

    #region The CA file

    [Fact]
    public void RootsFrom_Should_ReturnNull_When_NoCaFileIsSet()
    {
        CollectorTrust.RootsFrom(Configuration(), CollectorTrust.OtlpSetting).Should().BeNull("without a CA file the collector is verified against the public roots, as before");
    }

    [Fact]
    public void RootsFrom_Should_ReadEveryCertificate_When_TheFileIsABundleOfTheSitesCas()
    {
        using var first = TlsCollector.NewAuthority("Ariva test site CA 1");
        using var second = TlsCollector.NewAuthority("Ariva test site CA 2");
        var path = Path.Combine(_folder.FullName, "bundle.crt");
        File.WriteAllText(path, first.ExportCertificatePem() + "\n" + second.ExportCertificatePem() + "\n");

        var roots = CollectorTrust.RootsFrom(Configuration((CollectorTrust.LokiSetting, path)), CollectorTrust.LokiSetting);

        roots.Should().HaveCount(2);
        roots.Select(r => r.Thumbprint).Should().BeEquivalentTo([first.Thumbprint, second.Thumbprint]);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("a private key")]
    [InlineData("no certificate")]
    [InlineData("not PEM")]
    [InlineData("too large")]
    public void RootsFrom_Should_StopTheHost_When_TheFileCannotServeAsTheCollectorsCa(string kind)
    {
        var path = Path.Combine(_folder.FullName, "ca.crt");
        using var authority = TlsCollector.NewAuthority("Ariva test site CA");
        switch (kind)
        {
            case "a private key":
                using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
                    File.WriteAllText(path, authority.ExportCertificatePem() + "\n" + key.ExportPkcs8PrivateKeyPem());
                break;
            case "no certificate":
                File.WriteAllText(path, "# the site CA goes here\n");
                break;
            case "not PEM":
                File.WriteAllText(path, "-----BEGIN CERTIFICATE-----\nnot base64 at all\n-----END CERTIFICATE-----\n");
                break;
            case "too large":
                File.WriteAllText(path, authority.ExportCertificatePem() + "\n" + new string('#', CollectorTrust.MaxFileBytes));
                break;
        }

        var read = () => CollectorTrust.RootsFrom(Configuration((CollectorTrust.OtlpSetting, path)), CollectorTrust.OtlpSetting);

        read.Should().Throw<InvalidOperationException>(kind).WithMessage($"*{CollectorTrust.OtlpSetting}*")
            .Which.Message.Should().NotContain("BEGIN", "a refusal names the setting and the file, never its contents");
    }

    [Fact]
    public void RootsFrom_Should_StopTheHost_When_ThePathNamesADevice()
    {
        // A wrong path to an endless file stops the host at the size bound instead of hanging it at start.
        Assert.SkipUnless(OperatingSystem.IsLinux(), "/dev/zero is a Linux device");

        var read = () => CollectorTrust.RootsFrom(Configuration((CollectorTrust.OtlpSetting, "/dev/zero")), CollectorTrust.OtlpSetting);

        read.Should().Throw<InvalidOperationException>().WithMessage($"{CollectorTrust.OtlpSetting} (/dev/zero) is larger than*");
    }

    #endregion

    #region The pinned clients

    [Theory]
    [InlineData(CollectorTrust.LokiClient)]
    [InlineData(CollectorTrust.OtlpLogsClient)]
    [InlineData(CollectorTrust.OtlpExporterClient)]
    public async Task PinnedClient_Should_TrustTheCaFileAloneDownloadNothingAndFollowNoRedirect_When_ACaFileIsSet(string client)
    {
        using var authority = TlsCollector.NewAuthority("Ariva test site CA");
        await using var provider = HostServices(TlsCollector.WritePem(authority, _folder));

        // The log sinks' clients come from their own factory (CollectorClients), the exporters' from the host's.
        var handler = PrimaryHandler(client == CollectorTrust.OtlpExporterClient
            ? provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(client)
            : provider.GetRequiredService<CollectorClients>().CreateHandler(client));

        var policy = handler.SslOptions.CertificateChainPolicy;
        policy.Should().NotBeNull("the collector's client has a chain policy of its own");
        policy.TrustMode.Should().Be(X509ChainTrustMode.CustomRootTrust, "the system roots are never consulted");
        policy.CustomTrustStore.Select(c => c.Thumbprint).Should().Equal([authority.Thumbprint], "the CA file's certificates alone are trusted");
        policy.RevocationMode.Should().Be(X509RevocationMode.NoCheck);
        policy.DisableCertificateDownloads.Should().BeTrue("CWE-918: no request leaves for an address a certificate names");
        policy.ApplicationPolicy.Cast<Oid>().Select(o => o.Value).Should().Equal(["1.3.6.1.5.5.7.3.1"], "the collector's certificate must be one for a server");
        handler.SslOptions.RemoteCertificateValidationCallback.Should().BeNull("only the handshake's own check decides, and it checks the host name");
        handler.AllowAutoRedirect.Should().BeFalse("a redirect would send a batch and its headers to another host");
        handler.UseCookies.Should().BeFalse();
        (client == CollectorTrust.OtlpExporterClient ? CollectorTrust.IsPinned(provider, client) : provider.GetRequiredService<CollectorClients>().IsPinned(client))
            .Should().BeTrue();
    }

    [Fact]
    public async Task ExporterClient_Should_KeepTheExportDeadlineUnderEveryOptionsName_When_ACaFileIsSet()
    {
        using var authority = TlsCollector.NewAuthority("Ariva test site CA");
        await using var provider = HostServices(TlsCollector.WritePem(authority, _folder));
        var monitor = provider.GetRequiredService<IOptionsMonitor<OtlpExporterOptions>>();

        foreach (var name in new[] { string.Empty, "another exporter" })
        {
            var exporter = monitor.Get(name);
            using var client = exporter.HttpClientFactory();

            client.Timeout.Should().Be(TimeSpan.FromMilliseconds(exporter.TimeoutMilliseconds), $"the export deadline holds ({name})");
            client.MaxResponseContentBufferSize.Should().Be(1024 * 1024, "a collector's answer is small");
        }
    }

    [Fact]
    public async Task PinnedClient_Should_DownloadNoCertificate_When_TheCollectorLeavesOutItsIntermediate()
    {
        await using var issuer = await IssuerSite.StartAsync(Ct);
        await using var collector = await TlsCollector.StartBehindAnIntermediateAsync("Ariva test site CA", issuer.Address, Ct);
        var roots = new X509Certificate2Collection();
        roots.ImportFromPem(collector.Authority.ExportCertificatePem());
        var services = new ServiceCollection();
        CollectorTrust.Pin(services, roots, [CollectorTrust.LokiClient]);
        await using var provider = services.BuildServiceProvider();
        using var pinned = provider.GetRequiredService<IHttpClientFactory>().CreateClient(CollectorTrust.LokiClient);

        var send = () => pinned.GetAsync(new Uri(collector.Address), Ct);

        await send.Should().ThrowAsync<HttpRequestException>("the chain cannot be built from what the collector sent");
        issuer.Requests.Should().BeEmpty("CWE-918: no request leaves for the address the certificate names");
        collector.Requests.Should().BeEmpty();
        if (OperatingSystem.IsLinux())
        {
            // The control: a chain that may download does fetch that address, so the silence above is the pin's.
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(collector.Authority);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(10);
            chain.Build(collector.ServerCertificate).Should().BeFalse("the issuer's address hands out nothing");
            issuer.Requests.Should().NotBeEmpty("a chain allowed to download fetches the address the certificate names");
        }
    }

    [Fact]
    public async Task PublicClients_Should_DownloadNoCertificate_When_ACollectorWithoutACaFileLeavesOutItsIntermediate()
    {
        // ARV-226: a collector reached over https without a CA file of its own gets a client of Ariva's pinned without roots, so
        // with the image's public CAs, which downloads no certificate; a client with the platform's default policy fetches the
        // address the collector's certificate names, before the collector is authenticated.
        await using var issuer = await IssuerSite.StartAsync(Ct);
        await using var collector = await TlsCollector.StartBehindAnIntermediateAsync("Ariva test public CA", issuer.Address, Ct);
        var services = new ServiceCollection();
        CollectorTrust.Pin(services, null, [CollectorTrust.LokiClient]);
        services.AddHttpClient("plain");
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        using var pinned = factory.CreateClient(CollectorTrust.LokiClient);

        var send = () => pinned.GetAsync(new Uri(collector.Address), Ct);

        await send.Should().ThrowAsync<HttpRequestException>("no CA of the image's issued the collector's certificate");
        issuer.Requests.Should().BeEmpty("CWE-918: no request leaves for the address the certificate names");
        CollectorTrust.IsPinned(provider, CollectorTrust.LokiClient).Should().BeTrue();
        CollectorTrust.IsPinned(provider, "plain").Should().BeFalse();
        if (OperatingSystem.IsLinux())
        {
            // The control: the factory's default handler fetches that address, so the silence above is the pin's.
            using var plain = factory.CreateClient("plain");
            var fetch = () => plain.GetAsync(new Uri(collector.Address), Ct);
            await fetch.Should().ThrowAsync<HttpRequestException>();
            issuer.Requests.Should().NotBeEmpty("a client with the platform's default policy fetches the address the certificate names");
        }
    }

    [Fact]
    public async Task Loki_Should_DownloadNoCertificate_When_ItIsReachedOverHttpsWithoutACaFile()
    {
        await using var issuer = await IssuerSite.StartAsync(Ct);
        await using var collector = await TlsCollector.StartBehindAnIntermediateAsync("Ariva test public CA", issuer.Address, Ct);

        Ship(Configuration(("Loki:Enabled", "true"), ("Loki:Uri", collector.Address)));

        (await collector.ReceivedAsync("POST", Silence, Ct)).Should().BeFalse("no CA of the image's issued the collector's certificate");
        issuer.Requests.Should().BeEmpty("the Loki sink's client downloads no certificate (ARV-226)");
    }

    [Fact]
    public async Task OtlpLogs_Should_DownloadNoCertificate_When_TheCollectorIsReachedOverHttpsWithoutACaFile()
    {
        await using var issuer = await IssuerSite.StartAsync(Ct);
        await using var collector = await TlsCollector.StartBehindAnIntermediateAsync("Ariva test public CA", issuer.Address, Ct);

        Ship(Configuration(("Otlp:Enabled", "true"), ("Otlp:Endpoint", collector.Address), ("Otlp:Protocol", "http/protobuf")));

        (await collector.ReceivedAsync("POST", Silence, Ct)).Should().BeFalse("no CA of the image's issued the collector's certificate");
        issuer.Requests.Should().BeEmpty("the OTLP log sink's handler downloads no certificate (ARV-226)");
    }

    #endregion

    #region The OTLP exporters

    [Theory]
    [MemberData(nameof(TraceProtocols))]
    public async Task Traces_Should_DownloadNoCertificate_When_TheCollectorIsReachedOverHttpsWithoutACaFile(string protocol, string line)
    {
        // ARV-226: the exporters take Ariva's client with the image's public CAs, over gRPC and http/protobuf alike.
        await using var issuer = await IssuerSite.StartAsync(Ct);
        await using var collector = await TlsCollector.StartBehindAnIntermediateAsync("Ariva test public CA", issuer.Address, Ct);
        await using var provider = Telemetry(collector.Address, protocol, caFile: null);

        ExportTrace(provider);

        (await collector.ReceivedAsync(line, Silence, Ct)).Should().BeFalse("no CA of the image's issued the collector's certificate");
        issuer.Requests.Should().BeEmpty("the exporters' client downloads no certificate");
    }

    [Theory]
    [MemberData(nameof(TraceProtocols))]
    public async Task Traces_Should_ReachTheCollector_When_ItsCaFileNamesTheCaThatIssuedItsCertificate(string protocol, string line)
    {
        await using var collector = await TlsCollector.StartAsync("Ariva test site CA", Ct);
        await using var provider = Telemetry(collector.Address, protocol, TlsCollector.WritePem(collector.Authority, _folder));

        ExportTrace(provider);

        (await collector.ReceivedAsync(line, Patience, Ct)).Should().BeTrue($"the exporter trusts the site CA ({string.Join(", ", collector.Requests)})");
    }

    [Theory]
    [MemberData(nameof(TraceProtocols))]
    public async Task Traces_Should_NotReachTheCollector_When_ItsCaFileNamesAnotherCa(string protocol, string line)
    {
        await using var collector = await TlsCollector.StartAsync("Ariva test site CA", Ct);
        using var other = TlsCollector.NewAuthority("Ariva test other CA");
        await using var provider = Telemetry(collector.Address, protocol, TlsCollector.WritePem(other, _folder));

        ExportTrace(provider);

        (await collector.ReceivedAsync(line, Silence, Ct)).Should().BeFalse("a collector whose certificate another CA issued is never trusted");
        collector.Requests.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(TraceProtocols))]
    public async Task Traces_Should_NotReachTheCollector_When_ItsCertificateNamesAnotherHost(string protocol, string line)
    {
        await using var collector = await TlsCollector.StartAsync("Ariva test site CA", Ct, serverName: "collector.invalid");
        await using var provider = Telemetry(collector.Address, protocol, TlsCollector.WritePem(collector.Authority, _folder));

        ExportTrace(provider);

        (await collector.ReceivedAsync(line, Silence, Ct)).Should().BeFalse("the site CA issued it, but for another host: any certificate of the site's PKI could pose as the collector otherwise");
        collector.Requests.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(MetricProtocols))]
    public async Task Metrics_Should_ReachTheCollector_When_ItsCaFileNamesTheCaThatIssuedItsCertificate(string protocol, string line)
    {
        await using var collector = await TlsCollector.StartAsync("Ariva test site CA", Ct);
        await using var provider = Telemetry(collector.Address, protocol, TlsCollector.WritePem(collector.Authority, _folder));

        using (var meter = new Meter(Source))
            meter.CreateCounter<long>("ariva.probe.count").Add(1);
        provider.GetRequiredService<MeterProvider>().ForceFlush(10_000);

        (await collector.ReceivedAsync(line, Patience, Ct)).Should().BeTrue(string.Join(", ", collector.Requests));
    }

    [Theory]
    [MemberData(nameof(LogProtocols))]
    public async Task ExportedLogs_Should_ReachTheCollector_When_ItsCaFileNamesTheCaThatIssuedItsCertificate(string protocol, string line)
    {
        // The simulator exports its logs through OpenTelemetry's logging provider (ExportLogs), not through Serilog.
        await using var collector = await TlsCollector.StartAsync("Ariva test site CA", Ct);
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection([
            new("Otlp:Enabled", "true"), new("Otlp:Endpoint", collector.Address), new("Otlp:Protocol", protocol),
            new(CollectorTrust.OtlpSetting, TlsCollector.WritePem(collector.Authority, _folder))
        ]);
        builder.AddArivaServiceDefaults(options =>
        {
            options.ServiceName = "ariva-probe";
            options.Environment = ArivaEnvironment.VmLocal;
            options.ExportLogs = true;
        });
        using (var host = builder.Build())
            host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Ariva.Probe").LogWarning("Probe line for a collector the site CA signed");

        (await collector.ReceivedAsync(line, Patience, Ct)).Should().BeTrue(string.Join(", ", collector.Requests));
    }

    [Fact]
    public async Task Traces_Should_NotReachTheCollector_When_NoCaFileIsSet()
    {
        // The baseline: the image's public roots do not hold the site CA, which is why ARV-200 exists.
        await using var collector = await TlsCollector.StartAsync("Ariva test site CA", Ct);
        await using var provider = Telemetry(collector.Address, "http/protobuf", caFile: null);

        ExportTrace(provider);

        (await collector.ReceivedAsync("POST /v1/traces", Silence, Ct)).Should().BeFalse();
    }

    [Theory]
    [InlineData("Otlp:Endpoint", "http://localhost:4318")]
    [InlineData("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4318")]
    public void AddArivaTelemetry_Should_StopTheHost_When_ACaFileIsSetAndAnEndpointIsHttp(string setting, string endpoint)
    {
        using var authority = TlsCollector.NewAuthority("Ariva test site CA");
        // The other address is https, so the http one is what stops the host.
        var other = setting == "Otlp:Endpoint" ? "OTEL_EXPORTER_OTLP_ENDPOINT" : "Otlp:Endpoint";
        var configuration = Configuration(("Otlp:Enabled", "true"), (other, "https://localhost:4318"), (setting, endpoint), ("Otlp:Protocol", "http/protobuf"),
            (CollectorTrust.OtlpSetting, TlsCollector.WritePem(authority, _folder)));

        var add = () => new ServiceCollection().AddArivaTelemetry(configuration, Options());

        add.Should().Throw<InvalidOperationException>().WithMessage($"*{CollectorTrust.OtlpSetting}*https*");
    }

    [Theory]
    [InlineData("OTEL_EXPORTER_OTLP_CERTIFICATE")]
    [InlineData("OTEL_EXPORTER_OTLP_CLIENT_CERTIFICATE")]
    [InlineData("OTEL_EXPORTER_OTLP_CLIENT_KEY")]
    [InlineData("OTEL_EXPORTER_OTLP_TRACES_CERTIFICATE")]
    [InlineData("OTEL_EXPORTER_OTLP_LOGS_CLIENT_KEY")]
    public void AddArivaTelemetry_Should_StopTheHost_When_OpenTelemetrysOwnCertificateVariableIsSetOutsideVmLocal(string variable)
    {
        // Ariva's pin replaces the exporters' own client and Serilog's sink never reads these, so a CA given there would reach the
        // traces and metrics but not the logs: refused whether OTLP is on or not, naming the variable and never its value.
        var configuration = Configuration((variable, "/app/secrets/site/ca.crt"));
        var options = Options();
        options.Environment = ArivaEnvironment.K8sPrd;

        var add = () => new ServiceCollection().AddArivaTelemetry(configuration, options);
        var local = () => CollectorTrust.EnsureNoLibraryCertificates(configuration, ArivaEnvironment.VmLocal);

        add.Should().Throw<InvalidOperationException>().WithMessage($"{variable} is not read: give the OTLP collector's CA as {CollectorTrust.OtlpSetting}*")
            .Which.Message.Should().NotContain("/app/secrets", "the message names the variable, never its value");
        local.Should().NotThrow("vm-local is exempt, as for the OTLP headers");
    }

    #endregion

    #region The log sinks

    [Fact]
    public async Task Loki_Should_GetTheLogs_When_ItsCaFileNamesTheCaThatIssuedItsCertificate()
    {
        await using var collector = await TlsCollector.StartAsync("Ariva test site CA", Ct);
        var pem = TlsCollector.WritePem(collector.Authority, _folder);

        Ship(Configuration(("Loki:Enabled", "true"), ("Loki:Uri", collector.Address), (CollectorTrust.LokiSetting, pem)));

        (await collector.ReceivedAsync("POST /loki/api/v1/push", Patience, Ct)).Should().BeTrue(string.Join(", ", collector.Requests));
    }

    [Fact]
    public async Task Loki_Should_NotGetTheLogs_When_ItsCaFileNamesAnotherCa()
    {
        await using var collector = await TlsCollector.StartAsync("Ariva test site CA", Ct);
        using var other = TlsCollector.NewAuthority("Ariva test other CA");

        Ship(Configuration(("Loki:Enabled", "true"), ("Loki:Uri", collector.Address), (CollectorTrust.LokiSetting, TlsCollector.WritePem(other, _folder))));

        (await collector.ReceivedAsync("POST", Silence, Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Loki_Should_NotGetTheLogs_When_ItsCertificateNamesAnotherHost()
    {
        await using var collector = await TlsCollector.StartAsync("Ariva test site CA", Ct, serverName: "collector.invalid");

        Ship(Configuration(("Loki:Enabled", "true"), ("Loki:Uri", collector.Address), (CollectorTrust.LokiSetting, TlsCollector.WritePem(collector.Authority, _folder))));

        (await collector.ReceivedAsync("POST", Silence, Ct)).Should().BeFalse("the site CA issued it, but for another host");
        collector.Requests.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(LogProtocols))]
    public async Task OtlpLogs_Should_NotReachTheCollector_When_ItsCertificateNamesAnotherHost(string protocol, string line)
    {
        await using var collector = await TlsCollector.StartAsync("Ariva test site CA", Ct, serverName: "collector.invalid");

        Ship(Configuration(("Otlp:Enabled", "true"), ("Otlp:Endpoint", collector.Address), ("Otlp:Protocol", protocol),
            (CollectorTrust.OtlpSetting, TlsCollector.WritePem(collector.Authority, _folder))));

        (await collector.ReceivedAsync(line, Silence, Ct)).Should().BeFalse("the site CA issued it, but for another host");
        collector.Requests.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(LogProtocols))]
    public async Task OtlpLogs_Should_ReachTheCollector_When_ItsCaFileNamesTheCaThatIssuedItsCertificate(string protocol, string line)
    {
        await using var collector = await TlsCollector.StartAsync("Ariva test site CA", Ct);
        var pem = TlsCollector.WritePem(collector.Authority, _folder);

        Ship(Configuration(("Otlp:Enabled", "true"), ("Otlp:Endpoint", collector.Address), ("Otlp:Protocol", protocol), (CollectorTrust.OtlpSetting, pem)));

        (await collector.ReceivedAsync(line, Patience, Ct)).Should().BeTrue(string.Join(", ", collector.Requests));
    }

    [Theory]
    [MemberData(nameof(LogProtocols))]
    public async Task OtlpLogs_Should_NotReachTheCollector_When_ItsCaFileNamesAnotherCa(string protocol, string line)
    {
        await using var collector = await TlsCollector.StartAsync("Ariva test site CA", Ct);
        using var other = TlsCollector.NewAuthority("Ariva test other CA");

        Ship(Configuration(("Otlp:Enabled", "true"), ("Otlp:Endpoint", collector.Address), ("Otlp:Protocol", protocol),
            (CollectorTrust.OtlpSetting, TlsCollector.WritePem(other, _folder))));

        (await collector.ReceivedAsync(line, Silence, Ct)).Should().BeFalse();
    }

    [Fact]
    public void Configure_Should_StopTheHost_When_LokisCaFileIsSetAndLokiIsHttp()
    {
        using var authority = TlsCollector.NewAuthority("Ariva test site CA");
        var configuration = Configuration(("Loki:Enabled", "true"), ("Loki:Uri", "http://localhost:3100"), (CollectorTrust.LokiSetting, TlsCollector.WritePem(authority, _folder)));

        var ship = () => Ship(configuration);

        ship.Should().Throw<InvalidOperationException>().WithMessage($"*{CollectorTrust.LokiSetting}*Loki:Uri must be https*");
    }

    [Fact]
    public void Configure_Should_StopTheHost_When_ACaFileIsSetAndTheLoggerHasNoClientFactory()
    {
        using var authority = TlsCollector.NewAuthority("Ariva test site CA");
        var configuration = Configuration(("Loki:Enabled", "true"), ("Loki:Uri", "https://localhost:3100"), (CollectorTrust.LokiSetting, TlsCollector.WritePem(authority, _folder)));

        var configure = () => ArivaLogging.Configure(new LoggerConfiguration(), configuration, ArivaEnvironment.VmLocal);

        configure.Should().Throw<InvalidOperationException>().WithMessage("*AddCollectorClients*");
    }

    [Theory]
    [InlineData("Loki")]
    [InlineData("OTLP")]
    public async Task Configure_Should_StopTheHost_When_ACollectorIsReachedOverHttpsWithoutACaFileAndTheFactoryHoldsNoPinnedClient(string collector)
    {
        // ARV-226: without a CA file too, a sink never makes its own client, whose chain would download what the collector left out.
        var configuration = collector == "Loki"
            ? Configuration(("Loki:Enabled", "true"), ("Loki:Uri", "https://localhost:3100"))
            : Configuration(("Otlp:Enabled", "true"), ("Otlp:Endpoint", "https://localhost:4317"));
        var services = new ServiceCollection();
        services.AddHttpClient();
        await using var provider = services.BuildServiceProvider();

        var unpinned = () => ArivaLogging.Configure(new LoggerConfiguration(), configuration, ArivaEnvironment.VmLocal, null, provider);
        var bare = () => ArivaLogging.Configure(new LoggerConfiguration(), configuration, ArivaEnvironment.VmLocal);

        unpinned.Should().Throw<InvalidOperationException>().WithMessage("*reached over https*pinned collector clients (AddCollectorClients)*");
        bare.Should().Throw<InvalidOperationException>().WithMessage("*AddCollectorClients*");
        using var host = HostLogger.Create(configuration, ArivaEnvironment.VmLocal);
        host.Logger.Should().NotBeNull("the host registers the pinned clients before it builds the logger");
    }

    [Theory]
    [InlineData("Loki")]
    [InlineData("OTLP")]
    public async Task Configure_Should_StopTheHost_When_ACaFileIsSetAndTheFactoryHoldsNoPinnedClient(string collector)
    {
        // The host's factory hands out a client of any name, with the public roots: the logger takes its clients from CollectorClients only.
        using var authority = TlsCollector.NewAuthority("Ariva test site CA");
        var pem = TlsCollector.WritePem(authority, _folder);
        var configuration = collector == "Loki"
            ? Configuration(("Loki:Enabled", "true"), ("Loki:Uri", "https://localhost:3100"), (CollectorTrust.LokiSetting, pem))
            : Configuration(("Otlp:Enabled", "true"), ("Otlp:Endpoint", "https://localhost:4317"), (CollectorTrust.OtlpSetting, pem));
        var services = new ServiceCollection();
        services.AddHttpClient();
        await using var provider = services.BuildServiceProvider();

        var configure = () => ArivaLogging.Configure(new LoggerConfiguration(), configuration, ArivaEnvironment.VmLocal, null, provider);

        configure.Should().Throw<InvalidOperationException>().WithMessage("*pinned collector clients (AddCollectorClients)*");
    }

    [Fact]
    public void AddCollectorClients_Should_StopTheHost_When_TheCaFileIsMissing()
    {
        var configuration = Configuration((CollectorTrust.LokiSetting, Path.Combine(_folder.FullName, "absent.crt")));

        var add = () => ArivaLogging.AddCollectorClients(new ServiceCollection(), configuration);

        add.Should().Throw<InvalidOperationException>().WithMessage($"*{CollectorTrust.LokiSetting}*does not exist*");
    }

    #endregion

    #region The hosts

    [Theory]
    [MemberData(nameof(ArivaHosts.BackplaneHosts), MemberType = typeof(ArivaHosts))]
    public async Task Host_Should_ShipItsLinesToLoki_When_ProductionNamesTheSiteCaOfLokisCertificate(string host)
    {
        // V16.4.3 through the host's own wiring (AddAppLogging): a k8s-prd host whose Loki chains to the site CA starts, and its lines
        // arrive, the last one too: it is written just before the host is disposed, so only the final flush can carry it. That flush
        // may end after the factory's dispose returns: the host's entry point is in RunAsync too and disposes the same services when
        // the host stops, and whichever disposes first does the work while the other returns at once (dependency injection disposes
        // a provider once). So the last line is waited for; it never arrives if the flush fails, since the logger takes no batch after.
        await using var collector = await TlsCollector.StartAsync("Ariva test site CA", Ct);
        var pem = TlsCollector.WritePem(collector.Authority, _folder);
        var probe = $"Last line before the host stops {Guid.NewGuid():N}";

        await using (var app = ArivaHosts.Create(host, ArivaEnvironment.K8sPrd, builder => builder
            .UseSetting(ArivaLogging.LokiUriSetting, collector.Address)
            .UseSetting(CollectorTrust.LokiSetting, pem)))
        {
            app.CreateClient().Dispose();
            app.Services.GetRequiredService<CollectorClients>().IsPinned(CollectorTrust.LokiClient).Should().BeTrue();
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Ariva.Probe").LogWarning("{Probe}", probe);
        }

        (await collector.ReceivedAsync("POST /loki/api/v1/push", Patience, Ct)).Should().BeTrue(string.Join(", ", collector.Requests));
        (await collector.ReceivedTextAsync(probe, Patience, Ct)).Should().BeTrue("the line written just before the host stops goes out with the final flush");
    }

    [Theory]
    [MemberData(nameof(ArivaHosts.BackplaneHosts), MemberType = typeof(ArivaHosts))]
    public async Task Host_Should_ShipItsLinesToTheOtlpCollector_When_ProductionNamesTheSiteCaOfItsCertificate(string host)
    {
        // As the Loki case above, the last line waited for. The collector answers gRPC without a gRPC status, so the sink counts
        // every export as failed and keeps its batch: the final flush sends it again with the last line.
        await using var collector = await TlsCollector.StartAsync("Ariva test site CA", Ct);
        var pem = TlsCollector.WritePem(collector.Authority, _folder);
        var probe = $"Last line before the host stops {Guid.NewGuid():N}";

        await using (var app = ArivaHosts.Create(host, ArivaEnvironment.K8sPrd, builder => builder
            .UseSetting(ArivaLogging.LokiEnabledSetting, "false")
            .UseSetting("OTEL_EXPORTER_OTLP_ENDPOINT", collector.Address)
            .UseSetting(CollectorTrust.OtlpSetting, pem)))
        {
            app.CreateClient().Dispose();
            app.Services.GetRequiredService<CollectorClients>().IsPinned(CollectorTrust.OtlpLogsClient).Should().BeTrue();
            CollectorTrust.IsPinned(app.Services, CollectorTrust.OtlpExporterClient).Should().BeTrue();
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Ariva.Probe").LogWarning("{Probe}", probe);
        }

        (await collector.ReceivedAsync("POST /opentelemetry.proto.collector.logs.v1.LogsService/Export", Patience, Ct))
            .Should().BeTrue(string.Join(", ", collector.Requests));
        (await collector.ReceivedTextAsync(probe, Patience, Ct)).Should().BeTrue("the line written just before the host stops goes out with the final flush");
    }

    [Fact]
    public void CollectorClients_Should_RefuseAnyClientButAPinnedOne_When_AskedForAnotherName()
    {
        // A factory hands out a client of any name, unpinned: CollectorClients hands out its pinned ones only.
        using var authority = TlsCollector.NewAuthority("Ariva test site CA");
        using var clients = CollectorClients.From(Configuration((CollectorTrust.LokiSetting, TlsCollector.WritePem(authority, _folder))));

        var unpinnedClient = () => clients.CreateClient(CollectorTrust.OtlpLogsClient);
        var unpinnedHandler = () => clients.CreateHandler("an outbound integration");
        using var pinned = clients.CreateClient(CollectorTrust.LokiClient);

        unpinnedClient.Should().Throw<InvalidOperationException>().WithMessage("*is pinned*");
        unpinnedHandler.Should().Throw<InvalidOperationException>().WithMessage("*is pinned*");
        pinned.Should().NotBeNull();
    }

    #endregion

    #region Only the collectors

    [Fact]
    public async Task OtherClients_Should_KeepThePublicRoots_When_ACollectorIsPinned()
    {
        await using var collector = await TlsCollector.StartAsync("Ariva test site CA", Ct);
        var roots = new X509Certificate2Collection();
        roots.ImportFromPem(collector.Authority.ExportCertificatePem());
        var services = new ServiceCollection();
        CollectorTrust.Pin(services, roots, [CollectorTrust.LokiClient]);
        services.AddHttpClient("an outbound integration");
        await using var provider = services.BuildServiceProvider();
        var clients = provider.GetRequiredService<IHttpClientFactory>();

        using var pinned = clients.CreateClient(CollectorTrust.LokiClient);
        using var response = await pinned.GetAsync(new Uri(collector.Address), Ct);
        using var outbound = clients.CreateClient("an outbound integration");
        var unpinned = () => outbound.GetAsync(new Uri(collector.Address), Ct);

        response.IsSuccessStatusCode.Should().BeTrue("the collector's client trusts the site CA");
        await unpinned.Should().ThrowAsync<HttpRequestException>("every other client keeps the public roots and never trusts the site CA");
    }

    #endregion

    #region Helpers

    private static IConfiguration Configuration(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string>(s.Key, s.Value))).Build();

    private static ArivaServiceDefaultsOptions Options()
    {
        var options = new ArivaServiceDefaultsOptions { ServiceName = "ariva-probe", Environment = ArivaEnvironment.VmLocal };
        options.ActivitySources.Add(Source);
        options.Meters.Add(Source);
        return options;
    }

    /// <summary>A host's collector services with both CA files set: the log sinks' clients and the exporters'.</summary>
    private static ServiceProvider HostServices(string caFile)
    {
        var configuration = Configuration(("Otlp:Enabled", "true"), ("Otlp:Endpoint", "https://collector.example:4317"),
            (CollectorTrust.OtlpSetting, caFile), (CollectorTrust.LokiSetting, caFile));
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        ArivaLogging.AddCollectorClients(services, configuration);
        services.AddArivaTelemetry(configuration, Options());
        return services.BuildServiceProvider();
    }

    /// <summary>The handler a client sends through, past its delegating handlers (resilience and the like).</summary>
    private static SocketsHttpHandler PrimaryHandler(HttpMessageHandler handler)
    {
        while (handler is DelegatingHandler delegating)
            handler = delegating.InnerHandler;
        return handler.Should().BeOfType<SocketsHttpHandler>().Subject;
    }

    private static ServiceProvider Telemetry(string endpoint, string protocol, string caFile)
    {
        var settings = new List<(string, string)> { ("Otlp:Enabled", "true"), ("Otlp:Endpoint", endpoint), ("Otlp:Protocol", protocol) };
        if (caFile is not null)
            settings.Add((CollectorTrust.OtlpSetting, caFile));
        var configuration = Configuration([.. settings]);
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddArivaTelemetry(configuration, Options());
        return services.BuildServiceProvider();
    }

    private static void ExportTrace(ServiceProvider provider)
    {
        var tracing = provider.GetRequiredService<TracerProvider>();
        using (var source = new ActivitySource(Source))
        using (source.StartActivity("probe"))
        {
        }

        tracing.ForceFlush(10_000);
    }

    /// <summary>Builds the host's logger as a host does (the collector clients first), writes a line and disposes it, which ships it.</summary>
    private static void Ship(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        ArivaLogging.AddCollectorClients(services, configuration);
        using var provider = services.BuildServiceProvider();
        using var logger = ArivaLogging.Configure(new LoggerConfiguration(), configuration, ArivaEnvironment.VmLocal, null, provider).CreateLogger();
        logger.Information("Probe line for a collector the site CA signed");
    }

    #endregion
}
