using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Ariva.Api.Common.Extensions;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using MQTTnet.AspNetCore;
using MQTTnet.Server;

namespace Ariva.Api.Ingest.Mqtt;

/// <summary>The MQTT transport (ARV-024): an MQTTnet broker on a Kestrel listener of its own, behind the packet size limit.</summary>
public static class MqttExtensions
{
    /// <summary>Registers the broker when <c>Ingest:Mqtt:Enabled</c>; checks the settings at start-up.</summary>
    public static IServiceCollection AddArivaMqttTransport(this IServiceCollection services, IConfiguration configuration, string environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var settings = configuration.GetSection(MqttSettings.SectionName).Get<MqttSettings>() ?? new MqttSettings();
        var problems = settings.Problems(environment).ToList();
        if (problems.Count > 0)
            throw new InvalidOperationException(string.Join(' ', problems));
        services.AddOptions<MqttSettings>().Bind(configuration.GetSection(MqttSettings.SectionName));
        if (!settings.Enabled)
            return services;

        services.AddMqttConnectionHandler();
        services.AddHostedMqttServer(options => options
            .WithDefaultCommunicationTimeout(TimeSpan.FromSeconds(settings.ConnectTimeoutSeconds))
            .WithMaxPendingMessagesPerClient(100)
            .WithPersistentSessions(false)
            .WithKeepAlive());
        services.AddSingleton<MqttConnectionLimits>();
        services.AddSingleton<MqttDeviceBroker>();
        services.AddHostedService(sp => sp.GetRequiredService<MqttDeviceBroker>());
        return services;
    }

    /// <summary>
    /// Adds the MQTT listener to Kestrel when enabled: TLS 1.2 and 1.3 only, with the pods' cipher suites on Linux (<see cref="TlsOptions"/>,
    /// ARV-084) and the configured certificate (client certificates are optional and checked against a device's pin, not a CA),
    /// the packet size limit, then MQTTnet.
    /// Declaring a listener in code replaces the URLs Kestrel would otherwise bind, so the HTTP endpoints are declared
    /// here too (<see cref="HttpEndpoints"/>): none when Kestrel:Endpoints declares the host's endpoints (k8s-prd's https
    /// endpoint, ARV-082b), which Kestrel binds beside this listener; else the configured URLs, else HTTP ports, else
    /// Application:BindingPort, else 8080.
    /// </summary>
    public static IWebHostBuilder UseArivaMqttListener(this IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.ConfigureKestrel((context, kestrel) =>
        {
            var settings = context.Configuration.GetSection(MqttSettings.SectionName).Get<MqttSettings>() ?? new MqttSettings();
            if (!settings.Enabled)
                return;
            X509Certificate2 certificate = null;
            if (settings.RequireTls)
            {
                using var pem = X509Certificate2.CreateFromPemFile(settings.CertificatePath, settings.CertificateKeyPath);
                // Re-imported so the private key is usable by SslStream on every platform.
                certificate = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
                // ARV-125a: only its end of validity is kept, for ariva.certificates.expires_in.
                kestrel.ApplicationServices.GetService<Ariva.Infra.Monitoring.CertificateMetrics>()?
                    .Track(Ariva.Infra.Monitoring.CertificateMetrics.MqttServer, certificate.NotAfter.ToUniversalTime());
            }

            var open = 0;
            kestrel.ListenAnyIP(settings.Port, listen =>
            {
                var limits = listen.ApplicationServices.GetRequiredService<MqttConnectionLimits>();
                // Connections beyond the limits (all together, and per client address) are dropped before the TLS
                // handshake costs anything.
                listen.Use(next => async connection =>
                {
                    var address = (connection.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
                    if (address.IsIPv4MappedToIPv6)
                        address = address.MapToIPv4();
                    if (Interlocked.Increment(ref open) > settings.MaxConnections)
                    {
                        Interlocked.Decrement(ref open);
                        connection.Abort();
                        return;
                    }

                    if (!limits.TryOpen(address, settings.MaxConnectionsPerAddress))
                    {
                        Interlocked.Decrement(ref open);
                        connection.Abort();
                        return;
                    }

                    try
                    {
                        await next(connection);
                    }
                    finally
                    {
                        limits.Close(address);
                        Interlocked.Decrement(ref open);
                    }
                });
                if (certificate is not null)
                    listen.UseHttps(TlsOptions(certificate, TimeSpan.FromSeconds(settings.ConnectTimeoutSeconds)));

                listen.UseMqttPacketLimit(limits);
                listen.UseMqtt();
            });

            foreach (var endpoint in HttpEndpoints(context.Configuration))
                kestrel.Listen(endpoint);
        });
    }

    /// <summary>
    /// The listener's TLS in every environment that uses it (ARV-084; CWE-319, CWE-327; ASVS V12.1.1, V12.1.2): TLS 1.2 and 1.3 only,
    /// the versions the pods offer (<see cref="PodTransportExtensions.Protocols"/>), whatever the operating system would allow; and on
    /// Linux the pods' suites (<see cref="PodTransportExtensions.CipherSuites"/>): TLS 1.3's three and, for TLS 1.2, ECDHE with AES-GCM
    /// or ChaCha20-Poly1305, so no CBC suite and no static RSA key exchange. Client certificates stay optional and are checked against
    /// a device's pin, not a CA. Not used in vm-local without TLS (<see cref="MqttSettings.RequireTls"/>).
    /// </summary>
    /// <param name="certificate">The server certificate with its private key.</param>
    /// <param name="handshakeTimeout">The time a client has to finish the handshake.</param>
    /// <returns>The options for Kestrel's UseHttps on the MQTT listener.</returns>
    public static HttpsConnectionAdapterOptions TlsOptions(X509Certificate2 certificate, TimeSpan handshakeTimeout)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        // The options are given whole, so Kestrel's https defaults (the pods' policy in k8s-prd) do not reach this listener: it sets its own.
        var options = new HttpsConnectionAdapterOptions
        {
            ServerCertificate = certificate,
            SslProtocols = PodTransportExtensions.Protocols,
            ClientCertificateMode = ClientCertificateMode.AllowCertificate,
            // A device's certificate is pinned by its SHA-256 (ARV-022), not chained to a CA.
            ClientCertificateValidation = (_, _, _) => true,
            HandshakeTimeout = handshakeTimeout
        };
        if (OperatingSystem.IsLinux())
        {
            // CipherSuitesPolicy is a Linux feature of SslStream (the images run on Linux); one policy for every handshake.
            var policy = new CipherSuitesPolicy(PodTransportExtensions.CipherSuites);
            options.OnAuthenticate = (_, authentication) => authentication.CipherSuitesPolicy = policy;
        }

        return options;
    }

    /// <summary>The HTTP endpoints the host would have bound without the MQTT listener.</summary>
    public static IReadOnlyList<IPEndPoint> HttpEndpoints(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        // ARV-082b: Kestrel binds the endpoints in Kestrel:Endpoints together with the ones declared in code, and the URLs and
        // ports give way to them; declaring the image's plain port (ASPNETCORE_HTTP_PORTS) here would add the listener they leave out.
        if (configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
            return [];

        var endpoints = new List<IPEndPoint>();
        foreach (var url in (configuration["urls"] ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                continue;
            var rest = url["http://".Length..].TrimEnd('/');
            var colon = rest.LastIndexOf(':');
            if (colon < 0 || !int.TryParse(rest[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
                continue;
            var host = rest[..colon].Trim('[', ']');
            var address = host is "*" or "+" or "0.0.0.0" ? IPAddress.Any
                : host is "localhost" ? IPAddress.Loopback
                : IPAddress.TryParse(host, out var parsed) ? parsed : IPAddress.Any;
            endpoints.Add(new IPEndPoint(address, port));
        }

        if (endpoints.Count == 0)
        {
            foreach (var text in (configuration["http_ports"] ?? string.Empty).Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
                    endpoints.Add(new IPEndPoint(IPAddress.Any, port));
            }
        }

        if (endpoints.Count == 0)
            endpoints.Add(new IPEndPoint(IPAddress.Any, int.TryParse(configuration["Application:BindingPort"], NumberStyles.None, CultureInfo.InvariantCulture, out var binding) ? binding : 8080));
        return endpoints;
    }
}
