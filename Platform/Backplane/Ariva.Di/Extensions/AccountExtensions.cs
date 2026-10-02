using Ariva.Core.Security;
using Ariva.Core.Services.Administration;
using Ariva.Core.Services.Security;
using Ariva.Infra.Security;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Security;
using Ariva.Infra.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ariva.Di.Extensions;

/// <summary>
/// Accounts and access tokens (ADR-0026, ARV-010a). Every host validates tokens and resolves permissions; only
/// Ariva.Api.Main signs tokens and signs users in (<see cref="AddArivaTokenIssuing"/>).
/// </summary>
public static class AccountExtensions
{
    /// <summary>Settings, validation keys, password policy, the session check, the stored permission resolver and the site scope; every API host.</summary>
    public static IServiceCollection AddArivaAccounts(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var settings = AuthSettings.From(configuration);
        services.TryAddSingleton(settings);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(_ => TokenKeys.Load(settings.Tokens, requireSigningKey: false));
        services.TryAddSingleton(_ => new PasswordPolicy(settings.ContextWords.Append(configuration["Application:SiteCode"])));
        services.TryAddScoped<IPermissionResolver, StoredPermissionResolver>();
        services.TryAddScoped<ISessionValidator, SessionValidator>();
        services.TryAddScoped<SiteAccessResolver>();
        services.TryAddScoped<ISiteScope, SiteScope>();
        return services;
    }

    /// <summary>
    /// Ariva.Api.Main only: the signing key, the token issuer, the sign-in service and user administration; on vm-local also the development
    /// accounts from Auth:DevelopmentUsers.
    /// </summary>
    public static IServiceCollection AddArivaTokenIssuing(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddArivaAccounts(configuration);
        var settings = AuthSettings.From(configuration);
        services.Replace(ServiceDescriptor.Singleton(_ => TokenKeys.Load(settings.Tokens, requireSigningKey: true)));
        services.TryAddSingleton<AccessTokenIssuer>();
        services.TryAddScoped<AccountSessions>();
        services.TryAddScoped<AuditTrail>();
        services.TryAddScoped<CallerRoles>();
        services.TryAddScoped<AdministrationGuards>();
        services.TryAddScoped<ISvcAuthenticator, SvcAuthenticator>();

        // User, role and audit administration (ARV-011): Ariva.Api.Main only, like sign-in.
        services.TryAddScoped<ISvcUsers, SvcUsers>();
        services.TryAddScoped<ISvcRoleAssignment, SvcRoleAssignment>();
        services.TryAddScoped<ISvcAuditEntries, SvcAuditEntries>();
        services.TryAddScoped<ISvcSites, SvcSites>();
        services.TryAddScoped<Ariva.Core.Services.Topology.ISvcTopology, Ariva.Infra.Services.Topology.SvcTopology>();
        services.TryAddScoped<Ariva.Core.Services.Topology.ISvcDeskCodeMappings, Ariva.Infra.Services.Topology.SvcDeskCodeMappings>();
        services.TryAddSingleton<Ariva.Core.Services.Storage.IFileStorage, Ariva.Infra.Storage.LocalDiskFileStorage>();
        services.TryAddScoped<Ariva.Core.Services.Topology.ISvcFloorPlans, Ariva.Infra.Services.Topology.SvcFloorPlans>();
        services.TryAddScoped<Ariva.Core.Services.Topology.ISvcZoneProfiles, Ariva.Infra.Services.Topology.SvcZoneProfiles>();
        services.TryAddScoped<Ariva.Core.Services.Sensing.ISvcDevices, Ariva.Infra.Services.Sensing.SvcDevices>();
        // The declarative device mappings shipped with Ariva (ARV-024); the registry checks names against them.
        services.TryAddSingleton<Ariva.Core.Services.Sensing.IDeviceMappingCatalog>(Ariva.Infra.Sensing.Declarative.DeclarativeMappingCatalog.Embedded);
        services.TryAddScoped<Ariva.Core.Services.Sensing.ISvcDeviceGateway, Ariva.Infra.Services.Sensing.SvcDeviceGateway>();
        // Device heartbeats and zone degradation (ARV-025): reads for the health API, recording for the consumer; the
        // heartbeat monitor itself runs only in Ariva.Api.Main (AddArivaDeviceHealthMonitor).
        services.AddOptions<Ariva.Infra.Sensing.DeviceHealthSettings>()
            .Bind(configuration.GetSection(Ariva.Infra.Sensing.DeviceHealthSettings.SectionName))
            .Validate(s => s.IsValid, "Devices:Health:HeartbeatTimeoutSeconds is from 30 to 3600 and SweepSeconds from 5 to 300, below the timeout.")
            .ValidateOnStart();
        services.TryAddSingleton<Ariva.Infra.Sensing.DeviceHealthMetrics>();
        services.TryAddScoped<Ariva.Core.Services.Sensing.ISvcDeviceHealth, Ariva.Infra.Services.Sensing.SvcDeviceHealth>();
        // Alert rules (ARV-037): Ariva.Api.Main only, like the rest of the administration.
        services.TryAddScoped<Ariva.Core.Services.Alerting.ISvcAlertRules, Ariva.Infra.Services.Alerting.SvcAlertRules>();
        // ARV-038: the stored minutes rules are judged on, and the arrival wave (none before ARV-047).
        services.TryAddSingleton<Ariva.Core.Alerting.IArrivalWaveSource, Ariva.Core.Alerting.NoArrivalWave>();
        services.TryAddScoped<Ariva.Infra.Alerting.AlertInputs>();
        services.TryAddScoped<Ariva.Infra.Alerting.AlertRuleTick>();

        var environment = configuration["Application:Environment"];
        if (settings.DevelopmentUsers.Count > 0)
        {
            if (!string.Equals(environment, "vm-local", StringComparison.Ordinal))
                throw new InvalidOperationException("Auth:DevelopmentUsers is only allowed in vm-local.");
            services.AddHostedService<DevelopmentUserSeed>();
        }

        return services;
    }
}
