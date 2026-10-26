using Ariva.Core.Services;
using Ariva.Infra.NHibernate;
using Ariva.Infra.Services.Foundation;
using Ariva.Infra.Settings;
using Ariva.Infra.Timescale;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

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
        EnsureProductionTransport(settings, configuration);
        // ARV-096d: a command or statement timeout out of range stops the host (and the migration job, which builds it) at start.
        settings.EnsureValidLimits();
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
        // ARV-180: the environment the migration job records in the database, and a vm-local host checks before it migrates or seeds.
        var environment = DatabaseEnvironment.FromConfiguration(configuration);
        services.AddSingleton(environment);

        // ARV-180: SchemaUpdate on a developer machine only. A test enforces it on the committed files; this refuses a process
        // of a named environment other than vm-local that is given Database__AllowSchemaUpdate (the integration tests' own
        // processes name none).
        if (settings.AllowSchemaUpdate && environment.Name is not null && !environment.IsVmLocal)
            throw new InvalidOperationException($"Database:AllowSchemaUpdate is only allowed in vm-local; this process runs as '{environment.Name}' (ARV-180).");
        // ARV-180: a vm-local host that writes to its database at start (migration, development accounts, demo topology) checks
        // the database's environment before any hosted service starts (the guard's StartingAsync), and the hosted services
        // start one after another whatever the configuration says (servicesStartConcurrently), as the guards here rely on.
        services.PostConfigure<HostOptions>(options => options.ServicesStartConcurrently = false);
        if (environment.IsVmLocal && (settings.AllowSchemaUpdate || SeedsDevelopmentAccounts(configuration) || SeedsDemoTopology(configuration)))
            services.AddHostedService<DevelopmentDatabaseGuard>();
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
    /// Whether the host is configured with development accounts (Auth:DevelopmentUsers), which Ariva.Api.Main seeds at start in
    /// vm-local (ARV-180). Read here, before the accounts are registered, so that the guard starts before the seed.
    /// </summary>
    internal static bool SeedsDevelopmentAccounts(IConfiguration configuration) =>
        configuration.GetSection("Auth:DevelopmentUsers").GetChildren().Any();

    /// <summary>Whether the host seeds the demo topology at start (Seed:DemoTopology, Ariva.Api.Main), as <see cref="DemoSeedExtensions"/> reads it.</summary>
    internal static bool SeedsDemoTopology(IConfiguration configuration) =>
        configuration.GetValue<bool>(DemoSeedExtensions.SettingName);

    /// <summary>
    /// ARV-082a (CWE-319, CWE-295): in production every connection to PostgreSQL uses TLS verified against the pinned internal
    /// CA, or the host (and the migration job, which builds it) does not start.
    /// </summary>
    internal static void EnsureProductionTransport(DatabaseSettings settings, IConfiguration configuration)
    {
        if (configuration["Application:Environment"] == "k8s-prd" && settings.ProductionTransportProblem(File.Exists) is { } problem)
            throw new InvalidOperationException(problem);
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
