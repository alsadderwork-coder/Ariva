using Ariva.Infra.Services.Seed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.Di.Extensions;

/// <summary>
/// The demo topology seeds (DMO, ARV-019; the illustrative AUH-TA, ARV-139a) for Ariva.Api.Main: on with <c>Seed:DemoTopology</c> (vm-local in appsettings,
/// k8s-demo through the Helm value <c>demoSeed</c>). Fictional data must never reach a real deployment (CWE-269), so
/// the setting is refused unless both the host environment (DOTNET_ENVIRONMENT, which Helm sets) and
/// <c>Application:Environment</c> are development or demo environments; a production host with it on refuses to start.
/// Checking both covers a value injected through an environment variable on either side (since ARV-098 neither has a
/// vm-local default: the host refuses to start without a known environment, and the base settings name none).
/// <para>
/// The development-only NBJ-BC1 site (ARV-139c, <see cref="NbjSettingName"/>) is narrower still: it is derived from a third
/// party's confidential drawings (CWE-200), so its code is compiled only in Debug builds (<c>ARIVA_DEV_SEED</c>; Release
/// builds and images never carry it), no committed setting turns it on, and a host with it on refuses to start unless it
/// is a Debug build whose host environment and <c>Application:Environment</c> are both vm-local, outside a Codespace or a
/// cloud agent session (docs/demo/nbj-bc1.md). The refusal messages name the setting but no site, path or document: they
/// are all a Release Ariva.Di.dll carries of it (NbjSiteScopeTests).
/// </para>
/// </summary>
public static class DemoSeedExtensions
{
    #region Settings

    public const string SettingName = "Seed:DemoTopology";

    /// <summary>
    /// Opt-in for the NBJ BC1 site (ARV-139c), on top of <see cref="SettingName"/>, on the owner's machine only: set it
    /// for one local run of Ariva.Api.Main (the environment variable <c>Seed__NbjSite=true</c>); no committed settings
    /// file, chart, AppHost, E2E run or script sets it (NbjSiteScopeTests).
    /// </summary>
    public const string NbjSettingName = "Seed:NbjSite";

    /// <summary>The only environment, for the host and for <c>Application:Environment</c> both, where <see cref="NbjSettingName"/> may be on.</summary>
    public const string NbjEnvironment = "vm-local";

    /// <summary>
    /// Variables that mark a shared or hosted development machine, where <see cref="NbjSettingName"/> is refused although the
    /// host runs as vm-local in a Debug build: GitHub Codespaces sets <c>CODESPACES=true</c>, a cloud agent session
    /// <c>CLAUDE_CODE_REMOTE=true</c> (hosts read both through their environment variables configuration).
    /// </summary>
    public static readonly IReadOnlyList<string> HostedMachineMarkers = ["CODESPACES", "CLAUDE_CODE_REMOTE"];

    /// <summary>Where the demo seed may run; everything else, k8s-prd above all, is refused.</summary>
    public static readonly IReadOnlySet<string> AllowedEnvironments =
        new HashSet<string>(["vm-local", "k8s-dev", "k8s-demo"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether this build carries the NBJ-BC1 seed: Debug builds only (<c>ARIVA_DEV_SEED</c>), never Release builds or images.</summary>
    public static bool NbjSeedCompiled { get; } =
#if ARIVA_DEV_SEED
        true;
#else
        false;
#endif

    #endregion

    #region Registration

    public static IServiceCollection AddArivaDemoSeed(this IServiceCollection services, IConfiguration configuration, string hostEnvironment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var applicationEnvironment = configuration["Application:Environment"];

        // Before anything else, so that the setting is refused even where the demo seed is off: a shared deployment or a
        // Release build with it on stops at start-up instead of silently ignoring it.
        var nbj = configuration.GetValue<bool>(NbjSettingName);
        if (nbj)
            EnsureNbjAllowed(configuration, hostEnvironment, applicationEnvironment);

        if (!configuration.GetValue<bool>(SettingName))
            return services;
        if (!AllowedEnvironments.Contains(hostEnvironment ?? string.Empty) || !AllowedEnvironments.Contains(applicationEnvironment ?? string.Empty))
        {
            throw new InvalidOperationException(
                $"{SettingName} is for development and demo deployments only ({string.Join(", ", AllowedEnvironments)}); " +
                $"this host runs as '{hostEnvironment}' with Application:Environment '{applicationEnvironment}'. Turn it off.");
        }

        // In this order: the fictional DMO (ARV-019), then the illustrative AUH Terminal A arrivals (ARV-139a), then,
        // opt-in on the owner's machine only, NBJ terminal BC1 (ARV-139c).
        services.AddScoped<IDemoTopologySeed, DemoTopologySeed>();
        services.AddScoped<IDemoTopologySeed, AuhTerminalASeed>();
#if ARIVA_DEV_SEED
        if (nbj)
            services.AddScoped<IDemoTopologySeed, NbjBc1Seed>();
#endif
        services.AddHostedService<DemoSeedService>();
        return services;
    }

    /// <summary>
    /// <see cref="NbjSettingName"/> on: only on the owner's own developer machine (host environment and
    /// <c>Application:Environment</c> both vm-local, no Codespace or cloud agent session) and only in a Debug build, which
    /// alone carries the seed; anything else refuses to start (CWE-200). No site code, name, path or document in these
    /// messages: they are all a Release build carries of the seed (NbjSiteScopeTests).
    /// </summary>
    private static void EnsureNbjAllowed(IConfiguration configuration, string hostEnvironment, string applicationEnvironment)
    {
        if (!string.Equals(hostEnvironment, NbjEnvironment, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(applicationEnvironment, NbjEnvironment, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{NbjSettingName} is for a developer machine only ({NbjEnvironment}); this host runs as '{hostEnvironment}' with " +
                $"Application:Environment '{applicationEnvironment}'. Turn it off.");
        }

        if (!NbjSeedCompiled)
        {
            throw new InvalidOperationException(
                $"{NbjSettingName} is on, but this is not a Debug build: that development-only seed is compiled only in Debug builds " +
                "(ARIVA_DEV_SEED), never in Release builds or images. Turn it off, or run a Debug build on a developer machine.");
        }

        var marker = HostedMachineMarkers.FirstOrDefault(name => string.Equals(configuration[name]?.Trim(), "true", StringComparison.OrdinalIgnoreCase));
        if (marker is not null)
        {
            throw new InvalidOperationException(
                $"{NbjSettingName} is for the owner's own machine only, not a Codespace or a cloud agent session ({marker} is true). Turn it off.");
        }
    }

    #endregion
}
