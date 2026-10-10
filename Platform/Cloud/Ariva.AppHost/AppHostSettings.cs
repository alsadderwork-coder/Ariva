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

    #region The development-only site (ARV-139c)

    /// <summary>The AppHost's own key for the development-only site of the owner's machine (run-ariva.ps1's switch).</summary>
    public const string DevelopmentSiteKey = "AppHost:NbjSite";

    /// <summary>The suffix of the database volume of a run with the development-only site, so the usual volume never holds its rows.</summary>
    public const string DevelopmentSiteVolumeSuffix = "-nbj";

    /// <summary>
    /// The development-only site for this run (CWE-200): read from this section only (the argument run-ariva.ps1's switch
    /// passes), never from a Seed variable the AppHost inherits; api-main gets the seed setting from it and nothing else.
    /// </summary>
    public bool NbjSite { get; init; }

    /// <summary>
    /// The demo accounts of a run with the development-only site: the file demo-local.mjs prepares (.demo), of which only
    /// api-main's sign-in variables are applied (<see cref="ReadAccounts"/>). Refused in every other run, which takes the
    /// demo accounts from <see cref="HostEnvironmentFile"/>.
    /// </summary>
    public string AccountsFile { get; init; }

    private bool DevelopmentSite => NbjSite;

    /// <summary>The database volume of this run: the usual one, or its separate twin when the development-only site is on.</summary>
    public string DatabaseVolumeForRun => DevelopmentSite && !string.IsNullOrWhiteSpace(DatabaseVolume)
        ? DatabaseVolume + DevelopmentSiteVolumeSuffix
        : DatabaseVolume;

    /// <summary>
    /// Refuses what a run with the development-only site must never be combined with: the host variables file (the E2E
    /// suite's through e2e-apphost, the scripted demo's), which could point a host elsewhere; a run that keeps no data
    /// (the E2E switches); and a run without a database volume, whose data would stay in the shared container.
    /// </summary>
    public void EnsureConsistent()
    {
        if (!DevelopmentSite)
        {
            if (!string.IsNullOrWhiteSpace(AccountsFile))
                throw new InvalidOperationException($"AppHost:AccountsFile is only for a run with {DevelopmentSiteKey}; other runs take the demo accounts from AppHost:HostEnvironmentFile.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(HostEnvironmentFile))
            throw new InvalidOperationException($"{DevelopmentSiteKey} is refused with AppHost:HostEnvironmentFile (the E2E suite's and the scripted demo's settings); give the demo accounts as AppHost:AccountsFile.");
        if (!Persistent)
            throw new InvalidOperationException($"{DevelopmentSiteKey} is refused with AppHost:Persistent false (an E2E run).");
        if (string.IsNullOrWhiteSpace(DatabaseVolume))
            throw new InvalidOperationException($"{DevelopmentSiteKey} needs a database volume: its run uses the volume's separate twin ({DevelopmentSiteVolumeSuffix}), never the usual one.");
    }

    /// <summary>
    /// The sign-in variables for api-main from <see cref="AccountsFile"/>: Auth__TotpRequired and Auth__DevelopmentUsers__n__*
    /// only. Any other variable for api-main is refused; other resources' sections (the scripted demo's simulator key) are
    /// not applied, so the scripted demo cannot drive a run with the development-only site.
    /// </summary>
    public IReadOnlyDictionary<string, string> ReadAccounts()
    {
        if (string.IsNullOrWhiteSpace(AccountsFile))
            return new Dictionary<string, string>();
        var parsed = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(AccountsFile))
                     ?? throw new InvalidOperationException("AppHost:AccountsFile is empty.");
        if (!parsed.TryGetValue("api-main", out var variables) || variables is null)
            throw new InvalidOperationException("AppHost:AccountsFile has no api-main accounts.");
        foreach (var name in variables.Keys.Where(name => !AccountVariable().IsMatch(name)))
            throw new InvalidOperationException($"AppHost:AccountsFile: {name} for api-main is refused (only Auth__TotpRequired and Auth__DevelopmentUsers__n__* are applied).");
        return variables;
    }

    [GeneratedRegex(@"^Auth__(TotpRequired|DevelopmentUsers__\d{1,2}__(UserName|Password|Temporary|TotpSecret|Roles__\d{1,2}|Sites__\d{1,2}))$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AccountVariable();

    #endregion

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
