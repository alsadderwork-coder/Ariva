using System.Globalization;
using Ariva.Infra.Settings;
using Ariva.Infra.Timescale;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Ariva.Di;

/// <summary>
/// The pre-pilot check of stored texts (ARV-159, widened by ARV-162). Ariva.Api.Main runs it instead of the web host when
/// started with <c>--check-stored-names</c>: it reads the site, topology, zone profile, zone and line names, every other
/// stored text the display text rule guards, every zone outline and every floor plan file name through the hosts' runtime
/// login in a read-only transaction (<see cref="StoredNameCheck"/>) and prints one line per value its rule refuses, escaped
/// (a personal text only as its offending code points and their positions, never the text), then a summary. It never writes. Exit code 0 when every value passes, 3 when some do not, 1 on failure (logged).
/// </summary>
public static class StoredNameCheckCommand
{
    public const string Flag = "--check-stored-names";

    /// <summary>The exit code when stored names break the rule.</summary>
    public const int Found = 3;

    public static bool IsRequested(string[] args) => args?.Contains(Flag, StringComparer.Ordinal) == true;

    /// <summary>The arguments without the flag, so the configuration command line provider never sees it.</summary>
    public static string[] WithoutFlag(string[] args) => (args ?? []).Where(a => !string.Equals(a, Flag, StringComparison.Ordinal)).ToArray();

    public static async Task<int> RunAsync(IConfiguration configuration, TextWriter output, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(output);

        using var loggers = LoggerFactory.Create(logging => logging.AddSimpleConsole(options => options.SingleLine = true));
        var logger = loggers.CreateLogger(typeof(StoredNameCheckCommand));
        try
        {
            var settings = DatabaseSettings.FromConfiguration(configuration);
            // ARV-082a: as every host, never a plain or unpinned connection in production.
            Extensions.PersistenceExtensions.EnsureProductionTransport(settings, configuration);
            var result = await new StoredNameCheck(settings).RunAsync(ct);
            await output.WriteAsync(Format(result));
            return result.Findings.Count == 0 ? 0 : Found;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "The stored name check failed");
            return 1;
        }
    }

    /// <summary>One tab-separated line per finding (place, site or "-", id, state, the escaped text), then the summary.</summary>
    public static string Format(StoredNameCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var lines = result.Findings
            .Select(f => string.Join('\t', f.Place, f.SiteCode ?? "-", f.Id.ToString("D", CultureInfo.InvariantCulture), f.State, f.Escaped))
            .Append(string.Create(CultureInfo.InvariantCulture,
                $"{result.Checked} stored values checked; {result.Findings.Count} hold a control, invisible format or broken character, or an outline that touches itself (ARV-159, ARV-162)."))
            .Append(string.Empty);
        return string.Join(Environment.NewLine, lines);
    }
}
