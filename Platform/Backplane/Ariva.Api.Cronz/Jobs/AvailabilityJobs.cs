using Ariva.Core.Availability;
using Ariva.Infra.Availability;
using Ariva.Infra.Resilience;
using TickerQ.Utilities.Base;

namespace Ariva.Api.Cronz.Jobs;

/// <summary>
/// The availability ledger (ARV-118, formulas F18), every minute at second 30: one run of <see cref="AvailabilityLedger"/>
/// over the sites, each in its own transaction under the site's advisory lock, inserting each decided minute once. A run
/// from the dashboard, a second replica or a restart writes nothing twice (the first decision of a minute stands); after
/// downtime the backlog is filled from the database alone (runbook 4.8k). A run ends at <see cref="RunTimeout"/> (ARV-096e): the
/// sites it finished keep their minutes, and the next run goes on from where each site stopped.
/// </summary>
public sealed class AvailabilityJobs(AvailabilityLedger ledger, AvailabilitySettings settings, ILogger<AvailabilityJobs> logger, TimeProvider timeProvider = null)
    : IBoundedRuns
{
    /// <summary>The function name the dashboard shows and runs.</summary>
    public const string Ledger = "AvailabilityLedger";

    /// <summary>
    /// Ten minutes: a run of 500 sites, each a day of catch-up at the most, within the database command timeout per statement. The
    /// next occurrence starts beside a long run (TickerQ), so this also bounds how many runs a stalled database gathers.
    /// </summary>
    public TimeSpan RunTimeout { get; init; } = TimeSpan.FromMinutes(10);

    [TickerFunction(Ledger, "30 * * * * *")]
    public async Task RecordAsync(TickerFunctionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!settings.Enabled)
            return;
        var run = await RunBound.RunAsync(Ledger, RunTimeout, timeProvider ?? TimeProvider.System, ledger.RunAsync, ct);
        if (run.SitesFailed > 0)
            logger.LogWarning("Availability ledger ({Type}): {Sites} sites, {Failed} failed, {Minutes} minutes recorded", context.Type, run.Sites, run.SitesFailed, run.Minutes);
        else
            logger.LogDebug("Availability ledger ({Type}): {Sites} sites, {Held} held by another replica, {Skipped} skipped without a time zone, {Minutes} minutes ({Available} available, {Unavailable} unavailable, {Unobserved} unobserved)",
                context.Type, run.Sites, run.SitesHeldElsewhere, run.SitesSkipped, run.Minutes, run.Available, run.Unavailable, run.Unobserved);
    }
}
