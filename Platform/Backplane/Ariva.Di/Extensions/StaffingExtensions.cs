using Ariva.Core.Services.Border;
using Ariva.Core.Staffing;
using Ariva.Infra.Staffing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ariva.Di.Extensions;

/// <summary>
/// The indicative staffing recommendation (ARV-181a): its settings (<c>Staffing</c>, with <c>Flights:ArrivalWave</c> and
/// <c>Border:EgateCoupling</c>, checked at start), the job Ariva.Api.Cronz runs every minute, and the service Ariva.Api.Main
/// answers with.
/// </summary>
public static class StaffingExtensions
{
    /// <summary>Ariva.Api.Cronz: the inputs reader and the runner that writes <c>lane_staffing</c>.</summary>
    public static IServiceCollection AddArivaStaffingJob(this IServiceCollection services, IConfiguration configuration)
    {
        AddSettings(services, configuration);
        services.TryAddScoped<StaffingInputs>();
        services.TryAddSingleton<StaffingRecommendations>();
        return services;
    }

    /// <summary>Ariva.Api.Main: the service that answers the stored recommendation.</summary>
    public static IServiceCollection AddArivaStaffingRecommendations(this IServiceCollection services, IConfiguration configuration)
    {
        AddSettings(services, configuration);
        services.TryAddScoped<Ariva.Infra.Services.Administration.CallerRoles>();
        services.TryAddScoped<ISvcStaffingRecommendations, Ariva.Infra.Services.Border.SvcStaffingRecommendations>();
        return services;
    }

    private static void AddSettings(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var settings = configuration.GetSection(StaffingSettings.SectionName).Get<StaffingSettings>() ?? new StaffingSettings();
        var problems = settings.Problems().ToList();
        if (problems.Count > 0)
            throw new InvalidOperationException(string.Join(" ", problems));
        services.TryAddSingleton(settings);
        // The arrival wave's settings and e-gate coupling, checked at start (ARV-047, ARV-049).
        services.AddArivaArrivalWaveSource(configuration);
        services.TryAddSingleton(TimeProvider.System);
    }
}
