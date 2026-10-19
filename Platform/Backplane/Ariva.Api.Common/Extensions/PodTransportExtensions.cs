using System.Net.Security;
using System.Security.Authentication;
using Ariva.Api.Common.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.Api.Common.Extensions;

/// <summary>
/// ARV-082b (CWE-319, CWE-295; ASVS V12.3.1, V12.3.3): in k8s-prd every pod serves https only, with the certificate the cluster CA
/// issued for it (the pod TLS secret, mounted at /app/secrets/pod-tls), so the ingress reaches the pod over TLS and verifies it.
/// appsettings.base.k8s-prd.json declares the endpoint (Kestrel:Endpoints:Https on 8443). Configured endpoints take the place of
/// the addresses Kestrel would otherwise bind, so the image's ASPNETCORE_HTTP_PORTS (8080) leaves no plain listener. A k8s-prd
/// host refuses to start when its configuration would serve plain HTTP, names no certificate file that is there, or allows a TLS
/// version below 1.2; on Linux its https endpoints offer the TLS 1.3 suites and, for TLS 1.2, ECDHE with AES-GCM or
/// ChaCha20-Poly1305 only (the database's ssl_ciphers of ARV-082a). vm-local, k8s-dev and k8s-demo are left as they are. Kestrel
/// watches the certificate files and serves a renewed certificate without a restart (PodTransportTests).
/// </summary>
public static class PodTransportExtensions
{
    #region Messages

    /// <summary>No endpoint is declared, so Kestrel would bind the image's plain port.</summary>
    public const string NoEndpoint =
        "Kestrel:Endpoints must declare the pod's https endpoint in k8s-prd: without one Kestrel serves plain HTTP on ASPNETCORE_HTTP_PORTS.";

    /// <summary>An endpoint whose Url is not https.</summary>
    public const string PlainEndpoint = "Kestrel must serve https only in k8s-prd: the ingress verifies the pod's certificate.";

    /// <summary>An endpoint without a certificate file and key file of its own.</summary>
    public const string NoCertificate =
        "Every Kestrel endpoint must name its certificate's Path and KeyPath in k8s-prd: the pod serves the certificate the cluster CA issued for it.";

    /// <summary>The certificate or key file is not there.</summary>
    public const string MissingCertificate =
        "A Kestrel endpoint's certificate Path or KeyPath names a file that does not exist: mount the pod TLS secret.";

    /// <summary>A TLS version below 1.2, or a value that leaves the choice to the operating system.</summary>
    public const string WeakProtocols =
        "Kestrel's SslProtocols must list only Tls12 and Tls13 in k8s-prd: older versions, None, Default and unknown names are refused.";

    /// <summary>ASPNETCORE_URLS (the urls setting, also DOTNET_URLS) names a plain address.</summary>
    public const string PlainUrls = "ASPNETCORE_URLS must not hold an http:// address in k8s-prd: the pod serves https only.";

    /// <summary>preferHostingUrls would bind the urls and ports in place of the configured endpoints.</summary>
    public const string PreferredHostingUrls =
        "preferHostingUrls must not be true in k8s-prd: it binds ASPNETCORE_URLS and ASPNETCORE_HTTP_PORTS in place of the https endpoint.";

    #endregion

    #region Fields

    private const string EndpointsKey = "Kestrel:Endpoints";
    private const string EndpointDefaultsProtocolsKey = "Kestrel:EndpointDefaults:SslProtocols";

    /// <summary>
    /// The names of the versions a pod may offer, which configured values are checked against by name: for the endpoints the
    /// settings declare, the code makes no protocol choice of its own (CA5398), the settings file does. A listener declared in code
    /// takes the same names (<see cref="Protocols"/>).
    /// </summary>
    private static readonly string[] ModernNames = ["Tls12", "Tls13"];

    #endregion

    #region Properties

    /// <summary>
    /// TLS 1.2 and 1.3 as one value, for a TLS listener Ariva declares in code rather than in Kestrel:Endpoints (Ingest's MQTT
    /// listener, ARV-084; ASVS V12.1.1): parsed from the names the configured endpoints are checked against, so the pods and the
    /// listener offer the same versions. Pinned on purpose, not left to the operating system: a newer version needs a change here.
    /// </summary>
    public static SslProtocols Protocols { get; } = Enum.Parse<SslProtocols>(string.Join(", ", ModernNames));

    /// <summary>
    /// The suites the pod's https endpoints offer on Linux: TLS 1.3's three, and for TLS 1.2 ECDHE (forward secrecy) with an AEAD
    /// cipher, for ECDSA and RSA certificates alike. No CBC suite, no static RSA key exchange and no finite field DHE, matching
    /// the database's ssl_ciphers ECDHE+AESGCM:ECDHE+CHACHA20 (ARV-082a). Ingest's MQTT listener offers the same (ARV-084).
    /// </summary>
    public static IReadOnlyList<TlsCipherSuite> CipherSuites { get; } =
    [
        TlsCipherSuite.TLS_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_CHACHA20_POLY1305_SHA256,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256
    ];

    #endregion

    #region Services

    /// <summary>
    /// In k8s-prd: refuses a configuration that would serve plain HTTP or weak TLS (<see cref="ProductionTransportProblem"/>), and
    /// on Linux restricts the cipher suites of every https endpoint through Kestrel's https defaults. Does nothing elsewhere.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configuration">The host's layered configuration (reads Kestrel, urls and preferHostingUrls).</param>
    /// <param name="hostEnvironment">The host's own environment name.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="InvalidOperationException">The k8s-prd configuration is not fit to serve.</exception>
    public static IServiceCollection AddAppPodTransport(this IServiceCollection services, IConfiguration configuration, string hostEnvironment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!IsProduction(configuration, hostEnvironment))
            return services;

        EnsureProductionTransport(configuration);
        if (OperatingSystem.IsLinux())
        {
            // One policy for every handshake; Kestrel applies the https defaults to the configured endpoints before their own settings.
            var policy = new CipherSuitesPolicy(CipherSuites);
            services.AddOptions<KestrelServerOptions>().Configure(kestrel => kestrel.ConfigureHttpsDefaults(https => RestrictCipherSuites(https, policy)));
        }

        return services;
    }

    #endregion

    #region Checks

    /// <summary>Throws when the configuration is not fit for k8s-prd (<see cref="ProductionTransportProblem"/>).</summary>
    /// <param name="configuration">The host's layered configuration.</param>
    /// <exception cref="InvalidOperationException">The configuration would serve plain HTTP or weak TLS.</exception>
    public static void EnsureProductionTransport(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (ProductionTransportProblem(configuration, File.Exists) is { } problem)
            throw new InvalidOperationException(problem);
    }

    /// <summary>
    /// Why this configuration is not fit for k8s-prd, or null: at least one Kestrel endpoint, every one https with a certificate
    /// Path and KeyPath whose files exist, SslProtocols (when set, on the endpoint, its SNI entries or the endpoint defaults) only
    /// Tls12 and Tls13, no http:// address in urls and no preferHostingUrls. The messages are constants and never name a file's
    /// content.
    /// </summary>
    /// <param name="configuration">The host's layered configuration.</param>
    /// <param name="fileExists">Whether a file exists (<see cref="File.Exists(string)"/> in the hosts).</param>
    /// <returns>The problem, or null.</returns>
    public static string ProductionTransportProblem(IConfiguration configuration, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(fileExists);

        var urls = configuration[WebHostDefaults.ServerUrlsKey] ?? string.Empty;
        if (urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(url => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
            return PlainUrls;
        if (IsTrue(configuration[WebHostDefaults.PreferHostingUrlsKey]))
            return PreferredHostingUrls;
        if (AllowsBelowTls12(configuration.GetSection(EndpointDefaultsProtocolsKey)))
            return WeakProtocols;

        var endpoints = configuration.GetSection(EndpointsKey).GetChildren().ToList();
        if (endpoints.Count == 0)
            return NoEndpoint;
        foreach (var endpoint in endpoints)
        {
            if (!(endpoint["Url"] ?? string.Empty).Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return PlainEndpoint;
            var certificate = endpoint["Certificate:Path"];
            var key = endpoint["Certificate:KeyPath"];
            if (string.IsNullOrWhiteSpace(certificate) || string.IsNullOrWhiteSpace(key))
                return NoCertificate;
            if (!fileExists(certificate) || !fileExists(key))
                return MissingCertificate;
            if (AllowsBelowTls12(endpoint.GetSection("SslProtocols")) ||
                endpoint.GetSection("Sni").GetChildren().Any(sni => AllowsBelowTls12(sni.GetSection("SslProtocols"))))
                return WeakProtocols;
        }

        return null;
    }

    #endregion

    #region Helpers

    private static bool IsProduction(IConfiguration configuration, string hostEnvironment) =>
        string.Equals(hostEnvironment, ArivaEnvironment.K8sPrd, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(configuration["Application:Environment"], ArivaEnvironment.K8sPrd, StringComparison.OrdinalIgnoreCase);

    /// <summary>As the web host reads preferHostingUrls: "true" or "1".</summary>
    private static bool IsTrue(string value) =>
        string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase) || string.Equals(value?.Trim(), "1", StringComparison.Ordinal);

    /// <summary>
    /// Whether an SslProtocols section allows less than TLS 1.2, read as Kestrel reads it: a list of names parsed without regard to
    /// case. Unset means the endpoint defaults apply (checked on their own). A value Kestrel would skip (an unknown name, or a single
    /// value where it reads a list) leaves the operating system's choice, which is refused as None and Default are. A parsed value
    /// is fit when every flag it holds is named Tls12 or Tls13: Default, None, the older versions and undefined bits (a number) all
    /// format to other names.
    /// </summary>
    private static bool AllowsBelowTls12(IConfigurationSection protocols)
    {
        var names = protocols.GetChildren().Select(child => child.Value).ToList();
        if (names.Count == 0)
            return !string.IsNullOrWhiteSpace(protocols.Value);
        return names.Any(name => !Enum.TryParse(name, ignoreCase: true, out SslProtocols parsed) ||
                                 parsed.ToString().Split(", ").Any(flag => !ModernNames.Contains(flag, StringComparer.Ordinal)));
    }

    /// <summary>Sets the policy on every handshake of an https endpoint, after any callback set before it.</summary>
    private static void RestrictCipherSuites(HttpsConnectionAdapterOptions https, CipherSuitesPolicy policy)
    {
        var previous = https.OnAuthenticate;
        https.OnAuthenticate = (connection, authentication) =>
        {
            previous?.Invoke(connection, authentication);
            authentication.CipherSuitesPolicy = policy;
        };
    }

    #endregion
}
