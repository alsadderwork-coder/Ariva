using Ariva.Di.Extensions;
using Ariva.Infra.DataProtection;
using Ariva.Infra.Settings;
using Ariva.Infra.Timescale;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ariva.Di;

/// <summary>
/// The one-shot migration command (ARV-006). Ariva.Api.Main runs it instead of the web host when started with
/// <c>--migrate</c>; the Helm chart runs that as a Job before each release, so pods never change the schema.
/// ARV-211 (the review's findings H1 and N1): after the scripts it runs the key ring upgrade check (<see cref="KeyRingUpgradeCheck"/>)
/// with the release's configuration, so an upgrade, or a restore of a backup from before ARV-211, that would leave every stored
/// secret under keys the new hosts refuse stops here, before any pod rolls. Leftovers of an installation already upgraded (values
/// the re-protection job could not move, or a row a writer planted) never stop a release: they are logged, and the hosts measure them.
/// </summary>
public static class DatabaseMigration
{
    public const string Flag = "--migrate";

    public static bool IsRequested(string[] args) => args?.Contains(Flag, StringComparer.Ordinal) == true;

    /// <summary>The arguments without the flag, so the configuration command line provider never sees it.</summary>
    public static string[] WithoutFlag(string[] args) => (args ?? []).Where(a => !string.Equals(a, Flag, StringComparison.Ordinal)).ToArray();

    /// <summary>
    /// Applies the scripts, then checks the key ring upgrade, and returns the process exit code: 0 on success, 1 on failure
    /// (logged), 2 when the key ring upgrade check refuses (logged with what to do).
    /// </summary>
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

            var now = DateTimeOffset.UtcNow;
            var finding = await KeyRingUpgradeCheck.FindAsync(provider.GetRequiredService<DatabaseSettings>().BuildMigrationConnectionString(), ct);
            var until = KeyRingSigning.CertificateSignedUntilOf(configuration, now);
            var problem = KeyRingUpgradeCheck.Problem(finding, until, now);
            if (problem is not null)
            {
                logger.LogCritical("Key ring upgrade check refused: {Problem}", problem);
                return 2;
            }

            var leftovers = KeyRingUpgradeCheck.Leftovers(finding, until, now);
            if (leftovers is not null)
                logger.LogWarning("Key ring upgrade check: {Leftovers}", leftovers);
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Migration failed");
            return 1;
        }
    }
}
