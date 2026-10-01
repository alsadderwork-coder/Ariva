using Ariva.Di.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.Di;

/// <summary>
/// Composition root for Ariva services. Every host calls <see cref="RegisterArivaServices"/> once.
/// Registrations (storage provider, unit of work, caching, message bus, services) are added by the backlog,
/// grouped into extension methods under <c>Extensions/</c> and called from here.
/// </summary>
public static class DependencyRegister
{
    /// <summary>
    /// Registers the Ariva application services shared by all hosts.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configuration">The host's layered configuration (base, base per environment, service, service per environment).</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection RegisterArivaServices(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddArivaPersistence(configuration);
        services.AddArivaCaching(configuration);
        services.AddArivaDataProtection(configuration);

        return services;
    }
}
