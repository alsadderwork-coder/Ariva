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

    /// <summary>
    /// The arrival-wave projection (ARV-047, Ariva.Api.Main): its settings (<c>Flights:ArrivalWave</c>, checked at start)
    /// and the service.
    /// </summary>
    public static IServiceCollection AddArivaArrivalWave(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddArivaArrivalWaveSource(configuration);
        services.TryAddScoped<Ariva.Infra.Services.Administration.CallerRoles>();
        services.TryAddScoped<ISvcArrivalWave, Ariva.Infra.Services.Flights.SvcArrivalWave>();
        // ARV-055: the live screen's desk states, scoped like the arrival wave.
        services.TryAddScoped<Ariva.Core.Services.Live.ISvcDeskStates, Ariva.Infra.Services.Live.SvcDeskStates>();
        return services;
    }

    /// <summary>
    /// The arrival wave that predicted-breach rules read (ARV-038, ARV-047; Ariva.Api.Stream's evaluation and Ariva.Api.Main's
    /// backtest): the settings (<c>Flights:ArrivalWave</c> and <c>Border:EgateCoupling</c>, checked at start) and
    /// <see cref="ProjectedArrivalWave"/>.
    /// </summary>
    public static IServiceCollection AddArivaArrivalWaveSource(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var settings = configuration.GetSection(Ariva.Core.Flights.ArrivalWaveSettings.SectionName).Get<Ariva.Core.Flights.ArrivalWaveSettings>() ??
                       new Ariva.Core.Flights.ArrivalWaveSettings();
        var problems = settings.Problems().ToList();
        if (problems.Count > 0)
            throw new InvalidOperationException(string.Join(" ", problems));
        services.TryAddSingleton(settings);
        var coupling = configuration.GetSection(Ariva.Core.Border.EgateCouplingSettings.SectionName).Get<Ariva.Core.Border.EgateCouplingSettings>() ??
                       new Ariva.Core.Border.EgateCouplingSettings();
        var couplingProblems = coupling.Problems().ToList();
        if (couplingProblems.Count > 0)
            throw new InvalidOperationException(string.Join(" ", couplingProblems));
        services.TryAddSingleton(coupling);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<Ariva.Core.Alerting.IArrivalWaveSource, ProjectedArrivalWave>();
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
