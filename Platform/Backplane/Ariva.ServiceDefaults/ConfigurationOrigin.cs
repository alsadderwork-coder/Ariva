using Microsoft.Extensions.Configuration;

namespace Ariva.ServiceDefaults;

/// <summary>
/// Where a setting's value comes from (ARV-091, ARV-091a). Secrets (the log pseudonym key, the Loki sender credentials, the
/// OTLP headers) must come from the environment outside vm-local, never from a configuration file: the appsettings files,
/// and the appsettings secrets the chart mounts as files, are read by more people than a Kubernetes Secret given as a variable.
/// </summary>
public static class ConfigurationOrigin
{
    /// <summary>Whether the provider that gives the setting, the last that holds it, reads a file (JSON, XML or INI).</summary>
    /// <param name="configuration">The host's configuration.</param>
    /// <param name="key">The setting.</param>
    /// <returns>True when a configuration file gives the value.</returns>
    public static bool IsFromAFile(IConfiguration configuration, string key)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration is IConfigurationRoot root &&
               root.Providers.Reverse().FirstOrDefault(provider => provider.TryGet(key, out _)) is FileConfigurationProvider;
    }
}
