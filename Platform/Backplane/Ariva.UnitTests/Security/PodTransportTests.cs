using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Ariva.Api.Common.Extensions;
using Ariva.Api.Common.Hosting;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-082b (CWE-319, CWE-295; ASVS V12.3.1, V12.3.3): in k8s-prd every pod serves https only, with the certificate the cluster CA
/// issued for it, and the ingress verifies it. A k8s-prd host refuses to start when its configuration would serve plain HTTP, has
/// no certificate files or allows TLS below 1.2 (every Backplane host; vm-local, k8s-dev and k8s-demo are never refused). A real
/// Kestrel with the shipped k8s-prd section answers a client pinned to the cluster CA over TLS 1.2 and 1.3 under the name the
/// certificate holds, and nothing else: no plain listener although the image's ASPNETCORE_HTTP_PORTS is set, no answer in clear, no
/// trust from another CA. On Linux TLS 1.2 offers ECDHE with AEAD only (no CBC, no static RSA). A renewed certificate, swapped in
/// as the kubelet updates a secret volume, is served without a restart.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class PodTransportTests : IDisposable
{
    #region Fields

    private const string PodName = "api-main-service";
    private const string ProductionFile = "appsettings.base.k8s-prd.json";

    private readonly string _folder = Directory.CreateTempSubdirectory("ariva-pod-tls-").FullName;
    private readonly X509Certificate2 _ca = Authority("CN=Ariva cluster CA");
    private readonly string _certificatePath;
    private readonly string _keyPath;

    #endregion

    #region Constructors

    public PodTransportTests()
    {
        var folder = Path.Combine(_folder, "pod-tls");
        Issue(_ca, folder);
        _certificatePath = Path.Combine(folder, "tls.crt");
        _keyPath = Path.Combine(folder, "tls.key");
    }

    public void Dispose()
    {
        _ca.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    #endregion

    #region Properties

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    #endregion

    #region The guard

    public static TheoryData<string, string> Refusals => new()
    {
        { "no endpoint", PodTransportExtensions.NoEndpoint },
        { "a plain endpoint", PodTransportExtensions.PlainEndpoint },
        { "a second, plain endpoint", PodTransportExtensions.PlainEndpoint },
        { "an endpoint without a url", PodTransportExtensions.PlainEndpoint },
        { "no certificate path", PodTransportExtensions.NoCertificate },
        { "no key path", PodTransportExtensions.NoCertificate },
        { "no certificate file", PodTransportExtensions.MissingCertificate },
        { "no key file", PodTransportExtensions.MissingCertificate },
        { "TLS 1.1 beside TLS 1.2", PodTransportExtensions.WeakProtocols },
        { "TLS 1.0", PodTransportExtensions.WeakProtocols },
        { "SSL 3", PodTransportExtensions.WeakProtocols },
        { "None", PodTransportExtensions.WeakProtocols },
        { "Default", PodTransportExtensions.WeakProtocols },
        { "an unknown name", PodTransportExtensions.WeakProtocols },
        { "a single value Kestrel skips", PodTransportExtensions.WeakProtocols },
        { "TLS 1.0 by number", PodTransportExtensions.WeakProtocols },
        { "TLS 1.1 in the endpoint defaults", PodTransportExtensions.WeakProtocols },
        { "TLS 1.1 for an SNI name", PodTransportExtensions.WeakProtocols },
        { "a plain ASPNETCORE_URLS", PodTransportExtensions.PlainUrls },
        { "a plain address among ASPNETCORE_URLS", PodTransportExtensions.PlainUrls },
        { "preferHostingUrls", PodTransportExtensions.PreferredHostingUrls },
        { "preferHostingUrls as 1", PodTransportExtensions.PreferredHostingUrls }
    };

    public static TheoryData<string> Acceptances => new()
    {
        "as shipped",
        "TLS 1.3 only",
        "names in any case",
        "both versions in one entry",
        "protocols left to the endpoint defaults",
        "an https ASPNETCORE_URLS",
        "preferHostingUrls false",
        "two https endpoints"
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void AddAppPodTransport_Should_RefuseToStart_When_ProductionWouldServePlainOrWeakTls(string change, string message)
    {
        var configuration = Configuration(Production(change));

        var add = () => new ServiceCollection().AddAppPodTransport(configuration, ArivaEnvironment.K8sPrd);

        add.Should().Throw<InvalidOperationException>().Which.Message.Should().Be(message, "a constant names the problem, never a path or a key ({0})", change);
    }

    [Theory]
    [MemberData(nameof(Acceptances))]
    public void AddAppPodTransport_Should_Accept_When_ProductionServesHttpsWithTheMountedCertificate(string change)
    {
        var configuration = Configuration(Production(change));

        var add = () => new ServiceCollection().AddAppPodTransport(configuration, ArivaEnvironment.K8sPrd);

        add.Should().NotThrow();
        PodTransportExtensions.ProductionTransportProblem(configuration, File.Exists).Should().BeNull();
    }

    [Fact]
    public void AddAppPodTransport_Should_Refuse_When_OnlyTheSettingsSayProduction()
    {
        // ARV-098 refuses the mismatch at startup; this guard still judges the settings as production on their own.
        var configuration = Configuration(Production("a plain endpoint"));

        var add = () => new ServiceCollection().AddAppPodTransport(configuration, ArivaEnvironment.K8sDev);

        add.Should().Throw<InvalidOperationException>().WithMessage(PodTransportExtensions.PlainEndpoint);
    }

    [Theory]
    [InlineData(ArivaEnvironment.VmLocal)]
    [InlineData(ArivaEnvironment.K8sDev)]
    [InlineData(ArivaEnvironment.K8sDemo)]
    public void AddAppPodTransport_Should_ChangeNothing_When_NotInProduction(string environment)
    {
        // Everything the guard refuses at once: these environments keep serving plain HTTP on 8080 as before.
        var configuration = Configuration(new Dictionary<string, string>
        {
            ["Application:Environment"] = environment,
            ["Kestrel:Endpoints:Http:Url"] = "http://*:8080",
            ["Kestrel:Endpoints:Http:SslProtocols:0"] = "Tls11",
            ["urls"] = "http://+:8080",
            ["preferHostingUrls"] = "true"
        });
        var services = new ServiceCollection();

        var add = () => services.AddAppPodTransport(configuration, environment);

        add.Should().NotThrow();
        services.Should().BeEmpty("no Kestrel option and no cipher suite policy is registered outside k8s-prd");
    }

    [Fact]
    public void AddAppPodTransport_Should_RestrictTheHandshakesCipherSuites_When_ProductionRunsOnLinux()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "CipherSuitesPolicy is a Linux feature of SslStream; the images run on Linux.");
        using var provider = new ServiceCollection().AddAppPodTransport(Configuration(Production("as shipped")), ArivaEnvironment.K8sPrd).BuildServiceProvider();

        var kestrel = new KestrelServerOptions();
        foreach (var configure in provider.GetServices<IConfigureOptions<KestrelServerOptions>>())
            configure.Configure(kestrel);

        AllowedSuites(kestrel).Should().Equal(PodTransportExtensions.CipherSuites);
    }

    #endregion

    #region The settings files

    [Fact]
    public void ProductionSettings_Should_ServeHttpsFromThePodSecret_When_TheBaseFileIsRead()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.base.json"))
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, ProductionFile))
            .Build();

        var endpoint = configuration.GetSection("Kestrel:Endpoints").GetChildren().Should().ContainSingle().Subject;
        endpoint.Key.Should().Be("Https");
        endpoint["Url"].Should().Be("https://*:8443", "every interface, IPv4 and IPv6, as the image's HTTP_PORTS did on 8080");
        endpoint["Certificate:Path"].Should().Be("/app/secrets/pod-tls/tls.crt", "the chart mounts the pod TLS secret there");
        endpoint["Certificate:KeyPath"].Should().Be("/app/secrets/pod-tls/tls.key");
        endpoint.GetSection("SslProtocols").GetChildren().Select(p => p.Value).Should().Equal("Tls12", "Tls13");
        configuration.GetSection("Kestrel:EndpointDefaults").Exists().Should().BeFalse();
        PodTransportExtensions.ProductionTransportProblem(configuration, path => path.StartsWith("/app/secrets/pod-tls/", StringComparison.Ordinal))
            .Should().BeNull("the shipped settings pass the guard once the secret is mounted");
        PodTransportExtensions.ProductionTransportProblem(configuration, _ => false).Should().Be(PodTransportExtensions.MissingCertificate);
    }

    [Theory]
    [InlineData("appsettings.base.json")]
    [InlineData("appsettings.base.vm-local.json")]
    [InlineData("appsettings.base.k8s-dev.json")]
    [InlineData("appsettings.base.k8s-demo.json")]
    public void OtherSettings_Should_DeclareNoKestrelEndpoint_When_Read(string file)
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, file)).Build();

        configuration.GetSection("Kestrel").Exists().Should().BeFalse("only k8s-prd serves https from the pod; elsewhere the hosts stay as they are");
    }

    [Fact]
    public void ProductionSettings_Should_NameEveryServiceOverHttps_When_TheBaseFileIsRead()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.base.json"))
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, ProductionFile))
            .Build();

        var addresses = configuration.GetSection("Microservices").GetChildren()
            .SelectMany(service => new[] { service["Internal"], service["External"] }.Select(address => (service.Key, Address: address)))
            .ToList();

        addresses.Should().HaveCount(10, "Main, Ingest, Stream, Cronz and Integration, each internal and external");
        addresses.Should().OnlyContain(a => Uri.IsWellFormedUriString(a.Address, UriKind.Absolute) && new Uri(a.Address).Scheme == Uri.UriSchemeHttps,
            "every pod serves https in k8s-prd, so a call between hosts is made over TLS too");
    }

    #endregion

    #region Every host

    [Theory]
    [MemberData(nameof(ArivaHosts.BackplaneHosts), MemberType = typeof(ArivaHosts))]
    public async Task Host_Should_RefuseToStart_When_ProductionServesPlainHttp(string host)
    {
        await using var app = ArivaHosts.Create(host, ArivaEnvironment.K8sPrd, builder => builder.UseSetting("Kestrel:Endpoints:Https:Url", "http://*:8080"));

        var start = () => app.Services;

        start.Should().Throw<InvalidOperationException>().WithMessage(PodTransportExtensions.PlainEndpoint);
    }

    [Theory]
    [MemberData(nameof(ArivaHosts.BackplaneHosts), MemberType = typeof(ArivaHosts))]
    public async Task Host_Should_OfferOnlyTheAeadSuites_When_ProductionRunsOnLinux(string host)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "CipherSuitesPolicy is a Linux feature of SslStream; the images run on Linux.");
        await using var production = ArivaHosts.Create(host, ArivaEnvironment.K8sPrd);
        await using var local = ArivaHosts.Create(host);

        var offered = AllowedSuites(production.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value);
        var elsewhere = AllowedSuites(local.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value);

        offered.Should().Equal(PodTransportExtensions.CipherSuites, "the host's https endpoints take Kestrel's https defaults");
        elsewhere.Should().BeNull("vm-local keeps the platform's defaults");
    }

    #endregion

    #region A real Kestrel

    [Theory]
    [InlineData("Tls12")]
    [InlineData("Tls13")]
    public async Task PodEndpoint_Should_Answer_When_TheClientPinsTheClusterCaAndNamesThePod(string protocol)
    {
        var (https, plain) = (FreePort(), FreePort());
        await using var app = await StartAsync(ArivaEnvironment.K8sPrd, https, plain, _certificatePath, _keyPath);
        await using var clients = Clients(_ca, protocol);

        using var response = await GetAsync(clients, Uri.UriSchemeHttps, https);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "TLS {0} with the cluster CA alone and the name the certificate holds", protocol);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Be("pod");
    }

    [Fact]
    public async Task PodEndpoint_Should_BeRefused_When_TheClientTrustsAnotherCaOrNamesAnotherHost()
    {
        var (https, plain) = (FreePort(), FreePort());
        await using var app = await StartAsync(ArivaEnvironment.K8sPrd, https, plain, _certificatePath, _keyPath);
        using var stranger = Authority("CN=Another CA");
        await using var strangers = Clients(stranger);
        await using var pinned = Clients(_ca);

        var otherCa = async () => await GetAsync(strangers, Uri.UriSchemeHttps, https);
        var otherName = async () => await GetAsync(pinned, Uri.UriSchemeHttps, https, host: "api-ingest-service");

        (await otherCa.Should().ThrowAsync<HttpRequestException>()).WithInnerException<AuthenticationException>();
        (await otherName.Should().ThrowAsync<HttpRequestException>()).WithInnerException<AuthenticationException>();
    }

    [Fact]
    public async Task PodEndpoint_Should_NotAnswerInClear_When_ItsPortIsAskedOverHttp()
    {
        var (https, plain) = (FreePort(), FreePort());
        await using var app = await StartAsync(ArivaEnvironment.K8sPrd, https, plain, _certificatePath, _keyPath);
        await using var clients = Clients(_ca);

        var clear = async () => await GetAsync(clients, Uri.UriSchemeHttp, https);

        await clear.Should().ThrowAsync<HttpRequestException>("the port speaks TLS only and closes a request sent in clear");
    }

    [Fact]
    public async Task PodEndpoint_Should_LeaveNoPlainListener_When_HttpPortsIsSet()
    {
        // As the image's ASPNETCORE_HTTP_PORTS=8080, on a free port: the configured endpoint takes the place of the port.
        var (https, plain) = (FreePort(), FreePort());
        await using var app = await StartAsync(ArivaEnvironment.K8sPrd, https, plain, _certificatePath, _keyPath);

        app.Urls.Should().Equal($"https://127.0.0.1:{https}");
        (await Connect(plain)).Should().Be(SocketError.ConnectionRefused, "nothing listens in plain");
    }

    [Fact]
    public async Task PlainPort_Should_Listen_When_NoEndpointIsDeclaredOutsideProduction()
    {
        // The control for the test above: the same HTTP_PORTS setting does bind in k8s-dev, which declares no endpoint.
        var plain = FreePort();
        await using var app = await StartAsync(ArivaEnvironment.K8sDev, https: 0, plain, certificate: null, key: null);
        await using var clients = Clients(_ca);

        using var response = await GetAsync(clients, Uri.UriSchemeHttp, plain);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "k8s-dev keeps serving plain HTTP as before");
    }

    public static TheoryData<string, string, bool> Suites => new()
    {
        { "Rsa", "TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256", true },
        { "Rsa", "TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384", true },
        { "Rsa", "TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256", true },
        { "Rsa", "TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA256", false },
        { "Rsa", "TLS_ECDHE_RSA_WITH_AES_256_CBC_SHA384", false },
        { "Rsa", "TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA", false },
        { "Rsa", "TLS_RSA_WITH_AES_128_GCM_SHA256", false },
        { "Rsa", "TLS_RSA_WITH_AES_256_CBC_SHA256", false },
        { "Rsa", "TLS_DHE_RSA_WITH_AES_128_GCM_SHA256", false },
        { "Ecdsa", "TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256", true },
        { "Ecdsa", "TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384", true },
        { "Ecdsa", "TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256", true },
        { "Ecdsa", "TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA256", false },
        { "Ecdsa", "TLS_ECDHE_ECDSA_WITH_AES_256_CBC_SHA", false }
    };

    [Theory]
    [MemberData(nameof(Suites))]
    [SupportedOSPlatform("linux")]
    public async Task PodEndpoint_Should_OfferEcdheWithAeadOnly_When_TheClientSpeaksTls12(string key, string suite, bool accepted)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "CipherSuitesPolicy is a Linux feature of SslStream; the images run on Linux.");
        var folder = Path.Combine(_folder, key + "-" + suite);
        Issue(_ca, folder, rsa: key == "Rsa");
        var (https, plain) = (FreePort(), FreePort());
        await using var app = await StartAsync(ArivaEnvironment.K8sPrd, https, plain, Path.Combine(folder, "tls.crt"), Path.Combine(folder, "tls.key"));

        var handshake = async () => await HandshakeAsync(https, "Tls12", Enum.Parse<TlsCipherSuite>(suite));

        if (accepted)
            (await handshake()).Should().Be(("Tls12", suite));
        else
            await handshake.Should().ThrowAsync<AuthenticationException>("{0} is not ECDHE with an AEAD cipher", suite);
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task PodEndpoint_Should_RefuseCbcBecauseOfItsPolicy_When_ComparedWithAHostWithoutIt()
    {
        // The control for the theory above: the platform's defaults accept this CBC suite, so the refusal is the policy's doing.
        Assert.SkipUnless(OperatingSystem.IsLinux(), "CipherSuitesPolicy is a Linux feature of SslStream; the images run on Linux.");
        var folder = Path.Combine(_folder, "rsa-control");
        Issue(_ca, folder, rsa: true);
        var (production, development) = (FreePort(), FreePort());
        await using var withPolicy = await StartAsync(ArivaEnvironment.K8sPrd, production, FreePort(), Path.Combine(folder, "tls.crt"), Path.Combine(folder, "tls.key"));
        await using var without = await StartAsync(ArivaEnvironment.K8sDev, development, FreePort(), Path.Combine(folder, "tls.crt"), Path.Combine(folder, "tls.key"));

        var refused = async () => await HandshakeAsync(production, "Tls12", TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA256);
        var accepted = await HandshakeAsync(development, "Tls12", TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA256);

        accepted.Should().Be(("Tls12", "TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA256"));
        await refused.Should().ThrowAsync<AuthenticationException>();
    }

    [Fact]
    public async Task PodEndpoint_Should_ServeTheRenewedCertificateWithoutARestart_When_TheSecretVolumeIsUpdated()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The kubelet's secret volume layout uses symbolic links; the images run on Linux.");
        // The layout the kubelet writes for a secret volume: the files in a timestamped folder, ..data a link to it, and tls.crt
        // and tls.key links through ..data. An update writes a new folder and swaps ..data in one rename.
        var secret = Path.Combine(_folder, "volume");
        var first = Issue(_ca, Path.Combine(secret, "..2026_10_18_06_00_00.000000001"));
        File.CreateSymbolicLink(Path.Combine(secret, "..data"), "..2026_10_18_06_00_00.000000001");
        File.CreateSymbolicLink(Path.Combine(secret, "tls.crt"), Path.Combine("..data", "tls.crt"));
        File.CreateSymbolicLink(Path.Combine(secret, "tls.key"), Path.Combine("..data", "tls.key"));
        var (https, plain) = (FreePort(), FreePort());
        await using var app = await StartAsync(ArivaEnvironment.K8sPrd, https, plain, Path.Combine(secret, "tls.crt"), Path.Combine(secret, "tls.key"));
        (await ServedAsync(https)).Should().Be(first);

        var renewed = Issue(_ca, Path.Combine(secret, "..2026_10_18_07_00_00.000000002"));
        File.CreateSymbolicLink(Path.Combine(secret, "..data_tmp"), "..2026_10_18_07_00_00.000000002");
        // rename(2) over the old link, as the kubelet's atomic writer does: File.Replace checks with lstat and renames, where
        // File.Move would follow the link to its folder and refuse it.
        File.Replace(Path.Combine(secret, "..data_tmp"), Path.Combine(secret, "..data"), destinationBackupFileName: null);
        Directory.Delete(Path.Combine(secret, "..2026_10_18_06_00_00.000000001"), recursive: true);

        // Kestrel polls the certificate files (every four seconds, following the links to their targets) and rebinds the endpoint.
        var served = first;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (served != renewed && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250, Ct);
            served = await ServedOrNullAsync(https) ?? served;
        }

        served.Should().Be(renewed, "the same Kestrel serves the renewed certificate within seconds, without a restart");
        (await Connect(plain)).Should().Be(SocketError.ConnectionRefused, "the rebind brings back the https endpoint only");
    }

    #endregion

    #region Helpers

    private Dictionary<string, string> Production(string change)
    {
        var values = new Dictionary<string, string>
        {
            ["Application:Environment"] = ArivaEnvironment.K8sPrd,
            ["Kestrel:Endpoints:Https:Url"] = "https://*:8443",
            ["Kestrel:Endpoints:Https:SslProtocols:0"] = "Tls12",
            ["Kestrel:Endpoints:Https:SslProtocols:1"] = "Tls13",
            ["Kestrel:Endpoints:Https:Certificate:Path"] = _certificatePath,
            ["Kestrel:Endpoints:Https:Certificate:KeyPath"] = _keyPath,
            // The image's ASPNETCORE_HTTP_PORTS: always there, never a reason to refuse.
            ["http_ports"] = "8080"
        };
        var absent = Path.Combine(_folder, "absent.pem");
        (string Key, string Value)[] changes = change switch
        {
            "as shipped" => [],
            "no endpoint" => [.. values.Keys.Where(k => k.StartsWith("Kestrel:", StringComparison.Ordinal)).Select(k => (k, (string)null))],
            "a plain endpoint" => [("Kestrel:Endpoints:Https:Url", "http://*:8080")],
            "a second, plain endpoint" => [("Kestrel:Endpoints:Http:Url", "http://*:8080")],
            "an endpoint without a url" => [("Kestrel:Endpoints:Https:Url", null)],
            "no certificate path" => [("Kestrel:Endpoints:Https:Certificate:Path", null)],
            "no key path" => [("Kestrel:Endpoints:Https:Certificate:KeyPath", " ")],
            "no certificate file" => [("Kestrel:Endpoints:Https:Certificate:Path", absent)],
            "no key file" => [("Kestrel:Endpoints:Https:Certificate:KeyPath", absent)],
            "TLS 1.1 beside TLS 1.2" => [("Kestrel:Endpoints:Https:SslProtocols:1", "Tls11")],
            "TLS 1.0" => [("Kestrel:Endpoints:Https:SslProtocols:0", "Tls"), ("Kestrel:Endpoints:Https:SslProtocols:1", null)],
            "SSL 3" => [("Kestrel:Endpoints:Https:SslProtocols:1", "Ssl3")],
            "None" => [("Kestrel:Endpoints:Https:SslProtocols:0", "None"), ("Kestrel:Endpoints:Https:SslProtocols:1", null)],
            "Default" => [("Kestrel:Endpoints:Https:SslProtocols:1", "Default")],
            "an unknown name" => [("Kestrel:Endpoints:Https:SslProtocols:1", "Tls1.3")],
            "a single value Kestrel skips" =>
            [
                ("Kestrel:Endpoints:Https:SslProtocols:0", null), ("Kestrel:Endpoints:Https:SslProtocols:1", null),
                ("Kestrel:Endpoints:Https:SslProtocols", "Tls12")
            ],
            "TLS 1.0 by number" => [("Kestrel:Endpoints:Https:SslProtocols:1", "192")],
            "TLS 1.1 in the endpoint defaults" => [("Kestrel:EndpointDefaults:SslProtocols:0", "Tls11")],
            "TLS 1.1 for an SNI name" => [("Kestrel:Endpoints:Https:Sni:api-main-service:SslProtocols:0", "Tls11")],
            "a plain ASPNETCORE_URLS" => [("urls", "http://+:8080")],
            "a plain address among ASPNETCORE_URLS" => [("urls", "https://+:8443; HTTP://+:8080")],
            "preferHostingUrls" => [("preferHostingUrls", "True")],
            "preferHostingUrls as 1" => [("preferHostingUrls", "1")],
            "TLS 1.3 only" => [("Kestrel:Endpoints:Https:SslProtocols:0", "Tls13"), ("Kestrel:Endpoints:Https:SslProtocols:1", null)],
            "names in any case" => [("Kestrel:Endpoints:Https:SslProtocols:0", "tls12"), ("Kestrel:Endpoints:Https:SslProtocols:1", "TLS13")],
            "both versions in one entry" => [("Kestrel:Endpoints:Https:SslProtocols:0", "Tls12, Tls13"), ("Kestrel:Endpoints:Https:SslProtocols:1", null)],
            "protocols left to the endpoint defaults" => [("Kestrel:Endpoints:Https:SslProtocols:0", null), ("Kestrel:Endpoints:Https:SslProtocols:1", null)],
            "an https ASPNETCORE_URLS" => [("urls", "https://+:8443")],
            "preferHostingUrls false" => [("preferHostingUrls", "false")],
            "two https endpoints" =>
            [
                ("Kestrel:Endpoints:Probe:Url", "https://*:9443"), ("Kestrel:Endpoints:Probe:Certificate:Path", _certificatePath),
                ("Kestrel:Endpoints:Probe:Certificate:KeyPath", _keyPath)
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(change), change, "Unknown case.")
        };
        foreach (var (key, value) in changes)
        {
            if (value is null)
                values.Remove(key);
            else
                values[key] = value;
        }

        return values;
    }

    private static IConfiguration Configuration(Dictionary<string, string> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    /// <summary>
    /// A real Kestrel (not TestServer) built as the hosts build theirs, with the shipped k8s-prd settings file in production, the
    /// test's certificate on a loopback port in place of /app/secrets/pod-tls on 8443, and <paramref name="plain"/> as the image's
    /// ASPNETCORE_HTTP_PORTS. In k8s-dev it declares the https endpoint only when a certificate is given.
    /// </summary>
    private async Task<WebApplication> StartAsync(string environment, int https, int plain, string certificate, string key)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = environment, ContentRootPath = _folder });
        builder.Logging.ClearProviders();
        if (environment == ArivaEnvironment.K8sPrd)
            builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, ProductionFile), optional: false, reloadOnChange: false);
        var values = new Dictionary<string, string> { ["http_ports"] = plain.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (certificate is not null)
        {
            values["Kestrel:Endpoints:Https:Url"] = "https://127.0.0.1:" + https.ToString(System.Globalization.CultureInfo.InvariantCulture);
            values["Kestrel:Endpoints:Https:Certificate:Path"] = certificate;
            values["Kestrel:Endpoints:Https:Certificate:KeyPath"] = key;
            values["Kestrel:Endpoints:Https:SslProtocols:0"] = "Tls12";
            values["Kestrel:Endpoints:Https:SslProtocols:1"] = "Tls13";
        }

        builder.Configuration.AddInMemoryCollection(values);
        builder.Services.AddAppPodTransport(builder.Configuration, environment);
        var app = builder.Build();
        app.Run(context => context.Response.WriteAsync("pod", context.RequestAborted));
        await app.StartAsync(Ct);
        return app;
    }

    /// <summary>
    /// Clients through IHttpClientFactory that trust <paramref name="trusted"/> alone (CustomRootTrust, not the system store),
    /// optionally on one TLS version given by name.
    /// </summary>
    private static ServiceProvider Clients(X509Certificate2 trusted, string protocol = null)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(nameof(PodTransportTests)).UseSocketsHttpHandler((handler, _) =>
        {
            handler.SslOptions = Pinned(trusted, protocol);
            handler.AllowAutoRedirect = false;
        });
        return services.BuildServiceProvider();
    }

    private static SslClientAuthenticationOptions Pinned(X509Certificate2 trusted, string protocol, string host = null)
    {
        var chain = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
        chain.CustomTrustStore.Add(trusted);
        return new SslClientAuthenticationOptions
        {
            TargetHost = host,
            CertificateChainPolicy = chain,
            EnabledSslProtocols = protocol is null ? SslProtocols.None : Enum.Parse<SslProtocols>(protocol)
        };
    }

    /// <summary>A GET to the loopback port under the pod's name: the Host header is the name TLS sends and verifies.</summary>
    private static async Task<HttpResponseMessage> GetAsync(ServiceProvider clients, string scheme, int port, string host = PodName)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new UriBuilder(scheme, IPAddress.Loopback.ToString(), port).Uri);
        request.Headers.Host = host;
        using var client = clients.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(PodTransportTests));
        return await client.SendAsync(request, Ct);
    }

    /// <summary>A TLS handshake offering one version and one suite; returns what was negotiated.</summary>
    [SupportedOSPlatform("linux")]
    private async Task<(string Protocol, string Suite)> HandshakeAsync(int port, string protocol, TlsCipherSuite suite)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port, Ct);
        await using var tls = new SslStream(tcp.GetStream());
        var options = Pinned(_ca, protocol, PodName);
        options.CipherSuitesPolicy = new CipherSuitesPolicy([suite]);
        await tls.AuthenticateAsClientAsync(options, Ct);
        return (tls.SslProtocol.ToString(), tls.NegotiatedCipherSuite.ToString());
    }

    /// <summary>The SHA-256 hash of the certificate the endpoint serves, verified against the cluster CA under the pod's name.</summary>
    private async Task<string> ServedAsync(int port)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port, Ct);
        await using var tls = new SslStream(tcp.GetStream());
        await tls.AuthenticateAsClientAsync(Pinned(_ca, null, PodName), Ct);
        return tls.RemoteCertificate?.GetCertHashString(HashAlgorithmName.SHA256);
    }

    /// <summary>As <see cref="ServedAsync"/>, or null while the endpoint is between its stop and its new bind.</summary>
    private async Task<string> ServedOrNullAsync(int port)
    {
        try
        {
            return await ServedAsync(port);
        }
        catch (Exception e) when (e is SocketException or IOException or AuthenticationException)
        {
            return null;
        }
    }

    private static async Task<SocketError> Connect(int port)
    {
        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(IPAddress.Loopback, port, Ct);
            return SocketError.Success;
        }
        catch (SocketException e)
        {
            return e.SocketErrorCode;
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>The suites a handshake of Kestrel's https defaults allows (Kestrel keeps the defaults in a private member), or null.</summary>
    private static List<TlsCipherSuite> AllowedSuites(KestrelServerOptions kestrel)
    {
        var property = typeof(KestrelServerOptions).GetProperty("HttpsDefaults", BindingFlags.Instance | BindingFlags.NonPublic);
        property.Should().NotBeNull("Kestrel keeps the delegate ConfigureHttpsDefaults sets in this property");
        var https = new HttpsConnectionAdapterOptions();
        ((Action<HttpsConnectionAdapterOptions>)property.GetValue(kestrel)).Invoke(https);
        var handshake = new SslServerAuthenticationOptions();
        https.OnAuthenticate?.Invoke(null, handshake);
        return OperatingSystem.IsLinux() ? handshake.CipherSuitesPolicy?.AllowedCipherSuites.ToList() : null;
    }

    private static X509Certificate2 Authority(string name)
    {
        // Not disposed here: the certificate signs leaves with it.
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(name, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
    }

    /// <summary>
    /// Writes a pod certificate for <paramref name="name"/> from <paramref name="issuer"/> as tls.crt and tls.key (PEM, as
    /// cert-manager writes the secret) into <paramref name="folder"/>; returns its SHA-256 hash.
    /// </summary>
    private static string Issue(X509Certificate2 issuer, string folder, string name = PodName, bool rsa = false)
    {
        Directory.CreateDirectory(folder);
        using AsymmetricAlgorithm key = rsa ? RSA.Create(2048) : ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = key is RSA rsaKey
            ? new CertificateRequest("CN=" + name, rsaKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            : new CertificateRequest("CN=" + name, (ECDsa)key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(name);
        request.CertificateExtensions.Add(names.Build());
        // The CA's own key signs, whatever the leaf's key is (an RSA leaf under the ECDSA CA, as a cluster issuer may sign).
        using var issuerKey = issuer.GetECDsaPrivateKey();
        using var certificate = request.Create(issuer.SubjectName, X509SignatureGenerator.CreateForECDsa(issuerKey), DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(12), RandomNumberGenerator.GetBytes(16));
        File.WriteAllText(Path.Combine(folder, "tls.crt"), certificate.ExportCertificatePem());
        File.WriteAllText(Path.Combine(folder, "tls.key"), key.ExportPkcs8PrivateKeyPem());
        return certificate.GetCertHashString(HashAlgorithmName.SHA256);
    }

    #endregion
}
