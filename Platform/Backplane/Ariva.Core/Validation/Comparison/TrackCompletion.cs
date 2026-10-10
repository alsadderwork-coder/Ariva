using Ariva.Core.Queueing;

namespace Ariva.Core.Validation.Comparison;

/// <summary>
/// Track completion per queue zone over a campaign (formulas F18, ARV-104e), from the stored continuous checks
/// (zone_health_bin, ARV-114a): over every 15-minute bin of the planned days, the latest revision's tracks; the rate is the
/// tracks that exited over the tracks that entered in the Good bins, pooled (never an average of bin rates). Degraded and
/// Unknown bins (a bin of the planned days without a stored row, or whose latest revision is refused or conflicting, is Unknown)
/// are reported apart with their tracks; Provisional and OtherVersion bins are counted, and every bin that is not Good is
/// counted as excluded. Only zones whose sensors track people (T3) have tracks; elsewhere there is no rate (no
/// data, never 0 or 1). Pure: no I/O, no clock.
/// </summary>
public static class TrackCompletion
{
    /// <summary>Tracks exited over tracks entered; null with none entered or counts that cannot be (below 0, more exited than entered).</summary>
    public static double? Rate(long entered, long exited) => entered > 0 && exited >= 0 && exited <= entered ? (double)exited / entered : null;

    /// <summary>Each zone in scope, ordered by name.</summary>
    internal static IReadOnlyList<ZoneTrackCompletion> Compare(ComparisonData data)
    {
        var target = data.Settings.TrackCompletionTarget;
        var zones = new List<ZoneTrackCompletion>();
        foreach (var zone in data.Zones)
        {
            var standings = new List<ComparisonStanding>();
            var totals = new Dictionary<ComparisonStanding, List<ZoneHealthBin>>
            {
                [ComparisonStanding.Good] = [],
                [ComparisonStanding.Degraded] = [],
                [ComparisonStanding.Unknown] = []
            };
            var missing = 0;
            foreach (var bin in data.WindowBins)
            {
                var health = data.HealthAt(zone.Name, bin);
                var standing = ComparisonData.Worst(health switch
                {
                    null => ComparisonStanding.Unknown,
                    { ZoneProfileVersion: var v } when v != data.Version => ComparisonStanding.OtherVersion,
                    { Status: not BinStatus.Final } => ComparisonStanding.Provisional,
                    _ => ComparisonStanding.Good
                }, data.Over(zone.Name, bin, bin + ComparisonData.BinLength, requireFinal: true));
                standings.Add(standing);
                if (!totals.TryGetValue(standing, out var list))
                    continue;
                // Without a stored row the bin is at least Unknown: counted there, with no tracks.
                if (health is null)
                    missing++;
                else
                    list.Add(health);
            }

            var good = Totals(totals[ComparisonStanding.Good], 0);
            var verdict = good.Rate is { } rate
                ? CountAccuracy.AtLeast(rate, target) ? CriterionVerdict.Pass : CriterionVerdict.Fail
                : CriterionVerdict.NoData;
            var tally = StandingTally.Of(standings);
            zones.Add(new ZoneTrackCompletion(data.Version, zone.Name, tally, tally.Total - tally.Good, good, Totals(totals[ComparisonStanding.Degraded], 0),
                Totals(totals[ComparisonStanding.Unknown], missing), new CriterionCheck(good.Rate, target, verdict)));
        }

        return zones.AsReadOnly();
    }

    private static TrackTotals Totals(List<ZoneHealthBin> bins, int missing)
    {
        long entered = bins.Sum(b => b.TracksEntered), exited = bins.Sum(b => b.TracksExited);
        return new TrackTotals(bins.Count + missing, entered, exited, bins.Sum(b => b.TracksAbandoned), bins.Sum(b => b.TracksFragmented),
            bins.Sum(b => b.TracksCensored), Rate(entered, exited));
    }
}
