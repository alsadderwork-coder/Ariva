using Ariva.Di.Extensions;
using Ariva.Infra.Timescale;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ariva.Di;

/// <summary>
/// The one-shot migration command (ARV-006). Ariva.Api.Main runs it instead of the web host when started with
/// <c>--migrate</c>; the Helm chart runs that as a Job before each release, so pods never change the schema.
/// </summary>
public static class DatabaseMigration
{
    public const string Flag = "--migrate";

    public static bool IsRequested(string[] args) => args?.Contains(Flag, StringComparer.Ordinal) == true;

    /// <summary>The arguments without the flag, so the configuration command line provider never sees it.</summary>
    public static string[] WithoutFlag(string[] args) => (args ?? []).Where(a => !string.Equals(a, Flag, StringComparison.Ordinal)).ToArray();

    /// <summary>Applies the scripts and returns the process exit code: 0 on success, 1 on failure (logged).</summary>
    public static async Task<int> RunAsync(IConfiguration configuration, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddSimpleConsole(options => options.SingleLine = true));
        services.AddArivaPersistence(configuration);

        await using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DatabaseMigration));
        try
        {
            var applied = await provider.GetRequiredService<DatabaseMigrator>().MigrateAsync(ct);
            logger.LogInformation("Migration finished: {Count} script(s) applied", applied.Count);
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Migration failed");
            return 1;
        }
    }
}
