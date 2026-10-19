using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Ariva.Api.Common.Hosting;
using Ariva.Api.Common.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ariva.Api.Common.Extensions;

/// <summary>
/// Forwarded headers from trusted reverse proxies only, read from <c>Security:ForwardedHeaders</c>. ARV-085 (ASVS V4.1.3,
/// CWE-348): in k8s-prd the trusted networks are the ingress controller's pod network, which the chart passes from a Helm value
/// (<c>Security__ForwardedHeaders__KnownNetworks__0</c> and so on); appsettings.base.k8s-prd.json lists none, and a k8s-prd host
/// refuses to start when nothing is trusted, when a network is 0.0.0.0/0 or ::/0, when one is wider than an IPv4 /14 or an IPv6
/// /48 (the rule of the chart's validate.yaml), when an IPv6 network would trust IPv4 clients wider than a /14 (below) or when an
/// entry does not parse. The same networks decide whether Ingest takes a forwarded client certificate
/// (<see cref="ClientCertificateExtensions"/>). vm-local, k8s-dev and k8s-demo are left as they are.
/// </summary>
public static class ForwardedHeadersExtensions
{
    #region Messages

    /// <summary>Neither KnownNetworks nor KnownProxies holds an entry.</summary>
    public const string NoTrustedProxy =
        "Security:ForwardedHeaders must list the ingress controller's pod network in KnownNetworks, or its addresses in KnownProxies, in k8s-prd: " +
        "the chart sets them from a Helm value; without them every client shares the controller's address in the rate limits and network checks.";

    /// <summary>A network that is not CIDR or has address bits after its prefix, or a proxy that is not an IP address.</summary>
    public const string UnreadableEntry =
        "Every Security:ForwardedHeaders entry must parse in k8s-prd: KnownNetworks takes CIDR network addresses (no address bits after " +
        "the prefix) and KnownProxies IP addresses.";

    /// <summary>0.0.0.0/0 or ::/0.</summary>
    public const string EveryAddress =
        "Security:ForwardedHeaders:KnownNetworks must not hold 0.0.0.0/0 or ::/0 in k8s-prd: any caller could then name its own address.";

    /// <summary>An IPv4 network wider than /14, or an IPv6 network wider than /48.</summary>
    public const string WideNetwork =
        "Security:ForwardedHeaders:KnownNetworks must hold no network wider than an IPv4 /14 or an IPv6 /48 in k8s-prd: " +
        "list the ingress controller's pod network, not the cluster's private ranges.";

    /// <summary>An IPv6 network that holds the IPv4-mapped block, or whose prefix .NET matches against IPv4 clients with a short mask.</summary>
    public const string MappedClients =
        "Security:ForwardedHeaders:KnownNetworks must hold no IPv6 network that trusts IPv4 clients (a dual-stack pod sees them as " +
        "IPv4-mapped addresses) wider than an IPv4 /14 in k8s-prd: not the IPv4-mapped block or a network that holds it, and no prefix " +
        "from /65 to /77 or from /97 to /109, which .NET matches against an IPv4 client with a mask shorter than /14.";

    #endregion

    #region Fields

    /// <summary>The shortest IPv4 prefix a k8s-prd host trusts: a /14 holds 262,144 addresses, more than a pod network needs.</summary>
    public const int MinimumIPv4Prefix = 14;

    /// <summary>The shortest IPv6 prefix a k8s-prd host trusts.</summary>
    public const int MinimumIPv6Prefix = 48;

    /// <summary>The IPv4-mapped block, ::ffff:0:0/96, as a number: an IPv4 address in the last 32 bits after 0xffff.</summary>
    private static readonly UInt128 MappedBlock = (UInt128)0xFFFF << 32;

    private const int MappedPrefix = 96;

    #endregion

    #region Services

    /// <summary>
    /// Trusts <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> only from the configured proxies and networks.
    /// The framework defaults (loopback) are cleared, so an empty configuration trusts nobody and a client cannot
    /// spoof its address to escape the per IP rate limits. <c>X-Forwarded-Host</c> is never trusted. In k8s-prd the
    /// configuration must name a narrow trusted network or proxy (<see cref="ProductionTrustProblem(IConfiguration)"/>).
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configuration">The host's layered configuration.</param>
    /// <param name="hostEnvironment">The host's own environment name.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="InvalidOperationException">The k8s-prd configuration trusts nobody, everybody, too wide a network or an entry that does not parse.</exception>
    public static IServiceCollection AddAppForwardedHeaders(this IServiceCollection services, IConfiguration configuration, string hostEnvironment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (IsProduction(configuration, hostEnvironment))
            EnsureProductionTrust(configuration);

        services
            .AddOptions<ForwardedHeadersSettings>()
            .Bind(configuration.GetSection(ForwardedHeadersSettings.SectionName))
            .Validate(settings => settings.IsValid,
                $"{ForwardedHeadersSettings.SectionName} needs valid IP addresses, CIDR networks and a positive ForwardLimit.")
            .ValidateOnStart();

        services
            .AddOptions<ForwardedHeadersOptions>()
            .Configure<IOptions<ForwardedHeadersSettings>>((options, settings) =>
            {
                var trusted = settings.Value;
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                options.ForwardLimit = trusted.ForwardLimit;
                options.KnownProxies.Clear();
                options.KnownIPNetworks.Clear();

                foreach (var proxy in trusted.KnownProxies ?? [])
                {
                    options.KnownProxies.Add(IPAddress.Parse(proxy));
                }

                foreach (var network in trusted.KnownNetworks ?? [])
                {
                    options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
                }
            });

        return services;
    }

    #endregion

    #region Middlewares

    /// <summary>
    /// Adds the forwarded headers middleware. Call it first, so every later middleware sees the real client. Right after it
    /// comes the request log context (ARV-091, <see cref="Middlewares.RequestLogContextMiddleware"/>): every log event of the
    /// request then names that client's address and, once the authorization middleware has decided, the caller.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The same application builder, for chaining.</returns>
    public static IApplicationBuilder UseAppForwardedHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseForwardedHeaders().UseMiddleware<Middlewares.RequestLogContextMiddleware>();
    }

    #endregion

    #region Checks

    /// <summary>Throws when the trusted proxies are not fit for k8s-prd (<see cref="ProductionTrustProblem(IConfiguration)"/>).</summary>
    /// <param name="configuration">The host's layered configuration.</param>
    /// <exception cref="InvalidOperationException">The configuration trusts nobody, everybody, too wide a network or an entry that does not parse.</exception>
    public static void EnsureProductionTrust(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (ProductionTrustProblem(configuration) is { } problem)
            throw new InvalidOperationException(problem);
    }

    /// <summary>
    /// Why the <c>Security:ForwardedHeaders</c> section is not fit for k8s-prd, or null. The section is bound as the forwarded
    /// headers options bind it, so the check judges exactly what the middleware will trust.
    /// </summary>
    /// <param name="configuration">The host's layered configuration.</param>
    /// <returns>The problem, or null.</returns>
    public static string ProductionTrustProblem(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var settings = configuration.GetSection(ForwardedHeadersSettings.SectionName).Get<ForwardedHeadersSettings>() ?? new ForwardedHeadersSettings();
        return ProductionTrustProblem(settings);
    }

    /// <summary>
    /// Why these settings are not fit for k8s-prd, or null: at least one network or proxy, every proxy an IP address and every
    /// network CIDR with no address bits after its prefix, no 0.0.0.0/0 or ::/0, no IPv4 network shorter than
    /// <see cref="MinimumIPv4Prefix"/>, no IPv6 network shorter than <see cref="MinimumIPv6Prefix"/> and no IPv6 network that
    /// trusts IPv4 clients wider than an IPv4 <see cref="MinimumIPv4Prefix"/> (<see cref="TrustsMappedClients"/>). The messages are
    /// constants and never name an entry.
    /// </summary>
    /// <param name="settings">The bound settings.</param>
    /// <returns>The problem, or null.</returns>
    public static string ProductionTrustProblem(ForwardedHeadersSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var proxies = settings.KnownProxies ?? [];
        var entries = settings.KnownNetworks ?? [];
        if (proxies.Length == 0 && entries.Length == 0)
            return NoTrustedProxy;
        if (!proxies.All(proxy => IPAddress.TryParse(proxy, out _)))
            return UnreadableEntry;

        var networks = new List<System.Net.IPNetwork>(entries.Length);
        foreach (var entry in entries)
        {
            if (!System.Net.IPNetwork.TryParse(entry, out var network) || !IsNetworkAddress(entry, network))
                return UnreadableEntry;
            networks.Add(network);
        }

        if (networks.Any(network => network.PrefixLength == 0))
            return EveryAddress;
        if (networks.Any(IsWide))
            return WideNetwork;
        return networks.Any(TrustsMappedClients) ? MappedClients : null;
    }

    #endregion

    #region Helpers

    private static bool IsProduction(IConfiguration configuration, string hostEnvironment) =>
        string.Equals(hostEnvironment, ArivaEnvironment.K8sPrd, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(configuration["Application:Environment"], ArivaEnvironment.K8sPrd, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the entry names its network's own address. .NET 10 parses 10.244.1.0/16 as 10.244.0.0/16, clearing the bits after the
    /// prefix, so an entry meant as one /24 would trust the whole /16; the chart refuses such entries too.
    /// </summary>
    private static bool IsNetworkAddress(string entry, System.Net.IPNetwork network)
    {
        var slash = entry.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 && IPAddress.TryParse(entry.AsSpan(0, slash), out var address) && address.Equals(network.BaseAddress);
    }

    /// <summary>Whether a network is shorter than a k8s-prd host trusts in its own family.</summary>
    private static bool IsWide(System.Net.IPNetwork network) =>
        network.PrefixLength < (network.BaseAddress.AddressFamily == AddressFamily.InterNetwork ? MinimumIPv4Prefix : MinimumIPv6Prefix);

    /// <summary>
    /// Whether an IPv6 network trusts more IPv4 clients than an IPv4 <see cref="MinimumIPv4Prefix"/>. Kestrel's dual-stack socket
    /// (https://*:8443) shows an IPv4 client as ::ffff:a.b.c.d, and the forwarded headers middleware matches that address against
    /// every network. .NET 10's IPNetwork.Contains judges a mapped address against an IPv6 network with IPv4 arithmetic: the
    /// network's last 32 bits under a mask of (prefix mod 32) bits, so fd00:10:244::/65 trusts 0.0.0.0/1 and fd00::a00:0/104
    /// trusts 10.0.0.0/8 (ForwardedHeadersTests shows it through the middleware). Those prefixes are refused. A network that holds
    /// the whole mapped block (::/64, ::ffff:0:0/96) is refused too, because by the addresses it holds every IPv4 client; that is
    /// computed from its bits, not with Contains, which the same arithmetic would mislead.
    /// </summary>
    private static bool TrustsMappedClients(System.Net.IPNetwork network)
    {
        if (network.BaseAddress.AddressFamily != AddressFamily.InterNetworkV6)
            return false;
        var prefix = network.PrefixLength;
        var dotnetMask = prefix % 32 == 0 ? 32 : prefix % 32;
        if (dotnetMask < MinimumIPv4Prefix)
            return true;
        var value = BinaryPrimitives.ReadUInt128BigEndian(network.BaseAddress.GetAddressBytes());
        return prefix <= MappedPrefix && (MappedBlock & (UInt128.MaxValue << (128 - prefix))) == value;
    }

    #endregion
}
