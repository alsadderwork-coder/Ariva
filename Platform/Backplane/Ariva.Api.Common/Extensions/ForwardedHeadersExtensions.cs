using System.Net;
using Ariva.Api.Common.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ariva.Api.Common.Extensions;

/// <summary>
/// Forwarded headers from trusted reverse proxies only, read from <c>Security:ForwardedHeaders</c>.
/// </summary>
public static class ForwardedHeadersExtensions
{
    #region Services

    /// <summary>
    /// Trusts <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> only from the configured proxies and networks.
    /// The framework defaults (loopback) are cleared, so an empty configuration trusts nobody and a client cannot
    /// spoof its address to escape the per IP rate limits. <c>X-Forwarded-Host</c> is never trusted.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configuration">The host's layered configuration.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddAppForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

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
    /// Adds the forwarded headers middleware. Call it first, so every later middleware sees the real client.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The same application builder, for chaining.</returns>
    public static IApplicationBuilder UseAppForwardedHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseForwardedHeaders();
    }

    #endregion
}
