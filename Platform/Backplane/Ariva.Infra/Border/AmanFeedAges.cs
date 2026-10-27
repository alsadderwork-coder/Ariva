using Ariva.Core.Domain.Entities;

namespace Ariva.Infra.Border;

/// <summary>
/// ARV-257: how long each border site's AMAN feed has gone unheard, for the gauge
/// <see cref="BorderMetrics.FeedAgeName"/> (meter Ariva.Border), tagged with the site only, never a desk, an officer or a
/// record. At the start of each read, the desk feed of Ariva.Api.Stream (<see cref="DeskFeed"/>) lists the sites it reads
/// with the heartbeat it stores per site (<c>desk_feed_state.heartbeat_utc</c>, the minute of AMAN's latest record, script
/// 0065) and hands the sites with AMAN desk code mappings here. Each age is the one at that listing, so every replica
/// reports the same stored heartbeat, after a restart as well, and none reports a site older than it was: a read that fails
/// or is cut leaves the stored heartbeat where it was, so the next listing shows the age grown. A replica reports its
/// latest listing while it is younger than <see cref="ReportWindow"/>; a replica whose listings stop (the database out of
/// reach, the feed stopped) stops reporting, and the alert's absence rule takes over. A site AMAN has never been heard at is
/// reported from the first listing of it on this replica. Bounded by the listed sites, at most <see cref="MaxSites"/>.
/// </summary>
public sealed class AmanFeedAges
{
    #region Fields

    /// <summary>The most sites one replica reports: far above any deployment's border sites.</summary>
    public const int MaxSites = 1_000;

    private readonly TimeProvider _time;
    private readonly DeskFeedSettings _settings;
    private volatile Listing _listing = new(DateTime.MinValue, new Dictionary<string, Heard>(StringComparer.Ordinal));

    #endregion

    public AmanFeedAges(TimeProvider time, DeskFeedSettings settings, BorderMetrics metrics = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(settings);
        _time = time;
        _settings = settings;
        metrics?.ObserveFeedAges(Ages);
    }

    #region Properties

    /// <summary>How long a replica reports its latest listing: two of the feed's polls and half a minute.</summary>
    public TimeSpan ReportWindow => TimeSpan.FromSeconds(2 * Math.Max(1, _settings.PollSeconds) + 30);

    #endregion

    #region Methods

    /// <summary>
    /// A listing of the sites with AMAN desk code mappings at <paramref name="listedUtc"/>, each with its stored heartbeat
    /// (null while AMAN has never been heard there, or since the site's desk feed state was deleted: the heartbeat this
    /// replica knew is kept then). It replaces the last one: a site no longer listed is no longer reported. A code that is not
    /// a site code is left out, and the sites past <see cref="MaxSites"/>.
    /// </summary>
    public void Listed(IEnumerable<(string Site, DateTime? HeartbeatUtc)> sites, DateTime listedUtc)
    {
        ArgumentNullException.ThrowIfNull(sites);
        var last = _listing.Sites;
        var next = new Dictionary<string, Heard>(StringComparer.Ordinal);
        foreach (var (site, heartbeatUtc) in sites)
        {
            if (next.Count >= MaxSites)
                break;
            if (!Site.IsValidCode(site) || next.ContainsKey(site))
                continue;
            next[site] = last.TryGetValue(site, out var known)
                ? new Heard(heartbeatUtc ?? known.HeartbeatUtc, known.FirstListedUtc)
                : new Heard(heartbeatUtc, listedUtc);
        }

        _listing = new Listing(listedUtc, next);
    }

    /// <summary>
    /// Each site of the latest listing and the seconds from its heartbeat (or its first listing here) to that listing, while
    /// the listing is younger than <see cref="ReportWindow"/>; none after.
    /// </summary>
    public IEnumerable<(string Site, double Seconds)> Ages()
    {
        var listing = _listing;
        if (_time.GetUtcNow().UtcDateTime - listing.ListedUtc > ReportWindow)
            return [];
        return listing.Sites.Select(s => (s.Key, Math.Max(0, (listing.ListedUtc - (s.Value.HeartbeatUtc ?? s.Value.FirstListedUtc)).TotalSeconds))).ToList();
    }

    #endregion

    /// <summary>One listing: when it was taken, and its sites.</summary>
    private sealed record Listing(DateTime ListedUtc, IReadOnlyDictionary<string, Heard> Sites);

    /// <summary>One site: its stored AMAN heartbeat (null until the feed is heard), and the first listing of it on this replica.</summary>
    private sealed record Heard(DateTime? HeartbeatUtc, DateTime FirstListedUtc);
}
