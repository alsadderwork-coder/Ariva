using Ariva.Core.Staffing;
using Ariva.Infra.Staffing;
using TickerQ.Utilities.Base;

namespace Ariva.Api.Cronz.Jobs;

/// <summary>
/// The indicative staffing recommendation (ARV-181a, formulas F16 pilot version), every minute at second 45 (the queue
/// minute's live part lands about 30 s after the minute ends): one run of <see cref="StaffingRecommendations"/> over the
/// sites, each in its own transaction under the site's advisory lock, inserting each lane's minute once. A run from the
/// dashboard, a second replica or a restart writes nothing twice (the first computation of a minute stands); a minute the
/// job missed stays empty. TickerQ 10.0.2 starts the next occurrence while a run is still going and fires about one occurrence
/// in ten twice (measured in ARV-124a, wiki 05): the site lock makes the second run skip each site the first holds, and a
/// run that takes its whole minute or more is logged as a warning with its duration.
/// </summary>
public sealed class StaffingJobs(StaffingRecommendations recommendations, StaffingSettings settings, ILogger<StaffingJobs> logger)
{
    /// <summary>The function name the dashboard shows and runs.</summary>
    public const string Recommendations = "StaffingRecommendations";

    /// <summary>The time between two occurrences of the job: a run that takes this long or longer overlaps the next.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <summary>Whether a run took its whole minute or more, so the next occurrence started while it ran.</summary>
    public static bool Overran(StaffingRun run) => run is not null && run.Elapsed >= Interval;

    /// <summary>Whether a site failed or was refused by a read bound, or a lane was refused on its own.</summary>
    public static bool Troubled(StaffingRun run) => run is not null && (run.SitesFailed > 0 || run.SitesRefused > 0 || run.LanesRefused > 0);

    /// <summary>How a run is logged: a warning when it was troubled or took its whole minute, otherwise at Debug.</summary>
    public static LogLevel LevelOf(StaffingRun run) => Troubled(run) || Overran(run) ? LogLevel.Warning : LogLevel.Debug;

    [TickerFunction(Recommendations, "45 * * * * *")]
    public async Task RecommendAsync(TickerFunctionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!settings.Enabled)
            return;
        var run = await recommendations.RunAsync(ct);
        var milliseconds = (long)run.Elapsed.TotalMilliseconds;
        if (LevelOf(run) != LogLevel.Warning)
        {
            logger.LogDebug("Staffing recommendation ({Type}): {Sites} sites, {Held} held by another run, {Lanes} lanes, {Rows} rows written in {ElapsedMs} ms",
                context.Type, run.Sites, run.SitesHeldElsewhere, run.Lanes, run.Rows, milliseconds);
            return;
        }

        if (Troubled(run))
            logger.LogWarning(
                "Staffing recommendation ({Type}): {Sites} sites, {Failed} failed, {Refused} refused by a read bound, {Lanes} lanes, {LanesRefused} lanes refused, {Rows} rows written in {ElapsedMs} ms",
                context.Type, run.Sites, run.SitesFailed, run.SitesRefused, run.Lanes, run.LanesRefused, run.Rows, milliseconds);
        // A troubled run that also took its whole minute says so too, so an overrun is never hidden behind a failure.
        if (Overran(run))
            logger.LogWarning(
                "Staffing recommendation ({Type}): the run took {ElapsedMs} ms, its whole minute or more, so the next run started beside it: {Sites} sites, {Held} held by another run, {Lanes} lanes, {Rows} rows written",
                context.Type, milliseconds, run.Sites, run.SitesHeldElsewhere, run.Lanes, run.Rows);
    }
}
