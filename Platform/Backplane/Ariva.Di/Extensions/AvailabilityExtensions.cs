using Ariva.Core.Availability;
using Ariva.Infra.Availability;
using Ariva.Infra.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ariva.Di.Extensions;

/// <summary>
/// The availability ledger (ARV-118): its settings (<c>Availability</c>, checked at start) and the ledger Ariva.Api.Cronz runs every
/// minute, under its own database login (ARV-083a, Database:Availability): a cluster's Cronz host refuses to start without it,
/// or with one that is its own runtime login, the migration login or not a login of its own.
/// </summary>
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
        EnsureLedgerLogin(configuration);
        services.TryAddSingleton(settings);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(_ => AvailabilityLoginSettings.FromConfiguration(configuration));
        services.TryAddSingleton<AvailabilityLedger>();
        return services;
    }

    /// <summary>
    /// ARV-083a (CWE-269): the ledger's own login, required in a cluster and checked wherever it is given; in a cluster it is
    /// never the committed runtime login name either, whose password every pod mounts.
    /// </summary>
    internal static void EnsureLedgerLogin(IConfiguration configuration)
    {
        var login = AvailabilityLoginSettings.FromConfiguration(configuration);
        var cluster = configuration["Application:Environment"]?.StartsWith("k8s-", StringComparison.Ordinal) == true;
        if (!login.IsConfigured && cluster)
            throw new InvalidOperationException($"{AvailabilityLoginSettings.SectionName}:Username is not set: in a cluster the availability ledger records its minutes with a login of its own (ARV-083a, the chart's databaseLogins.availability secret).");
        login.EnsureValid(DatabaseSettings.FromConfiguration(configuration), cluster);
    }
}
