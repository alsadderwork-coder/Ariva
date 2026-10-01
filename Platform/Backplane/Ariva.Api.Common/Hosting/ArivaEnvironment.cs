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

    /// <summary>
    /// Returns the environment name, taken in order from the <c>--environment</c> command line argument (also what
    /// WebApplicationFactory passes in tests), <c>DOTNET_ENVIRONMENT</c>, <c>ASPNETCORE_ENVIRONMENT</c>, the
    /// <c>Environment</c> key of <see cref="FileName"/>, and finally <see cref="VmLocal"/>.
    /// </summary>
    /// <param name="args">The process command line arguments.</param>
    /// <param name="baseDirectory">The folder that holds <see cref="FileName"/>, normally <see cref="AppContext.BaseDirectory"/>.</param>
    /// <returns>The environment name.</returns>
    public static string Resolve(string[] args, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(baseDirectory);

        var fromArguments = new ConfigurationBuilder()
            .AddCommandLine(args ?? [])
            .Build()[HostDefaults.EnvironmentKey];
        if (!string.IsNullOrWhiteSpace(fromArguments))
        {
            return fromArguments.Trim();
        }

        var fromVariables = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        if (!string.IsNullOrWhiteSpace(fromVariables))
        {
            return fromVariables.Trim();
        }

        var fromFile = new ConfigurationBuilder()
            .SetBasePath(baseDirectory)
            .AddJsonFile(FileName, optional: true, reloadOnChange: false)
            .Build()["Environment"];

        return string.IsNullOrWhiteSpace(fromFile) ? VmLocal : fromFile.Trim();
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
