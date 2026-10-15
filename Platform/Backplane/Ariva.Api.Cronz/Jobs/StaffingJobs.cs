using Ariva.Core.Staffing;
using Ariva.Infra.Staffing;
using TickerQ.Utilities.Base;

namespace Ariva.Api.Cronz.Jobs;

/// <summary>
/// The indicative staffing recommendation (ARV-181a, formulas F16 pilot version), every minute at second 45 (the queue
/// minute's live part lands about 30 s after the minute ends): one run of <see cref="StaffingRecommendations"/> over the
/// sites, each in its own transaction under the site's advisory lock, inserting each lane's minute once. A run from the
/// dashboard, a second replica or a restart writes nothing twice (the first computation of a minute stands); a minute the
/// job missed stays empty.
/// </summary>
public sealed class StaffingJobs(StaffingRecommendations recommendations, StaffingSettings settings, ILogger<StaffingJobs> logger)
{
    /// <summary>The function name the dashboard shows and runs.</summary>
    public const string Recommendations = "StaffingRecommendations";

    [TickerFunction(Recommendations, "45 * * * * *")]
    public async Task RecommendAsync(TickerFunctionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!settings.Enabled)
            return;
        var run = await recommendations.RunAsync(ct);
        if (run.SitesFailed > 0 || run.SitesRefused > 0 || run.LanesRefused > 0)
            logger.LogWarning(
                "Staffing recommendation ({Type}): {Sites} sites, {Failed} failed, {Refused} refused by a read bound, {Lanes} lanes, {LanesRefused} lanes refused, {Rows} rows written",
                context.Type, run.Sites, run.SitesFailed, run.SitesRefused, run.Lanes, run.LanesRefused, run.Rows);
        else
            logger.LogDebug("Staffing recommendation ({Type}): {Sites} sites, {Held} held by another replica, {Lanes} lanes, {Rows} rows written",
                context.Type, run.Sites, run.SitesHeldElsewhere, run.Lanes, run.Rows);
    }
}
