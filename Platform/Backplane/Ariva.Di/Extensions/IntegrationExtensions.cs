using Ariva.Core.Services.Integration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ariva.Di.Extensions;

/// <summary>Integration clients (ARV-042).</summary>
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
}
