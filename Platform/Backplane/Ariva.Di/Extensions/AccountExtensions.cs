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
    /// <summary>
    /// Settings, validation keys, password policy, the session check, the stored permission resolver and the site scope;
    /// every API host and the break-glass command. Each of them refuses development accounts and sign-in without TOTP
    /// outside vm-local (<see cref="LocalOnlyProblem"/>): Ariva.Api.Cronz, for one, reads Auth:TotpRequired when it
    /// chooses report recipients.
    /// </summary>
    /// <param name="hostEnvironment">The host's own environment name (<c>IHostEnvironment.EnvironmentName</c>), never a configuration key.</param>
    public static IServiceCollection AddArivaAccounts(this IServiceCollection services, IConfiguration configuration, string hostEnvironment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var settings = AuthSettings.From(configuration);
        if (LocalOnlyProblem(settings, configuration, hostEnvironment) is { } problem)
            throw new InvalidOperationException(problem);
        services.TryAddSingleton(settings);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(_ => TokenKeys.Load(settings.Tokens, requireSigningKey: false));
        services.TryAddSingleton(_ => new PasswordPolicy(settings.ContextWords.Append(configuration["Application:SiteCode"])));
        services.TryAddScoped<IPermissionResolver, StoredPermissionResolver>();
        services.TryAddScoped<ISessionValidator, SessionValidator>();
        services.TryAddScoped<SiteAccessResolver>();
        services.TryAddScoped<UserRoles>();
        services.TryAddScoped<ISiteScope, SiteScope>();
        return services;
    }

    /// <summary>
    /// Ariva.Api.Main only: the signing key, the token issuer, the sign-in service and user administration; on vm-local also the development
    /// accounts from Auth:DevelopmentUsers.
    /// </summary>
    public static IServiceCollection AddArivaTokenIssuing(this IServiceCollection services, IConfiguration configuration, string hostEnvironment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddArivaAccounts(configuration, hostEnvironment);
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
        services.TryAddScoped<Ariva.Core.Services.Displays.ISvcDisplays, Ariva.Infra.Services.Displays.SvcDisplays>();
        services.TryAddScoped<Ariva.Core.Services.Displays.ISvcDisplayBoard, Ariva.Infra.Services.Displays.SvcDisplayBoard>();
        // Reports (ARV-060): the daily report and its schedules; deliveries run in Ariva.Api.Cronz (AddArivaReportDeliveries).
        services.TryAddScoped<Ariva.Infra.Services.Reports.ReportReader>();
        services.TryAddScoped<Ariva.Core.Services.Reports.ISvcReports, Ariva.Infra.Services.Reports.SvcReports>();
        services.TryAddScoped<Ariva.Core.Services.Reports.ISvcReportSchedules, Ariva.Infra.Services.Reports.SvcReportSchedules>();
        // ARV-038: the stored minutes rules are judged on, and the arrival wave (ARV-047).
        services.AddArivaArrivalWaveSource(configuration);
        services.TryAddScoped<Ariva.Infra.Alerting.AlertInputs>();
        services.TryAddScoped<Ariva.Infra.Alerting.AlertRuleTick>();
        // Alert emails are written with the alert change (ARV-040).
        services.AddArivaEmailOutbox(configuration);
        // Alerts (ARV-039); their notices come from AddArivaCaching (Redis, or nothing without it).
        services.TryAddScoped<Ariva.Core.Services.Alerting.ISvcAlerts, Ariva.Infra.Services.Alerting.SvcAlerts>();

        // AddArivaAccounts above has refused development accounts outside vm-local; only Main creates them.
        if (settings.DevelopmentUsers.Count > 0)
            services.AddHostedService<DevelopmentUserSeed>();

        return services;
    }

    /// <summary>
    /// Development accounts and sign-in without TOTP are for a developer machine only (CWE-287, CWE-308): both need
    /// <c>Application:Environment</c> vm-local and the host environment (<c>ArivaEnvironment.Resolve</c>: the command line,
    /// DOTNET_ENVIRONMENT, which Helm sets, ASPNETCORE_ENVIRONMENT, then environment.json) vm-local.
    /// Checking both covers a cluster whose mounted settings omit <c>Application:Environment</c> (it then defaults to
    /// vm-local) and a value injected through an environment variable. The host environment is the host's own name,
    /// passed in, not the <c>environment</c> configuration key, which an unprefixed ENVIRONMENT variable could override.
    /// Null when the settings are allowed, otherwise why not.
    /// </summary>
    public static string LocalOnlyProblem(AuthSettings settings, IConfiguration configuration, string hostEnvironment)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(configuration);
        var wanted = new List<string>();
        if (settings.DevelopmentUsers.Count > 0)
            wanted.Add("Auth:DevelopmentUsers");
        if (!settings.TotpRequired)
            wanted.Add("Auth:TotpRequired false");
        if (wanted.Count == 0)
            return null;
        var application = configuration["Application:Environment"];
        return string.Equals(application, "vm-local", StringComparison.Ordinal) && string.Equals(hostEnvironment, "vm-local", StringComparison.Ordinal)
            ? null
            : $"{string.Join(" and ", wanted)} only allowed in vm-local; this host runs as '{hostEnvironment}' with Application:Environment '{application}'.";
    }
}
