using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.ServiceDefaults;

/// <summary>
/// The CA a log or telemetry collector chains to (ARV-200, ASVS 5.0 V12.3.1, V12.3.2, V12.3.4, V16.4.3). By default a host
/// verifies Loki and the OTLP collector against the image's public roots, so a collector whose certificate comes from the
/// site PKI could not be reached: the host starts (nothing connects to a collector at start) and its logs and telemetry never
/// arrive. With <see cref="OtlpSetting"/> or <see cref="LokiSetting"/> naming a PEM file (the site CA, or a bundle of the
/// site's CAs), that collector's client trusts those certificates alone, as the database and Redis connections trust their
/// own CA files (ARV-082a): never the public roots beside them, and nothing else changes, so the outbound integrations keep
/// the public roots. Each collector has a named client of its own (<see cref="Pin"/>), pinned through IHttpClientFactory,
/// with the host name still checked by the TLS handshake. Revocation is not checked, as for the database and Redis, and no
/// certificate is ever downloaded (CWE-918): the collector sends its intermediates in the handshake.
/// </summary>
public static class CollectorTrust
{
    #region Constants

    /// <summary>The OTLP collector's CA file (PEM), for the trace, metric and log exporters and Serilog's OTLP sink.</summary>
    public const string OtlpSetting = "Otlp:RootCertificate";

    /// <summary>Loki's CA file (PEM), for Serilog's Loki sink.</summary>
    public const string LokiSetting = "Loki:RootCertificate";

    /// <summary>The client Serilog's Loki sink sends through when <see cref="LokiSetting"/> is set.</summary>
    public const string LokiClient = "ariva-loki";

    /// <summary>The client Serilog's OTLP sink sends through when <see cref="OtlpSetting"/> is set.</summary>
    public const string OtlpLogsClient = "ariva-otlp-logs";

    /// <summary>
    /// The client OpenTelemetry's trace, metric and log exporters send through when <see cref="OtlpSetting"/> is set: their
    /// options' HttpClientFactory names it, for gRPC and http/protobuf alike (the exporters would otherwise take a factory
    /// client of their own over http/protobuf only, and build a client over gRPC).
    /// </summary>
    public const string OtlpExporterClient = "ariva-otlp-exporter";

    /// <summary>The most a collector's CA file may hold: a CA, or a site's bundle of a few, is a few kilobytes.</summary>
    public const int MaxFileBytes = 1024 * 1024;

    /// <summary>
    /// OpenTelemetry's own variables for a collector's CA and a client certificate, shared and per signal. Ariva does not read
    /// them: its pinned client replaces the exporters' own, and Serilog's OTLP sink never reads them, so a CA given there would
    /// reach the traces and metrics but not the logs, and a client certificate would be dropped (<see cref="EnsureNoLibraryCertificates"/>).
    /// </summary>
    public static readonly IReadOnlyList<string> LibraryCertificateVariables =
    [
        .. new[] { "", "TRACES_", "METRICS_", "LOGS_" }.SelectMany(signal => new[]
        {
            $"OTEL_EXPORTER_OTLP_{signal}CERTIFICATE", $"OTEL_EXPORTER_OTLP_{signal}CLIENT_CERTIFICATE", $"OTEL_EXPORTER_OTLP_{signal}CLIENT_KEY"
        })
    ];

    /// <summary>The server authentication purpose a collector's certificate must hold.</summary>
    private const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";

    private const string VmLocal = "vm-local";

    #endregion

    #region Methods

    /// <summary>
    /// The certificates a collector's setting names, or null when it names none. A file that is missing, larger than
    /// <see cref="MaxFileBytes"/>, holds no certificate, or holds a private key (a key pair mounted by mistake, never a CA
    /// bundle) stops the host with a message that names the setting and the file, never its contents.
    /// </summary>
    /// <param name="configuration">The host's configuration.</param>
    /// <param name="setting"><see cref="OtlpSetting"/> or <see cref="LokiSetting"/>.</param>
    /// <exception cref="InvalidOperationException">The file cannot serve as the collector's CA.</exception>
    public static X509Certificate2Collection RootsFrom(IConfiguration configuration, string setting)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(setting);
        var path = configuration[setting];
        if (string.IsNullOrWhiteSpace(path))
            return null;
        if (!File.Exists(path))
            throw new InvalidOperationException($"{setting} names a file that does not exist ({path}): mount the collector's CA secret.");

        var text = ReadAtMost(setting, path);
        if (text.Contains("PRIVATE KEY", StringComparison.Ordinal))
            throw new InvalidOperationException($"{setting} ({path}) holds a private key: a collector's CA file holds certificates only (the CA's ca.crt).");
        var roots = new X509Certificate2Collection();
        try
        {
            roots.ImportFromPem(text);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            throw new InvalidOperationException($"{setting} ({path}) is not a PEM certificate file.");
        }

        return roots.Count > 0
            ? roots
            : throw new InvalidOperationException($"{setting} ({path}) holds no PEM certificate: give the CA the collector's certificate chains to.");
    }

    /// <summary>
    /// Pins the named clients to <paramref name="roots"/> (custom root trust, server authentication, no revocation check, no
    /// certificate download), the TLS handshake still checking the collector's host name; follows no redirect and keeps no
    /// cookie (CWE-918: a collector's redirect would send a batch and its headers to another host); and keeps their requests
    /// out of the logs they carry (a logged request of the Loki client would be shipped by the same client). Each pinned name
    /// is recorded, so a sink can require its client to be pinned (<see cref="IsPinned"/>).
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="roots">The collector's CA certificates (<see cref="RootsFrom"/>).</param>
    /// <param name="clients">The clients that reach that collector.</param>
    /// <returns>The builders, for a caller that adds more to a client.</returns>
    public static IReadOnlyList<IHttpClientBuilder> Pin(IServiceCollection services, X509Certificate2Collection roots, IEnumerable<string> clients)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(clients);
        if (roots.Count == 0)
            throw new ArgumentException("A collector is pinned to one certificate at least.", nameof(roots));

        return clients
            .Select(name =>
            {
                services.AddSingleton(new PinnedClient(name));
                return services
                    .AddHttpClient(name)
                    .UseSocketsHttpHandler((handler, _) =>
                    {
                        handler.SslOptions.CertificateChainPolicy = Policy(roots);
                        handler.AllowAutoRedirect = false;
                        handler.UseCookies = false;
                    })
                    .RemoveAllLoggers();
            })
            .ToList();
    }

    /// <summary>
    /// True when <paramref name="client"/> was pinned in these services (<see cref="Pin"/>): a factory hands out a client of
    /// any name, an unpinned one with the public roots, so a sink whose CA file is set requires this before it takes one.
    /// </summary>
    /// <param name="services">The host's services, or null.</param>
    /// <param name="client">The client's name.</param>
    public static bool IsPinned(IServiceProvider services, string client) =>
        services?.GetService<IEnumerable<PinnedClient>>()?.Any(pinned => string.Equals(pinned.Name, client, StringComparison.Ordinal)) == true;

    /// <summary>
    /// OpenTelemetry's certificate variables (<see cref="LibraryCertificateVariables"/>) are refused outside vm-local, whether
    /// OTLP is on or not: a collector's CA is <see cref="OtlpSetting"/>, and a client certificate is not supported. The
    /// message names the variables, never a value.
    /// </summary>
    /// <param name="configuration">The host's configuration.</param>
    /// <param name="environment">The host's environment; null takes the rules of every environment but vm-local.</param>
    /// <exception cref="InvalidOperationException">One of them is set.</exception>
    public static void EnsureNoLibraryCertificates(IConfiguration configuration, string environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (string.Equals(environment, VmLocal, StringComparison.Ordinal))
            return;
        var set = LibraryCertificateVariables.Where(name => !string.IsNullOrWhiteSpace(configuration[name])).ToList();
        if (set.Count > 0)
            throw new InvalidOperationException(
                $"{string.Join(", ", set)} is not read: give the OTLP collector's CA as {OtlpSetting} (the chart's otel.caSecretName); a client certificate for the collector is not supported.");
    }

    /// <summary>
    /// A collector's CA is checked over TLS only, so with its CA file set an http address would trust nothing and send in the
    /// clear: the host does not start. For Loki, its address.
    /// </summary>
    /// <exception cref="InvalidOperationException">The address is not https.</exception>
    public static void EnsureHttps(Uri lokiUri)
    {
        ArgumentNullException.ThrowIfNull(lokiUri);
        if (lokiUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"{LokiSetting} is set, so Loki:Uri must be https: a collector's CA is checked over TLS only.");
    }

    /// <summary>
    /// As <see cref="EnsureHttps(Uri)"/> for the OTLP collector: both addresses an exporter may use, Ariva's endpoint and
    /// OTEL_EXPORTER_OTLP_ENDPOINT when it is set (Serilog's OTLP sink reads it last).
    /// </summary>
    /// <exception cref="InvalidOperationException">An address is not https.</exception>
    public static void EnsureHttps(OtlpSettings otlp, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(otlp);
        ArgumentNullException.ThrowIfNull(configuration);
        var variable = configuration[OtlpSettings.EndpointVariable];
        if (otlp.Endpoint.Scheme != Uri.UriSchemeHttps ||
            (!string.IsNullOrWhiteSpace(variable) && (!Uri.TryCreate(variable, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)))
            throw new InvalidOperationException(
                $"{OtlpSetting} is set, so the OTLP endpoint (Otlp:Endpoint or {OtlpSettings.EndpointVariable}) must be https: a collector's CA is checked over TLS only.");
    }

    // A policy per handler, which the factory builds for each handler lifetime; that handler's connections share it and only
    // read it (its application policy is set here, so the handshake adds none). The chain is built from what the collector
    // sent, never by fetching a missing intermediate from the address a certificate names (CWE-918), as OutboundTransport does.
    private static X509ChainPolicy Policy(X509Certificate2Collection roots)
    {
        var policy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck,
            DisableCertificateDownloads = true
        };
        policy.CustomTrustStore.AddRange(roots);
        policy.ApplicationPolicy.Add(new Oid(ServerAuthentication));
        return policy;
    }

    /// <summary>The file's text, read up to <see cref="MaxFileBytes"/> (a wrong path to a large file or a device stops the host, never hangs it).</summary>
    private static string ReadAtMost(string setting, string path)
    {
        var buffer = new byte[MaxFileBytes + 1];
        int read;
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            read = file.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return read > MaxFileBytes
            ? throw new InvalidOperationException($"{setting} ({path}) is larger than {MaxFileBytes / 1024} KB: a collector's CA file holds a few certificates.")
            : Encoding.UTF8.GetString(buffer, 0, read);
    }

    #endregion

    #region Types

    /// <summary>A client <see cref="Pin"/> pinned, by name.</summary>
    private sealed record PinnedClient(string Name);

    #endregion
}
