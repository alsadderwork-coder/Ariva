using Ariva.Core.Services.Integration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ariva.Di.Extensions;

/// <summary>Integration clients (ARV-042), the Integration API's batches (ARV-043) and outbound endpoints (ARV-045).</summary>
public static class IntegrationExtensions
{
    /// <summary>Administration of integration clients (Ariva.Api.Main).</summary>
    public static IServiceCollection AddArivaIntegrationClients(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<Ariva.Infra.Services.Administration.AuditTrail>();
        services.TryAddScoped<ISvcIntegrationClients, Ariva.Infra.Services.Integration.SvcIntegrationClients>();
        return services;
    }

    /// <summary>
    /// The token exchange and the per-call check (Ariva.Api.Integration). The scheme and its key ring come from
    /// AddArivaIntegrationAuthentication in Ariva.Api.Common.
    /// </summary>
    public static IServiceCollection AddArivaIntegrationAuth(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<Ariva.Infra.Services.Administration.AuditTrail>();
        services.TryAddSingleton<Ariva.Infra.Services.Integration.IntegrationMetrics>();
        services.TryAddScoped<ISvcIntegrationAuth, Ariva.Infra.Services.Integration.SvcIntegrationAuth>();
        return services;
    }

    /// <summary>
    /// Idempotency of the Integration API's batch endpoints (ARV-043) and the sweep of expired keys (Ariva.Api.Integration).
    /// The intake itself comes from AddArivaFlights.
    /// </summary>
    public static IServiceCollection AddArivaIntegrationBatches(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<ISvcIntegrationIdempotency, Ariva.Infra.Services.Integration.SvcIntegrationIdempotency>();
        services.AddHostedService<Ariva.Infra.Integration.IdempotencySweeper>();
        return services;
    }

    /// <summary>Administration of outbound endpoints (ARV-045, Ariva.Api.Main): the registry, its rules and the protected secrets.</summary>
    public static IServiceCollection AddArivaOutboundEndpoints(this IServiceCollection services, IConfiguration configuration, string hostEnvironment)
    {
        ArgumentNullException.ThrowIfNull(services);
        AddOutboundSettings(services, configuration, hostEnvironment);
        services.TryAddScoped<Ariva.Infra.Services.Administration.AuditTrail>();
        services.TryAddScoped<ISvcOutboundEndpoints, Ariva.Infra.Services.Integration.SvcOutboundEndpoints>();
        return services;
    }

    /// <summary>
    /// Outbound calls (ARV-045, Ariva.Api.Integration): the guarded clients of the registered endpoints and, unless
    /// <paramref name="pollAcris"/> is false, the ACRIS flight pull.
    /// </summary>
    public static IServiceCollection AddArivaOutboundCalls(this IServiceCollection services, IConfiguration configuration, string hostEnvironment, bool pollAcris = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        AddOutboundSettings(services, configuration, hostEnvironment);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<Ariva.Infra.Integration.Outbound.IOutboundResolver, Ariva.Infra.Integration.Outbound.DnsOutboundResolver>();
        services.TryAddSingleton<Ariva.Infra.Integration.Outbound.OutboundClients>();
        services.TryAddScoped<Ariva.Infra.Services.Flights.SvcAcrisPull>();
        if (pollAcris)
            services.AddHostedService<Ariva.Infra.Flights.Acris.AcrisPoller>();
        return services;
    }

    private static void AddOutboundSettings(IServiceCollection services, IConfiguration configuration, string hostEnvironment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var settings = configuration.GetSection(Ariva.Infra.Integration.OutboundSettings.SectionName).Get<Ariva.Infra.Integration.OutboundSettings>() ?? new();
        if (settings.Problem(hostEnvironment, configuration["Application:Environment"]) is { } problem)
            throw new InvalidOperationException(problem);
        services.TryAddSingleton(settings);
        services.TryAddSingleton<Ariva.Infra.Integration.OutboundSecrets>();
    }
}
