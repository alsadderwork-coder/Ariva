using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Ariva.Simulation.Api.Security;

/// <summary>
/// The simulator's environment, resolved like the Backplane hosts' (ArivaEnvironment, which this host cannot reference):
/// the <c>--environment</c> argument, <c>DOTNET_ENVIRONMENT</c>, <c>ASPNETCORE_ENVIRONMENT</c>, then
/// <c>environment.json</c>. ARV-098: no default and no unknown name. The file exists only in developer build output
/// (images are published without it), so a container started without <c>DOTNET_ENVIRONMENT</c> refuses to start.
/// </summary>
public static class SimulationEnvironment
{
    /// <summary>The environments the simulator knows, as the Backplane hosts do.</summary>
    public static IReadOnlyList<string> Known { get; } = ["vm-local", "k8s-dev", "k8s-demo", "k8s-prd"];

    /// <summary>Returns the environment, one of <see cref="Known"/>.</summary>
    /// <param name="args">The process command line arguments.</param>
    /// <param name="baseDirectory">The folder that holds environment.json, normally <see cref="AppContext.BaseDirectory"/>.</param>
    /// <returns>The canonical environment name.</returns>
    /// <exception cref="InvalidOperationException">No environment is named, or the name is unknown.</exception>
    public static string Resolve(string[] args, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(baseDirectory);

        var named = new ConfigurationBuilder().AddCommandLine(args ?? []).Build()[HostDefaults.EnvironmentKey]
                    ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                    ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                    ?? new ConfigurationBuilder()
                        .SetBasePath(baseDirectory)
                        .AddJsonFile("environment.json", optional: true, reloadOnChange: false)
                        .Build()["Environment"];

        return Known.FirstOrDefault(k => string.Equals(k, named?.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException(string.IsNullOrWhiteSpace(named)
                   ? $"No environment is set. Set DOTNET_ENVIRONMENT to one of {string.Join(", ", Known)} (the Helm chart does)."
                   : $"Unknown environment '{named.Trim()}': use one of {string.Join(", ", Known)}.");
    }
}
