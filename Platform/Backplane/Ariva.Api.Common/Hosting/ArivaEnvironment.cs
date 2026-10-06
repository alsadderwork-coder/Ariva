using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Ariva.Api.Common.Hosting;

/// <summary>
/// Resolves the Ariva environment (vm-local, k8s-dev, k8s-demo, k8s-prd) once, before the host builder is created,
/// so the configuration files, <see cref="IHostEnvironment.EnvironmentName"/> and every environment check in the
/// security baseline agree. AMAN does the same in <c>Util.GetOrCreateEnvironment()</c>; that port replaces this class.
/// </summary>
public static class ArivaEnvironment
{
    #region Constants

    /// <summary>Developer machine. The only environment that may show exception details in error responses.</summary>
    public const string VmLocal = "vm-local";

    /// <summary>Kubernetes development cluster.</summary>
    public const string K8sDev = "k8s-dev";

    /// <summary>Kubernetes demonstration cluster.</summary>
    public const string K8sDemo = "k8s-demo";

    /// <summary>Kubernetes production cluster.</summary>
    public const string K8sPrd = "k8s-prd";

    /// <summary>File next to the binaries that names the environment when no variable or argument does.</summary>
    public const string FileName = "environment.json";

    #endregion

    #region Resolution

    /// <summary>The environments Ariva knows; any other name is refused, so a typo never selects defaults.</summary>
    public static IReadOnlyList<string> Known { get; } = [VmLocal, K8sDev, K8sDemo, K8sPrd];

    /// <summary>
    /// Returns the environment name, taken in order from the <c>--environment</c> command line argument (also what
    /// WebApplicationFactory passes in tests), <c>DOTNET_ENVIRONMENT</c>, <c>ASPNETCORE_ENVIRONMENT</c> and the
    /// <c>Environment</c> key of <see cref="FileName"/>. ARV-098: there is no default. The file exists only in build
    /// output on a developer machine (images are published without it), so a container or pod started without
    /// <c>DOTNET_ENVIRONMENT</c> refuses to start instead of running as <see cref="VmLocal"/>, where development
    /// accounts, sign-in without TOTP, SchemaUpdate and the demo seed are allowed.
    /// </summary>
    /// <param name="args">The process command line arguments.</param>
    /// <param name="baseDirectory">The folder that holds <see cref="FileName"/>, normally <see cref="AppContext.BaseDirectory"/>.</param>
    /// <returns>The environment name, one of <see cref="Known"/>.</returns>
    /// <exception cref="InvalidOperationException">No environment is named, or the name is not one of <see cref="Known"/>.</exception>
    public static string Resolve(string[] args, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(baseDirectory);

        var fromArguments = new ConfigurationBuilder()
            .AddCommandLine(args ?? [])
            .Build()[HostDefaults.EnvironmentKey];
        if (!string.IsNullOrWhiteSpace(fromArguments))
        {
            return Checked(fromArguments, "the --environment argument");
        }

        var fromVariables = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        if (!string.IsNullOrWhiteSpace(fromVariables))
        {
            return Checked(fromVariables, "DOTNET_ENVIRONMENT or ASPNETCORE_ENVIRONMENT");
        }

        var fromFile = new ConfigurationBuilder()
            .SetBasePath(baseDirectory)
            .AddJsonFile(FileName, optional: true, reloadOnChange: false)
            .Build()["Environment"];
        if (!string.IsNullOrWhiteSpace(fromFile))
        {
            return Checked(fromFile, FileName);
        }

        throw new InvalidOperationException(
            $"No Ariva environment is set. Set DOTNET_ENVIRONMENT to one of {string.Join(", ", Known)} (the Helm chart does); " +
            $"a developer machine gets {VmLocal} from {FileName} in the build output.");
    }

    private static string Checked(string name, string source)
    {
        var known = Known.FirstOrDefault(k => string.Equals(k, name.Trim(), StringComparison.OrdinalIgnoreCase));
        return known ?? throw new InvalidOperationException(
            $"Unknown Ariva environment '{name.Trim()}' from {source}: use one of {string.Join(", ", Known)}.");
    }

    #endregion

    #region Checks

    /// <summary>True when the host runs on a developer machine (<see cref="VmLocal"/>).</summary>
    /// <param name="environment">The host environment.</param>
    /// <returns>Whether the environment is vm-local.</returns>
    public static bool IsVmLocal(this IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return string.Equals(environment.EnvironmentName, VmLocal, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the host runs in production (<see cref="K8sPrd"/>).</summary>
    /// <param name="environment">The host environment.</param>
    /// <returns>Whether the environment is k8s-prd.</returns>
    public static bool IsK8sPrd(this IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return string.Equals(environment.EnvironmentName, K8sPrd, StringComparison.OrdinalIgnoreCase);
    }

    #endregion
}
