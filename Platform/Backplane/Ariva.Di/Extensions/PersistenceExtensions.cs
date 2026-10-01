using Ariva.Core.Services;
using Ariva.Infra.NHibernate;
using Ariva.Infra.Services.Foundation;
using Ariva.Infra.Settings;
using Ariva.Infra.Timescale;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ariva.Di.Extensions;

/// <summary>
/// NHibernate persistence (ARV-005) and the versioned schema (ARV-006): settings and session factory as singletons, storage provider and unit of work
/// per scope. Fallbacks for the current user (ARV-010a) and the outbox (ARV-020) are registered with TryAdd so the
/// real implementations replace them when they are registered first.
/// </summary>
public static class PersistenceExtensions
{
    public static IServiceCollection AddArivaPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var settings = DatabaseSettings.FromConfiguration(configuration);
        services.AddSingleton(settings);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(provider => new NHibernateSessionFactoryProvider(provider.GetRequiredService<DatabaseSettings>()));

        services.TryAddScoped<ICurrentUser, AnonymousCurrentUser>();
        services.TryAddSingleton<IDomainEventOutbox, MissingDomainEventOutbox>();

        services.AddScoped<IStorageProvider, NHibernateStorageProvider>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddSingleton<DatabaseMigrator>();

        // vm-local migrates at startup; every other environment only checks, the migration job changes the schema.
        if (settings.AllowSchemaUpdate)
            services.AddHostedService<DevelopmentMigrationService>();
        if (settings.VerifySchemaOnStartup)
            services.AddHostedService<SchemaVersionGate>();

        return services;
    }
}
