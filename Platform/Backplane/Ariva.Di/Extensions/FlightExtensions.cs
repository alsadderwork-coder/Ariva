using Ariva.Core.Services.Flights;
using Ariva.Infra.Flights;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ariva.Di.Extensions;

/// <summary>The flight model's intake and the stale-feed alarm (ARV-041), in Ariva.Api.Integration where the flight feeds arrive.</summary>
public static class FlightExtensions
{
    public static IServiceCollection AddArivaFlights(this IServiceCollection services, IConfiguration configuration, bool watchFeeds = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var settings = configuration.GetSection(FlightFeedSettings.SectionName).Get<FlightFeedSettings>() ?? new FlightFeedSettings();
        var problems = settings.Problems().ToList();
        if (problems.Count > 0)
            throw new InvalidOperationException(string.Join(" ", problems));
        services.TryAddSingleton(settings);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<FlightMetrics>();
        services.TryAddScoped<ISvcFlightIntake, Ariva.Infra.Services.Flights.SvcFlightIntake>();
        services.TryAddScoped<ISvcAidxIntake, Ariva.Infra.Services.Flights.SvcAidxIntake>();
        services.TryAddScoped<ISvcFeedFreshness, Ariva.Infra.Services.Flights.SvcFeedFreshness>();
        if (watchFeeds)
            services.AddHostedService<FeedFreshnessMonitor>();
        return services;
    }

    /// <summary>SSIM schedule imports (ARV-046, Ariva.Api.Main): the flight intake without the freshness sweep, and the import service.</summary>
    public static IServiceCollection AddArivaFlightSchedules(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddArivaFlights(configuration, watchFeeds: false);
        services.TryAddScoped<Ariva.Infra.Services.Administration.AuditTrail>();
        services.TryAddScoped<ISvcFlightSchedules, Ariva.Infra.Services.Flights.SvcFlightSchedules>();
        return services;
    }
}
