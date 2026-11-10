using Ariva.Infra.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ariva.Di.Extensions;

/// <summary>
/// The host-wide metrics the system alerts read (ARV-125a): the readiness checks as <c>ariva.health.status</c>
/// (<see cref="HealthStatusMetrics"/>, the host's health check publisher, every 15 seconds over the checks tagged
/// <c>ready</c>), the expiry of the certificates the host loaded (<see cref="CertificateMetrics"/>) and the age of its keys
/// (<see cref="KeyAgeMetrics"/>, ARV-207). The other instruments
/// are registered with what they measure. Every meter is exported by the host's telemetry (TelemetryExtensions.Meters).
/// </summary>
public static class MonitoringExtensions
{
    public static IServiceCollection AddArivaMonitoring(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(d => d.ServiceType == typeof(HealthStatusMetrics)))
            return services;

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<HealthStatusMetrics>();
        services.AddSingleton<IHealthCheckPublisher>(provider => provider.GetRequiredService<HealthStatusMetrics>());
        services.Configure<HealthCheckPublisherOptions>(options =>
        {
            options.Delay = TimeSpan.FromSeconds(5);
            options.Period = HealthStatusMetrics.Period;
            // Each check has its own bound (at most 5 s); this one only ends a run that ignores them.
            options.Timeout = TimeSpan.FromSeconds(10);
            options.Predicate = registration => registration.Tags.Contains(HealthExtensions.ReadyTag);
        });
        services.TryAddSingleton<CertificateMetrics>();
        services.AddHostedService(provider => provider.GetRequiredService<CertificateMetrics>());
        // ARV-207: the age of the keys the host loaded and, in Ariva.Api.Cronz, of the key ring and the dated credentials.
        services.TryAddSingleton<KeyAgeMetrics>();
        services.AddHostedService(provider => provider.GetRequiredService<KeyAgeMetrics>());
        return services;
    }
}
