using Ariva.Core.Availability;
using Ariva.Infra.Availability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ariva.Di.Extensions;

/// <summary>The availability ledger (ARV-118): its settings (<c>Availability</c>, checked at start) and the ledger Ariva.Api.Cronz runs every minute.</summary>
public static class AvailabilityExtensions
{
    public static IServiceCollection AddArivaAvailabilityLedger(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var settings = configuration.GetSection(AvailabilitySettings.SectionName).Get<AvailabilitySettings>() ?? new AvailabilitySettings();
        var problems = settings.Problems().ToList();
        if (problems.Count > 0)
            throw new InvalidOperationException(string.Join(" ", problems));
        services.TryAddSingleton(settings);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<AvailabilityLedger>();
        return services;
    }
}
