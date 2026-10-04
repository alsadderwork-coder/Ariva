using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace Ariva.AppHost;

/// <summary>
/// The AppHost section of its configuration (appsettings.json, user secrets, AppHost__* variables or --AppHost:*
/// arguments). Host ports default to docker-compose.dev.yml's, so tools that know those ports (the postgres-dev MCP,
/// the E2E suite) work against either; run one or the other, not both.
/// </summary>
public sealed partial class AppHostSettings
{
    public const string SectionName = "AppHost";

    public int DatabasePort { get; init; } = 5433;
    public int KafkaPort { get; init; } = 19092;
    public int RedisPort { get; init; } = 16379;
    public int SmtpPort { get; init; } = 2525;
    public int SmtpUiPort { get; init; } = 5080;

    /// <summary>Start the SvelteKit dev server (needs npm ci in Ariva.Web first).</summary>
    public bool Web { get; init; } = true;

    /// <summary>Start Ariva.Api.Stream (the E2E suite runs without it, as Playwright would start the hosts).</summary>
    public bool Stream { get; init; } = true;

    /// <summary>
    /// Keep TimescaleDB across AppHost runs, with its data in <see cref="DatabaseVolume"/>. An E2E run turns it off and
    /// names no volume, so test data never lands in the development database.
    /// </summary>
    public bool Persistent { get; init; } = true;

    /// <summary>The TimescaleDB data volume; empty for none (the data goes with the container).</summary>
    public string DatabaseVolume { get; init; } = "ariva-apphost-timescaledb";

    /// <summary>
    /// A JSON file of environment variables per resource ({"api-main": {"Name": "value"}}), applied after the AppHost's
    /// own: written by the E2E suite (scripts/e2e-apphost.mjs) so the hosts run exactly as Playwright would start them.
    /// </summary>
    public string HostEnvironmentFile { get; init; }

    public static AppHostSettings From(IConfiguration configuration) =>
        configuration.GetSection(SectionName).Get<AppHostSettings>() ?? new AppHostSettings();

    /// <summary>
    /// Variables the file may not set: the runtime's own (startup hooks, profilers, preloaded libraries, the environment
    /// name) and OpenTelemetry's experimental switches, so it can never turn URL query redaction off or change what runs.
    /// </summary>
    private static readonly string[] RefusedPrefixes = ["DOTNET_", "ASPNETCORE_", "CORECLR_", "COR_", "LD_", "DYLD_", "OTEL_DOTNET_EXPERIMENTAL_"];

    /// <summary>The per-resource variables of <see cref="HostEnvironmentFile"/>; names that are not variable names, or that are refused, fail.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ReadHostEnvironment()
    {
        if (string.IsNullOrWhiteSpace(HostEnvironmentFile))
            return new Dictionary<string, IReadOnlyDictionary<string, string>>();
        var parsed = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(HostEnvironmentFile))
                     ?? throw new InvalidOperationException("AppHost:HostEnvironmentFile is empty.");
        foreach (var (resource, variables) in parsed)
        {
            foreach (var name in variables.Keys.Where(name => !VariableName().IsMatch(name)))
                throw new InvalidOperationException($"AppHost:HostEnvironmentFile: '{name}' for {resource} is not an environment variable name.");
            foreach (var name in variables.Keys.Where(name => RefusedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidOperationException($"AppHost:HostEnvironmentFile: {name} for {resource} is refused (runtime and redaction settings stay the AppHost's).");
        }

        return parsed.ToDictionary(p => p.Key, p => (IReadOnlyDictionary<string, string>)p.Value, StringComparer.Ordinal);
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,199}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex VariableName();
}
