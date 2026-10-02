using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ariva.Api.Common.Extensions;

/// <summary>
/// Client certificates behind a TLS-terminating proxy (ARV-022). With <c>Security:ClientCertificates:ForwardedHeader</c>
/// set (ingress-nginx: <c>ssl-client-cert</c>, with <c>auth-tls-pass-certificate-to-upstream</c>), the certificate the
/// proxy verified arrives URL-encoded in PEM and becomes the connection's client certificate, which pinned devices must
/// present. The header is honoured only when the connection itself comes from a trusted proxy
/// (<c>Security:ForwardedHeaders</c>), and is removed from every request, so a caller that reaches the host directly
/// cannot name a certificate (a certificate is public; naming it is not holding it).
/// </summary>
public static class ClientCertificateExtensions
{
    public const string ForwardedHeaderSetting = "Security:ClientCertificates:ForwardedHeader";
    private const int MaxHeaderLength = 16_384;

    /// <summary>Kept for symmetry with the other host extensions; the middleware reads its settings itself.</summary>
    public static IServiceCollection AddAppClientCertificates(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        return services;
    }

    /// <summary>
    /// Call before <c>UseAppForwardedHeaders</c>, while the connection's address is still the proxy's; does nothing
    /// unless configured.
    /// </summary>
    public static IApplicationBuilder UseAppClientCertificates(this IApplicationBuilder app, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(configuration);
        var header = configuration[ForwardedHeaderSetting];
        if (string.IsNullOrWhiteSpace(header))
            return app;

        var trusted = app.ApplicationServices.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
        return app.Use((context, next) =>
        {
            if (context.Request.Headers.Remove(header, out var values) && values.Count == 1 && FromTrustedProxy(context.Connection.RemoteIpAddress, trusted))
                context.Connection.ClientCertificate = Parse(values[0]);
            return next(context);
        });
    }

    public static bool FromTrustedProxy(IPAddress address, ForwardedHeadersOptions trusted)
    {
        if (address is null)
            return false;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        return trusted.KnownProxies.Any(p => p.Equals(address)) || trusted.KnownIPNetworks.Any(n => n.Contains(address));
    }

    public static X509Certificate2 Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxHeaderLength)
            return null;
        try
        {
            return X509Certificate2.CreateFromPem(Uri.UnescapeDataString(value));
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or ArgumentException or UriFormatException)
        {
            return null;
        }
    }
}
