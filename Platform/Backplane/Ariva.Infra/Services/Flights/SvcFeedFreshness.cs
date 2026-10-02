using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Flights;
using Ariva.Core.Services.Flights;
using Ariva.Infra.Flights;
using Ariva.Infra.Services.Foundation;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Flights;

/// <summary>
/// The stale-feed alarm (ARV-041, runbook 4.3). One sweep at a time across Integration's replicas (advisory lock class
/// 42, key 0, apart from the legs' class 41; the others skip). For each feed of each site: the flights due at the site (not cancelled or diverted,
/// not yet on block or off block, expected from <see cref="FlightFeedSettings.DueAfterMinutes"/> ago to
/// <see cref="FlightFeedSettings.DueBeforeMinutes"/> ahead), then <see cref="FeedFreshnessRule"/>. A feed that goes
/// stale is logged as a warning and counted; one that recovers is logged; the stale total is a gauge.
/// </summary>
internal sealed class SvcFeedFreshness(IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider, FlightFeedSettings settings,
    FlightMetrics metrics, ILogger<SvcFeedFreshness> logger) : SvcBase(unitOfWork, currentUser, timeProvider), ISvcFeedFreshness
{
    public async Task<FeedFreshnessSweep> SweepAsync(CancellationToken ct = default)
    {
        var leader = await ExecuteCommandAsync<FlagRow>("""SELECT pg_try_advisory_xact_lock(42, 0) AS "Value" """, null, ct);
        if (leader.Count == 0 || !leader[0].Value)
            return new FeedFreshnessSweep(false, 0, 0, 0);

        var now = UtcNow;
        var due = (await ExecuteSqlAsync<DueRow>("""
            SELECT site_code AS "SiteCode", CAST(count(*) AS integer) AS "Due" FROM flight_leg
            WHERE NOT cancelled AND NOT diverted
              AND CASE direction WHEN 'Arrival' THEN on_block_utc IS NULL ELSE off_block_utc IS NULL AND actual_utc IS NULL END
              AND COALESCE(estimated_utc, scheduled_utc) BETWEEN :from AND :to
            GROUP BY site_code
            """, new Dictionary<string, object> { ["from"] = now.AddMinutes(-settings.DueAfterMinutes), ["to"] = now.AddMinutes(settings.DueBeforeMinutes) }, ct))
            .ToDictionary(r => r.SiteCode, r => r.Due, StringComparer.Ordinal);

        var feeds = await Query<FeedFreshness>().OrderBy(f => f.SiteCode).ThenBy(f => f.Feed).ToListAsync(ct);
        int stale = 0, changed = 0;
        foreach (var feed in feeds)
        {
            var flights = due.GetValueOrDefault(feed.SiteCode);
            // A feed that is not live (SSIM) is never stale: Idle when silent.
            var state = settings.StaleAfter(feed.Feed) is { } cadence
                ? FeedFreshnessRule.Evaluate(feed.LastMessageUtc, now, cadence, flights)
                : now - feed.LastMessageUtc <= TimeSpan.FromMinutes(settings.DefaultStaleAfterMinutes) ? FeedState.Fresh : FeedState.Idle;
            var previous = feed.Assess(state, flights, now);
            await UpdateAsync(feed, ct);
            if (state == FeedState.Stale)
                stale++;
            if (previous is not { } was)
                continue;
            changed++;
            if (state == FeedState.Stale)
            {
                metrics.WentStale(feed.SiteCode, feed.Feed);
                logger.LogWarning("Flight feed {Feed} of {Site} is stale: nothing for {Minutes:0} min while {Flights} flights are due (runbook 4.3)", feed.Feed,
                    feed.SiteCode, (now - feed.LastMessageUtc).TotalMinutes, flights);
            }
            else if (was == FeedState.Stale)
            {
                metrics.Recovered(feed.SiteCode, feed.Feed);
                logger.LogInformation("Flight feed {Feed} of {Site} is {State} again", feed.Feed, feed.SiteCode, state);
            }
        }

        metrics.Snapshot(stale);
        return new FeedFreshnessSweep(true, feeds.Count, stale, changed);
    }

    private sealed class FlagRow
    {
        public bool Value { get; set; }
    }

    private sealed class DueRow
    {
        public string SiteCode { get; set; }
        public int Due { get; set; }
    }
}
