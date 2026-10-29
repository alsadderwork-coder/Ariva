using Ariva.Core.Sensing;
using Ariva.Core.Services.Displays;
using Ariva.Infra.Live;
using Ariva.Infra.Services.Foundation;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Displays;

/// <summary>
/// The display stale sweep (ARV-259d, wiki 10 section 3's "display stale"). One replica of Ariva.Api.Main a round, the one that
/// takes advisory lock class <see cref="LockClass"/> (key 0) in the sweep's transaction; the others skip the round without
/// waiting. It judges the enabled, live displays, at most <see cref="MaxDisplays"/> by site and code, as their boards would be
/// judged now (<see cref="SvcDisplayBoard.IsStale"/>, the player's rule): a display is stale when the live snapshot of a queue
/// zone it shows is missing or older than the display's <c>StaleSeconds</c> by this server's clock, so its board shows the
/// neutral message for that entry. Each zone's snapshot is read from Redis once a sweep. When Redis does not answer, the board
/// answers 503 too: every display not judged by then counts as stale and Redis is not asked again that sweep. A fresh snapshot
/// without a nowcast (a zone out of service, or one without enough data yet) shows the neutral message by design and is not
/// counted. Nothing is written, and nothing about a display but its site leaves the sweep.
/// </summary>
internal sealed class SvcDisplayStaleness(IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider, ILiveSnapshotStore snapshots,
    ILogger<SvcDisplayStaleness> logger) : SvcBase(unitOfWork, currentUser, timeProvider), ISvcDisplayStaleness
{
    #region Constants

    /// <summary>
    /// The sweep's advisory lock class (two-key form, key 0; 25, 26, 36, 37, 38, 41, 42, 49 to 53, 141, 145, 1421, 1431, 1432 and
    /// 1441 are taken). The statement names it as a literal; the integration test holds the two together.
    /// </summary>
    public const int LockClass = 54;

    /// <summary>The most displays one sweep judges: ten sites at the display service's 200 each (SvcDisplays.MaxDisplaysPerSite).</summary>
    public const int MaxDisplays = 2_000;

    #endregion

    #region Methods

    public async Task<DisplayStaleSweep> SweepAsync(CancellationToken ct = default)
    {
        var leader = await ExecuteCommandAsync<FlagRow>("""SELECT pg_try_advisory_xact_lock(54, 0) AS "Value" """, null, ct);
        if (leader.Count == 0 || !leader[0].Value)
            return DisplayStaleSweep.Skipped;

        // The site, the threshold and the entries only: nothing else of a display (its credential's hash, its messages) is read.
        var displays = await QueryAsNoTracking<Display>()
            .Where(d => d.DeletedOn == null && d.Enabled)
            .OrderBy(d => d.SiteCode).ThenBy(d => d.Code)
            .Take(MaxDisplays + 1)
            .Select(d => new { d.SiteCode, d.StaleSeconds, d.Entries })
            .ToListAsync(ct);
        var truncated = displays.Count > MaxDisplays;
        var now = UtcNow;
        var read = new Dictionary<string, LiveZoneSnapshot>(StringComparer.Ordinal);
        var unreadable = false;
        var sites = new Dictionary<string, (int Displays, int Stale)>(StringComparer.Ordinal);
        foreach (var display in displays.Take(MaxDisplays))
        {
            var stale = false;
            foreach (var entry in Display.EntriesOf(display.Entries))
            {
                if (unreadable)
                {
                    stale = true;
                    break;
                }

                var key = ZoneKeys.For(display.SiteCode, entry.Zone);
                if (!read.TryGetValue(key, out var snapshot))
                {
                    try
                    {
                        snapshot = await snapshots.GetAsync(key, ct);
                    }
                    catch (Exception e) when (e is StackExchange.Redis.RedisException or TimeoutException)
                    {
                        // The board answers 503 while Redis does not answer; one wait a sweep, not one per zone.
                        logger.LogWarning(e, "Display stale sweep could not read the live snapshots; every display not judged yet counts as stale");
                        unreadable = true;
                        stale = true;
                        break;
                    }

                    read[key] = snapshot;
                }

                if (SvcDisplayBoard.IsStale(snapshot, now, display.StaleSeconds))
                {
                    stale = true;
                    break;
                }
            }

            var (count, staleCount) = sites.GetValueOrDefault(display.SiteCode);
            sites[display.SiteCode] = (count + 1, staleCount + (stale ? 1 : 0));
        }

        return new DisplayStaleSweep(true, [.. sites.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => new DisplayStaleSite(s.Key, s.Value.Displays, s.Value.Stale))],
            truncated, unreadable);
    }

    #endregion

    private sealed class FlagRow
    {
        public bool Value { get; set; }
    }
}
