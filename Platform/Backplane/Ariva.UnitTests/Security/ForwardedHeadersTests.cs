using System.Net;
using Ariva.Api.Common.Extensions;
using Ariva.Api.Common.Hosting;
using Ariva.Api.Common.Settings;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ForwardedHeadersExtensions = Ariva.Api.Common.Extensions.ForwardedHeadersExtensions;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-085 (ASVS V4.1.3, CWE-348): in k8s-prd a host trusts X-Forwarded-For and X-Forwarded-Proto from the ingress controller's
/// pod network only, which the chart passes from a Helm value; the shipped k8s-prd file lists no network. A k8s-prd host refuses
/// to start when nothing is trusted, when a network is 0.0.0.0/0 or ::/0, wider than an IPv4 /14 or an IPv6 /48, when an IPv6
/// network would trust IPv4 clients wider than a /14 or when an entry does not parse, with a constant message (every Backplane
/// host; vm-local, k8s-dev and k8s-demo are never refused). In the pipeline the forwarded client is taken from a connection inside
/// the configured network and ignored from one outside it, and the same network decides whether Ingest takes a forwarded client
/// certificate. The trust from a single proxy address is shown on Ingest by DeviceAuthenticationTests.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class ForwardedHeadersTests
{
    #region Fields

    private const string RemoteHeader = "X-Test-Remote";
    private const string PodNetwork = ArivaHosts.ProductionPodNetwork;
    private const string Client = "203.0.113.9";
    private const string NetworksKey = ForwardedHeadersSettings.SectionName + ":KnownNetworks";
    private const string ProxiesKey = ForwardedHeadersSettings.SectionName + ":KnownProxies";

    /// <summary>What appsettings.base.k8s-prd.json listed before ARV-085, and k8s-dev and k8s-demo still list.</summary>
    private static readonly string[] PrivateRanges = ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"];

    #endregion

    #region Properties

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    #endregion

    #region The guard

    public static TheoryData<string, string> Refusals => new()
    {
        { "nothing trusted", ForwardedHeadersExtensions.NoTrustedProxy },
        { "0.0.0.0/0", ForwardedHeadersExtensions.EveryAddress },
        { "::/0", ForwardedHeadersExtensions.EveryAddress },
        { "0.0.0.0/0 beside the pod network", ForwardedHeadersExtensions.EveryAddress },
        { "the private ranges", ForwardedHeadersExtensions.WideNetwork },
        { "10.0.0.0/8", ForwardedHeadersExtensions.WideNetwork },
        { "an IPv4 /13", ForwardedHeadersExtensions.WideNetwork },
        { "a wide network beside the pod network", ForwardedHeadersExtensions.WideNetwork },
        { "a wide network beside a proxy", ForwardedHeadersExtensions.WideNetwork },
        { "an IPv6 /8", ForwardedHeadersExtensions.WideNetwork },
        { "an IPv6 /47", ForwardedHeadersExtensions.WideNetwork },
        { "every IPv4-mapped address", ForwardedHeadersExtensions.MappedClients },
        { "an IPv6 network holding the mapped block", ForwardedHeadersExtensions.MappedClients },
        { "an IPv4-mapped /13", ForwardedHeadersExtensions.MappedClients },
        { "an IPv6 /65", ForwardedHeadersExtensions.MappedClients },
        { "an IPv6 /104", ForwardedHeadersExtensions.MappedClients },
        { "an IPv6 /108", ForwardedHeadersExtensions.MappedClients },
        { "a network without a prefix", ForwardedHeadersExtensions.UnreadableEntry },
        { "a prefix past 32", ForwardedHeadersExtensions.UnreadableEntry },
        { "a padded entry", ForwardedHeadersExtensions.UnreadableEntry },
        { "address bits after the prefix", ForwardedHeadersExtensions.UnreadableEntry },
        { "address bits after a zero prefix", ForwardedHeadersExtensions.UnreadableEntry },
        { "a host name", ForwardedHeadersExtensions.UnreadableEntry },
        { "an empty entry", ForwardedHeadersExtensions.UnreadableEntry },
        { "two networks in one entry", ForwardedHeadersExtensions.UnreadableEntry },
        { "a header injected into an entry", ForwardedHeadersExtensions.UnreadableEntry },
        { "a proxy that is not an address", ForwardedHeadersExtensions.UnreadableEntry }
    };

    public static TheoryData<string> Acceptances => new()
    {
        "one /16",
        "an IPv4 /14",
        "two networks",
        "an IPv6 /56",
        "an IPv6 /48",
        "an IPv6 /64",
        "an IPv6 /112",
        "dual stack",
        "an IPv4-mapped /16",
        "a proxy address only",
        "a network and a proxy"
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void AddAppForwardedHeaders_Should_RefuseToStart_When_ProductionTrustsNobodyEverybodyOrTooWide(string change, string message)
    {
        var configuration = Configuration(Production(change));

        var add = () => new ServiceCollection().AddAppForwardedHeaders(configuration, ArivaEnvironment.K8sPrd);

        add.Should().Throw<InvalidOperationException>().Which.Message.Should().Be(message, "a constant names the problem, never an entry ({0})", change);
    }

    [Theory]
    [MemberData(nameof(Acceptances))]
    public void AddAppForwardedHeaders_Should_TrustExactlyTheConfiguredEntries_When_ProductionNamesANarrowNetworkOrProxy(string change)
    {
        var values = Production(change);
        var configuration = Configuration(values);

        using var provider = new ServiceCollection().AddAppForwardedHeaders(configuration, ArivaEnvironment.K8sPrd).BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        ForwardedHeadersExtensions.ProductionTrustProblem(configuration).Should().BeNull();
        options.KnownIPNetworks.Should().Equal(Entries(values, NetworksKey).Select(System.Net.IPNetwork.Parse), "the guard judges what the middleware trusts");
        options.KnownProxies.Should().Equal(Entries(values, ProxiesKey).Select(IPAddress.Parse));
    }

    [Fact]
    public void AddAppForwardedHeaders_Should_Refuse_When_OnlyTheSettingsSayProduction()
    {
        // ARV-098 refuses the mismatch at startup; this guard still judges the settings as production on their own.
        var configuration = Configuration(Production("the private ranges"));

        var add = () => new ServiceCollection().AddAppForwardedHeaders(configuration, ArivaEnvironment.K8sDev);

        add.Should().Throw<InvalidOperationException>().WithMessage(ForwardedHeadersExtensions.WideNetwork);
    }

    public static TheoryData<string, string> Elsewhere
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var environment in new[] { ArivaEnvironment.VmLocal, ArivaEnvironment.K8sDev, ArivaEnvironment.K8sDemo })
            foreach (var change in new[] { "nothing trusted", "0.0.0.0/0", "::/0", "the private ranges", "every IPv4-mapped address", "an IPv6 /65", "address bits after the prefix" })
                data.Add(environment, change);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Elsewhere))]
    public void AddAppForwardedHeaders_Should_ChangeNothing_When_NotInProduction(string environment, string change)
    {
        var values = Production(change);
        values["Application:Environment"] = environment;

        using var provider = new ServiceCollection().AddAppForwardedHeaders(Configuration(values), environment).BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        options.KnownIPNetworks.Should().Equal(Entries(values, NetworksKey).Select(System.Net.IPNetwork.Parse), "{0} trusts what it is given, as before", environment);
    }

    #endregion

    #region The settings files

    [Fact]
    public void ProductionSettings_Should_TrustOnlyWhatTheChartPasses_When_TheBaseFileIsRead()
    {
        var shipped = ProductionFiles(new ConfigurationBuilder()).Build();
        // The chart's environment variables, under a prefix of this test's own so no other test sees them.
        var prefix = $"ARIVA_TEST_{Guid.NewGuid():N}_";
        var variables = new Dictionary<string, string>
        {
            [prefix + "Security__ForwardedHeaders__KnownNetworks__0"] = PodNetwork,
            [prefix + "Security__ForwardedHeaders__KnownNetworks__1"] = "fd00:10:244::/56"
        };
        foreach (var (name, value) in variables)
            Environment.SetEnvironmentVariable(name, value);
        try
        {
            var deployed = ProductionFiles(new ConfigurationBuilder()).AddEnvironmentVariables(prefix).Build();

            var settings = shipped.GetSection(ForwardedHeadersSettings.SectionName).Get<ForwardedHeadersSettings>() ?? new ForwardedHeadersSettings();
            settings.KnownNetworks.Should().BeEmpty("the chart supplies the ingress controller's pod network; the file has no default");
            settings.KnownProxies.Should().BeEmpty();
            ForwardedHeadersExtensions.ProductionTrustProblem(shipped).Should().Be(ForwardedHeadersExtensions.NoTrustedProxy);
            ForwardedHeadersExtensions.ProductionTrustProblem(deployed).Should().BeNull();
            deployed.GetSection(NetworksKey).GetChildren().Select(c => c.Value).Should().Equal(PodNetwork, "fd00:10:244::/56");
        }
        finally
        {
            foreach (var name in variables.Keys)
                Environment.SetEnvironmentVariable(name, null);
        }
    }

    #endregion

    #region Every host

    [Theory]
    [MemberData(nameof(ArivaHosts.BackplaneHosts), MemberType = typeof(ArivaHosts))]
    public async Task Host_Should_RefuseToStart_When_ProductionTrustsThePrivateRanges(string host)
    {
        await using var app = ArivaHosts.Create(host, ArivaEnvironment.K8sPrd, PrivateRangesFromTheChart);

        var start = () => app.Services;

        start.Should().Throw<InvalidOperationException>().WithMessage(ForwardedHeadersExtensions.WideNetwork);
    }

    [Theory]
    [MemberData(nameof(ArivaHosts.BackplaneHosts), MemberType = typeof(ArivaHosts))]
    public async Task Host_Should_TrustOnlyThePodNetwork_When_ProductionStarts(string host)
    {
        await using var app = ArivaHosts.Create(host, ArivaEnvironment.K8sPrd);

        var options = app.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        options.KnownIPNetworks.Should().Equal(System.Net.IPNetwork.Parse(PodNetwork));
        options.KnownProxies.Should().BeEmpty();
    }

    public static TheoryData<string, string> ClusterHostsElsewhere
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var host in ArivaHosts.Backplane)
            foreach (var environment in new[] { ArivaEnvironment.K8sDev, ArivaEnvironment.K8sDemo })
                data.Add(host, environment);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ClusterHostsElsewhere))]
    public async Task Host_Should_Start_When_DevOrDemoTrustThePrivateRanges(string host, string environment)
    {
        await using var app = ArivaHosts.Create(host, environment, PrivateRangesFromTheChart);

        var options = app.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        options.KnownIPNetworks.Should().Equal(PrivateRanges.Select(System.Net.IPNetwork.Parse), "{0} is left as it was", environment);
    }

    #endregion

    #region The pipeline

    [Theory]
    [InlineData("10.244.3.7")]
    [InlineData("10.244.255.254")]
    [InlineData("::ffff:10.244.3.7")]
    public async Task ForwardedHeaders_Should_BeHonoured_When_TheConnectionComesFromThePodNetwork(string connection)
    {
        using var host = await PipelineAsync(ArivaEnvironment.K8sPrd, PodNetwork);
        using var client = host.GetTestClient();

        var seen = await SeenAsync(client, connection, Client);

        seen.Should().Be($"{Client} https localhost", "the controller's address is replaced by the client's, and X-Forwarded-Host is never taken");
    }

    [Theory]
    [InlineData("10.245.0.7")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.9")]
    [InlineData("192.168.1.5")]
    [InlineData("::ffff:10.245.0.7")]
    [InlineData("fd00::7")]
    public async Task ForwardedHeaders_Should_BeIgnored_When_TheConnectionComesFromOutsideThePodNetwork(string connection)
    {
        using var host = await PipelineAsync(ArivaEnvironment.K8sPrd, PodNetwork);
        using var client = host.GetTestClient();

        var seen = await SeenAsync(client, connection, Client);

        seen.Should().Be($"{IPAddress.Parse(connection)} http localhost", "another pod in the cluster cannot name a client address or scheme");
    }

    [Fact]
    public async Task ForwardedHeaders_Should_TakeOnlyTheControllersEntry_When_TheClientSendsItsOwn()
    {
        using var host = await PipelineAsync(ArivaEnvironment.K8sPrd, PodNetwork);
        using var client = host.GetTestClient();

        var seen = await SeenAsync(client, "10.244.3.7", "198.51.100.1, " + Client);

        seen.Should().Be($"{Client} https localhost", "ForwardLimit 1: the entry the controller appended, not the one the client wrote before it");
    }

    [Theory]
    [InlineData("fd00:10:244::/65", "::ffff:10.1.2.3")]
    [InlineData("fd00:10:244::/65", "::ffff:100.64.0.9")]
    [InlineData("fd00::a00:0/104", "::ffff:10.1.2.3")]
    public async Task ForwardedHeaders_Should_TrustIPv4Clients_When_AnIPv6PrefixMisleadsDotnetsMask(string network, string connection)
    {
        // Why the guard refuses these IPv6 prefixes (MappedClients): on a dual-stack socket an IPv4 caller is ::ffff:a.b.c.d, and
        // .NET 10's IPNetwork.Contains judges it against an IPv6 network with a mask of (prefix mod 32) bits, so an IPv6 /65 trusts
        // 0.0.0.0/1 and fd00::a00:0/104 trusts 10.0.0.0/8. Shown where the guard does not run. Should this fail after a .NET update,
        // the runtime has changed that arithmetic: review the prefix rule in ForwardedHeadersExtensions.TrustsMappedClients.
        using var host = await PipelineAsync(ArivaEnvironment.K8sDev, network);
        using var client = host.GetTestClient();

        var seen = await SeenAsync(client, connection, Client);

        seen.Should().Be($"{Client} https localhost", "{0} passes an IPv6 /48 rule yet trusts the IPv4 pod {1}", network, connection);
        ForwardedHeadersExtensions.ProductionTrustProblem(new ForwardedHeadersSettings { KnownNetworks = [network] })
            .Should().Be(ForwardedHeadersExtensions.MappedClients);
    }

    [Fact]
    public async Task ForwardedHeaders_Should_NotTrustIPv4Clients_When_TheIPv6PodNetworkIsAccepted()
    {
        // The control for the theory above: the IPv6 networks the guard accepts trust no IPv4 pod.
        foreach (var network in new[] { "fd00:10:244::/48", "fd00:10:244::/56", "fd00:10:244:1::/64", "fd00:10:244:1::/112" })
        {
            ForwardedHeadersExtensions.ProductionTrustProblem(new ForwardedHeadersSettings { KnownNetworks = [network] }).Should().BeNull();
            using var host = await PipelineAsync(ArivaEnvironment.K8sPrd, network);
            using var client = host.GetTestClient();

            foreach (var connection in new[] { "::ffff:10.1.2.3", "::ffff:10.244.3.7", "::ffff:100.64.0.9", "::ffff:172.16.0.9" })
                (await SeenAsync(client, connection, Client)).Should().Be($"{IPAddress.Parse(connection)} http localhost", "{0} holds no IPv4 pod", network);
        }
    }

    [Fact]
    public void FromTrustedProxy_Should_TrustOnlyThePodNetwork_When_ACertificateIsForwarded()
    {
        var configuration = Configuration(Production("one /16"));
        using var provider = new ServiceCollection().AddAppForwardedHeaders(configuration, ArivaEnvironment.K8sPrd).BuildServiceProvider();
        var trusted = provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        ClientCertificateExtensions.FromTrustedProxy(IPAddress.Parse("10.244.3.7"), trusted).Should().BeTrue();
        ClientCertificateExtensions.FromTrustedProxy(IPAddress.Parse("::ffff:10.244.3.7"), trusted).Should().BeTrue("an IPv4-mapped address");
        ClientCertificateExtensions.FromTrustedProxy(IPAddress.Parse("10.245.0.7"), trusted).Should().BeFalse("another pod cannot hand over a certificate");
        ClientCertificateExtensions.FromTrustedProxy(IPAddress.Parse("192.168.1.5"), trusted).Should().BeFalse();
        ClientCertificateExtensions.FromTrustedProxy(null, trusted).Should().BeFalse();
    }

    #endregion

    #region Helpers

    /// <summary>The settings the chart passes in k8s-prd (the pod network), with one change.</summary>
    private static Dictionary<string, string> Production(string change)
    {
        var values = new Dictionary<string, string>
        {
            ["Application:Environment"] = ArivaEnvironment.K8sPrd,
            [NetworksKey + ":0"] = PodNetwork
        };
        (string Key, string Value)[] changes = change switch
        {
            "one /16" => [],
            "nothing trusted" => [(NetworksKey + ":0", null)],
            "0.0.0.0/0" => [(NetworksKey + ":0", "0.0.0.0/0")],
            "::/0" => [(NetworksKey + ":0", "::/0")],
            "0.0.0.0/0 beside the pod network" => [(NetworksKey + ":1", "0.0.0.0/0")],
            "the private ranges" => [.. PrivateRanges.Select((range, i) => (NetworksKey + ":" + i, range))],
            "10.0.0.0/8" => [(NetworksKey + ":0", "10.0.0.0/8")],
            "an IPv4 /13" => [(NetworksKey + ":0", "10.240.0.0/13")],
            "a wide network beside the pod network" => [(NetworksKey + ":1", "172.16.0.0/12")],
            "a wide network beside a proxy" => [(NetworksKey + ":0", "10.0.0.0/8"), (ProxiesKey + ":0", "10.244.1.10")],
            "an IPv6 /8" => [(NetworksKey + ":0", "fd00::/8")],
            "an IPv6 /47" => [(NetworksKey + ":0", "fd00:10:244::/47")],
            "every IPv4-mapped address" => [(NetworksKey + ":0", "::ffff:0.0.0.0/96")],
            "an IPv6 network holding the mapped block" => [(NetworksKey + ":0", "::/64")],
            "an IPv4-mapped /13" => [(NetworksKey + ":0", "::ffff:10.240.0.0/109")],
            "an IPv6 /65" => [(NetworksKey + ":0", "fd00:10:244::/65")],
            "an IPv6 /104" => [(NetworksKey + ":0", "fd00::a00:0/104")],
            "an IPv6 /108" => [(NetworksKey + ":0", "fd00:10:96::/108")],
            "a network without a prefix" => [(NetworksKey + ":0", "10.244.0.0")],
            "a prefix past 32" => [(NetworksKey + ":0", "10.244.0.0/33")],
            "a padded entry" => [(NetworksKey + ":0", " 10.244.0.0/16")],
            // .NET 10 parses these as 10.244.0.0/16 and 0.0.0.0/0, clearing the bits after the prefix: an entry meant as one /24
            // would trust the whole /16, and one mistyped would trust everybody.
            "address bits after the prefix" => [(NetworksKey + ":0", "10.244.1.0/16")],
            "address bits after a zero prefix" => [(NetworksKey + ":0", "10.244.0.1/0")],
            "a host name" => [(NetworksKey + ":0", "ingress-nginx-controller.ingress-nginx.svc")],
            "an empty entry" => [(NetworksKey + ":0", string.Empty)],
            "two networks in one entry" => [(NetworksKey + ":0", "10.244.0.0/16,0.0.0.0/0")],
            "a header injected into an entry" => [(NetworksKey + ":0", "10.244.0.0/16\r\nX-Forwarded-For: 127.0.0.1")],
            "a proxy that is not an address" => [(ProxiesKey + ":0", "ingress.local")],
            "an IPv4 /14" => [(NetworksKey + ":0", "10.244.0.0/14")],
            "two networks" => [(NetworksKey + ":1", "10.245.0.0/16")],
            "an IPv6 /56" => [(NetworksKey + ":0", "fd00:10:244::/56")],
            "an IPv6 /48" => [(NetworksKey + ":0", "fd00:10:244::/48")],
            "an IPv6 /64" => [(NetworksKey + ":0", "fd00:10:244:1::/64")],
            "an IPv6 /112" => [(NetworksKey + ":0", "fd00:10:244:1::/112")],
            "dual stack" => [(NetworksKey + ":1", "fd00:10:244::/56")],
            "an IPv4-mapped /16" => [(NetworksKey + ":0", "::ffff:10.244.0.0/112")],
            "a proxy address only" => [(NetworksKey + ":0", null), (ProxiesKey + ":0", "10.244.1.10")],
            "a network and a proxy" => [(ProxiesKey + ":0", "fd00:10:244::10")],
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

    /// <summary>The values of an array setting, in index order.</summary>
    private static IEnumerable<string> Entries(Dictionary<string, string> values, string key) =>
        values.Where(v => v.Key.StartsWith(key + ":", StringComparison.Ordinal))
            .OrderBy(v => int.Parse(v.Key[(key.Length + 1)..], System.Globalization.CultureInfo.InvariantCulture))
            .Select(v => v.Value);

    private static IConfigurationBuilder ProductionFiles(IConfigurationBuilder builder) => builder
        .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.base.json"))
        .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.base.k8s-prd.json"));

    /// <summary>The private ranges as the chart would pass them, over the fixture's pod network.</summary>
    private static void PrivateRangesFromTheChart(IWebHostBuilder builder)
    {
        for (var i = 0; i < PrivateRanges.Length; i++)
            builder.UseSetting(NetworksKey + ":" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), PrivateRanges[i]);
    }

    /// <summary>
    /// The security baseline's forwarded headers alone, trusting one network, behind a step that sets the connection's address
    /// (TestServer has none); answers with the address, scheme and host the rest of the pipeline sees.
    /// </summary>
    private static Task<IHost> PipelineAsync(string environment, string network)
    {
        var configuration = Configuration(new Dictionary<string, string>
        {
            ["Application:Environment"] = environment,
            [NetworksKey + ":0"] = network
        });
        return new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services => services.AddAppForwardedHeaders(configuration, environment))
                .Configure(app =>
                {
                    app.Use((context, next) =>
                    {
                        if (context.Request.Headers.TryGetValue(RemoteHeader, out var remote))
                            context.Connection.RemoteIpAddress = IPAddress.Parse(remote.ToString());
                        return next(context);
                    });
                    app.UseAppForwardedHeaders();
                    app.Run(context => context.Response.WriteAsync(
                        $"{context.Connection.RemoteIpAddress} {context.Request.Scheme} {context.Request.Host}", context.RequestAborted));
                }))
            .StartAsync(Ct);
    }

    /// <summary>A request over a connection from <paramref name="connection"/> that forwards a client, https and another host.</summary>
    private static async Task<string> SeenAsync(HttpClient client, string connection, string forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add(RemoteHeader, connection);
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-Host", "attacker.example");
        using var response = await client.SendAsync(request, Ct);
        return await response.Content.ReadAsStringAsync(Ct);
    }

    #endregion
}
