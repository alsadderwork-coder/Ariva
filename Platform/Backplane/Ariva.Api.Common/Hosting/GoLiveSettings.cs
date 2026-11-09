using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;

namespace Ariva.Api.Common.Hosting;

/// <summary>
/// ARV-136a (CWE-501; docs/product/pilot-to-confirm.md): the go-live settings whose pilot values are still To confirm with the
/// border authority and Dalil. In k8s-prd a host takes each one only when a k8s-prd source sets it: an
/// <c>appsettings.*.k8s-prd.json</c> file, an environment variable or the command line (<see cref="Confirms"/>). A value from
/// any other source (the code's defaults, the environment-neutral settings files, a developer's local file) is a reference
/// default, and a value that still reads <see cref="Placeholder"/> is a placeholder: either way the host refuses to start, so
/// production never runs on a value nobody confirmed. Each setting has a declared shape, so a section where one value belongs,
/// a value where a section belongs, a missing part or a part the setting does not have is refused too: the binder would run the
/// code's default for each. Every k8s-prd host checks every setting, so the shared k8s-prd settings carry one set of answers.
/// TC-03, TC-08 and TC-09 are a site's data, not settings (ARV-136).
/// </summary>
public static class GoLiveSettings
{
    #region Types

    /// <summary>A go-live setting: its pilot-to-confirm item, its key and what it decides, with the parts of a section.</summary>
    /// <param name="Item">The item of docs/product/pilot-to-confirm.md.</param>
    /// <param name="Key">The configuration key.</param>
    /// <param name="Decides">What the value decides.</param>
    /// <param name="Parts">The section's parts, each one value, all required; null for a setting that is one value.</param>
    public sealed record Setting(string Item, string Key, string Decides, IReadOnlyList<string> Parts = null);

    #endregion

    #region Constants

    /// <summary>The text a placeholder holds, in any letter case and spacing.</summary>
    public const string Placeholder = "To confirm";

    /// <summary>The ending of a k8s-prd settings file's name.</summary>
    public const string K8sPrdFileEnding = ".k8s-prd.json";

    /// <summary>Every go-live setting.</summary>
    public static IReadOnlyList<Setting> All { get; } =
    [
        new("TC-01", "Border:EgateCoupling:RejectLane", "the manual lane e-gate rejects join, formulas F12"),
        new("TC-02", "Flights:ArrivalWave:Mix", "the passenger lane mix when AMAN sends no lane demand, formulas F14",
            ["Cit", "Res", "Vis", "Crw", "Trf", "EGateShare"]),
        new("TC-44", "Stream:HandoverWindowSeconds", "the queue engine's hand-over window, formulas F6"),
        new("TC-44", "Stream:CensorAfterMinutes", "the queue engine's censoring timeout, formulas F6"),
        new("TC-44", "Border:DeskFeed:StaleAfterSeconds", "the desk engine's T_stale, formulas F10"),
        new("TC-47", "Kafka:Topics:Partitions", "the partitions of each provisioned topic")
    ];

    #endregion

    #region Checks

    /// <summary>
    /// The go-live settings <paramref name="configuration"/> does not confirm, one line each naming its key, its item and the
    /// first problem found; the values themselves are never quoted. A configuration that is not a root, whose sources cannot
    /// be told, confirms nothing.
    /// </summary>
    /// <param name="configuration">The host's layered configuration.</param>
    /// <returns>One line per setting not confirmed.</returns>
    public static IReadOnlyList<string> Problems(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        // The last provider that holds a key gives its value, as the configuration root reads it.
        var providers = configuration is IConfigurationRoot root ? root.Providers.Reverse().ToList() : null;
        var problems = new List<string>();
        foreach (var setting in All)
        {
            var problem = setting.Parts is null ? ValueProblem(configuration, providers, setting.Key) : SectionProblem(configuration, providers, setting);
            if (problem is not null)
                problems.Add($"{setting.Key} ({setting.Item}, {setting.Decides}) {problem}");
        }

        return problems;
    }

    /// <summary>
    /// Refuses to start a k8s-prd host while <see cref="Problems"/> finds any; other environments run on the reference
    /// defaults. Called by every host right after <see cref="ArivaEnvironment.EnsureConfigured"/>, before any setting is bound
    /// and before any command runs.
    /// </summary>
    /// <param name="configuration">The host's layered configuration.</param>
    /// <param name="hostEnvironment">The environment <see cref="ArivaEnvironment.Resolve"/> returned.</param>
    /// <exception cref="InvalidOperationException">In k8s-prd, a go-live setting is not confirmed.</exception>
    public static void EnsureConfirmed(IConfiguration configuration, string hostEnvironment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!string.Equals(hostEnvironment, ArivaEnvironment.K8sPrd, StringComparison.Ordinal))
            return;
        var problems = Problems(configuration);
        if (problems.Count > 0)
            throw new InvalidOperationException(
                "k8s-prd does not start on go-live settings nobody confirmed (docs/product/pilot-to-confirm.md): " + string.Join("; ", problems) +
                ". Set each one for k8s-prd once its answer is in: appsettings.base.k8s-prd.json, the service's k8s-prd file or the release's environment variables.");
    }

    /// <summary>
    /// Whether <paramref name="provider"/>, which gives <paramref name="key"/> its value, is a k8s-prd source: the environment
    /// variables, the command line, a settings file named <c>*.k8s-prd.json</c>, or a chained configuration whose own source
    /// for the key is one of those. Any other provider (the environment-neutral files, a developer's
    /// <c>appsettings.local.json</c>, an in-memory or stream source) gives a reference default.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <param name="key">The key it gives.</param>
    /// <returns>Whether the value counts as confirmed.</returns>
    public static bool Confirms(IConfigurationProvider provider, string key) => provider switch
    {
        EnvironmentVariablesConfigurationProvider => true,
        CommandLineConfigurationProvider => true,
        JsonConfigurationProvider json => Path.GetFileName(json.Source.Path ?? string.Empty).EndsWith(K8sPrdFileEnding, StringComparison.OrdinalIgnoreCase),
        ChainedConfigurationProvider chained => chained.Configuration is IConfigurationRoot inner &&
                                                Winner(inner.Providers.Reverse().ToList(), key) is { } source && Confirms(source, key),
        _ => false
    };

    /// <summary>The problem of a setting that is one value: a section under it, blank, a placeholder, or a reference default.</summary>
    private static string ValueProblem(IConfiguration configuration, List<IConfigurationProvider> providers, string key)
    {
        var section = configuration.GetSection(key);
        if (section.GetChildren().Any())
            return "is a section where one value belongs";
        return LeafProblem(section.Value, providers, key, "is");
    }

    /// <summary>The problem of a setting that is a section: a value of its own, a part it does not have, or a part's problem.</summary>
    private static string SectionProblem(IConfiguration configuration, List<IConfigurationProvider> providers, Setting setting)
    {
        var section = configuration.GetSection(setting.Key);
        if (!string.IsNullOrWhiteSpace(section.Value))
            return $"is one value where the parts {string.Join(", ", setting.Parts)} belong";
        if (section.GetChildren().Any(child => !setting.Parts.Contains(child.Key, StringComparer.OrdinalIgnoreCase)))
            return $"has a part that is not one of {string.Join(", ", setting.Parts)}";
        foreach (var part in setting.Parts)
        {
            var key = $"{setting.Key}:{part}";
            var value = configuration.GetSection(key);
            if (value.GetChildren().Any())
                return $"part {part} is a section where one value belongs";
            if (LeafProblem(value.Value, providers, key, $"part {part} is") is { } problem)
                return problem;
        }

        return null;
    }

    /// <summary>The problem of one value: blank, a placeholder, or a source that does not confirm it.</summary>
    private static string LeafProblem(string value, List<IConfigurationProvider> providers, string key, string subject)
    {
        if (string.IsNullOrWhiteSpace(value))
            return $"{subject} not set";
        // Spacing collapsed, so "To  confirm" or a placeholder broken over a line counts too.
        if (string.Join(' ', value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)).Contains(Placeholder, StringComparison.OrdinalIgnoreCase))
            return $"{subject} still {Placeholder}";
        if (providers is null)
            return $"{subject} from settings whose sources cannot be told";
        return Winner(providers, key) is { } source && Confirms(source, key) ? null : $"{subject} only a reference default, from no k8s-prd source";
    }

    /// <summary>The provider that gives <paramref name="key"/> its value: the first of <paramref name="providers"/> (latest first) that holds it.</summary>
    private static IConfigurationProvider Winner(IReadOnlyList<IConfigurationProvider> providers, string key) =>
        providers.FirstOrDefault(provider => provider.TryGet(key, out _));

    #endregion
}
