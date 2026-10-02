using Ariva.Infra.Services.Seed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.Di.Extensions;

/// <summary>
/// The demo topology seed (ARV-019) for Ariva.Api.Main: on with <c>Seed:DemoTopology</c> (vm-local in appsettings,
/// k8s-demo through the Helm value <c>demoSeed</c>). Fictional data must never reach a real deployment (CWE-269), so
/// the setting is refused unless both the host environment (DOTNET_ENVIRONMENT, which Helm sets) and
/// <c>Application:Environment</c> are development or demo environments; a production host with it on refuses to start.
/// Checking both covers a cluster whose mounted settings file omits <c>Application:Environment</c> (it then defaults to
/// vm-local) and a setting injected through an environment variable.
/// </summary>
public static class DemoSeedExtensions
{
    public const string SettingName = "Seed:DemoTopology";

    /// <summary>Where the demo seed may run; everything else, k8s-prd above all, is refused.</summary>
    public static readonly IReadOnlySet<string> AllowedEnvironments =
        new HashSet<string>(["vm-local", "k8s-dev", "k8s-demo"], StringComparer.OrdinalIgnoreCase);

    public static IServiceCollection AddArivaDemoSeed(this IServiceCollection services, IConfiguration configuration, string hostEnvironment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!configuration.GetValue<bool>(SettingName))
            return services;
        var applicationEnvironment = configuration["Application:Environment"];
        if (!AllowedEnvironments.Contains(hostEnvironment ?? string.Empty) || !AllowedEnvironments.Contains(applicationEnvironment ?? string.Empty))
        {
            throw new InvalidOperationException(
                $"{SettingName} is for development and demo deployments only ({string.Join(", ", AllowedEnvironments)}); " +
                $"this host runs as '{hostEnvironment}' with Application:Environment '{applicationEnvironment}'. Turn it off.");
        }

        services.AddScoped<IDemoTopologySeed, DemoTopologySeed>();
        services.AddHostedService<DemoSeedService>();
        return services;
    }
}
