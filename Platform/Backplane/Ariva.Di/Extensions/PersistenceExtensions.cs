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
/// per scope. Fallbacks for the current user (ARV-010a) and the outbox are registered with TryAdd; AddArivaMessaging replaces
/// the outbox with the NHibernate one (ARV-020).
/// </summary>
public static class PersistenceExtensions
{
    public static IServiceCollection AddArivaPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var settings = DatabaseSettings.FromConfiguration(configuration);
        services.AddSingleton(settings);
        // ARV-104g1: the validation reader login (Database:ValidationReader), apart from the runtime login. Configured in the
        // clusters for api-main and the migration job only; held by the validation service, the migrator and the guard.
        services.AddSingleton(ValidationReaderSettings.FromConfiguration(configuration));
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
        // After the two above (hosted services start in order): a host refuses to start when the validation reader login is
        // misconfigured next to its own, or when its runtime login holds ariva_validation_reader (ARV-104g1, CWE-269).
        services.AddHostedService<ValidationReaderGuard>();

        return services;
    }

    /// <summary>
    /// ARV-104g1 (CWE-863): declares this host the one that runs the validation service (Ariva.Api.Main), the only host the
    /// start-up guard lets hold the validation reader login (Database:ValidationReader) outside vm-local.
    /// </summary>
    public static IServiceCollection AddArivaValidationReaderHost(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ValidationReaderHost>();
        return services;
    }
}
