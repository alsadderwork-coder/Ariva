using Ariva.Core.Border;
using Ariva.Core.Domain.Entities;

namespace Ariva.Core.Flights;

/// <summary>
/// Which categories of the arrival wave (F14) a queue zone's lane takes, one rule for the staffing recommendation (F16) and
/// the predicted-breach wave (owner decision 2026-10-26, ARV-185). A lane the wave models (<see cref="LaneCategory.Reference"/>:
/// the manual lanes CIT, RES, VIS and CRW, and the e-gate lane EG, which keeps its arrivals while its rejects also join a
/// manual lane through F12) takes its own category. The shared lane (<see cref="LaneCategory.Shared"/>, one queue for every
/// manual category) takes the manual categories (<see cref="ImmigrationRules.Lanes"/>) that no arrivals-hall queue of the
/// site has as its own lane, so nobody is counted in two queues. Any other lane (a site's own code) takes none: it has no
/// demand. Pure.
/// </summary>
public static class LaneDemand
{
    /// <summary>
    /// The categories <paramref name="lane"/> takes, in code order, given <paramref name="hallLanes"/>, the lanes of the site's
    /// arrivals-hall queue zones (the zone's own lane among them or not).
    /// </summary>
    public static IReadOnlyList<string> Categories(string lane, IEnumerable<string> hallLanes)
    {
        ArgumentNullException.ThrowIfNull(hallLanes);
        if (lane is null)
            return [];
        if (System.Linq.Enumerable.Contains(LaneCategory.Reference, lane, StringComparer.Ordinal))
            return [lane];
        if (lane != LaneCategory.Shared)
            return [];
        var dedicated = hallLanes.Where(l => l is not null).ToHashSet(StringComparer.Ordinal);
        return [.. ImmigrationRules.Lanes.Where(c => !dedicated.Contains(c)).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The passengers of <paramref name="categories"/> in one minute's counts: their sum, or NaN when one of them is not a
    /// finite, non-negative number or the sum is not finite (a minute that is not plausible poisons its interval, never the
    /// site, CWE-501).
    /// </summary>
    public static double Of(LaneCounts lanes, IReadOnlyList<string> categories)
    {
        ArgumentNullException.ThrowIfNull(lanes);
        ArgumentNullException.ThrowIfNull(categories);
        double sum = 0;
        foreach (var category in categories)
        {
            var value = lanes.Of(category);
            if (!double.IsFinite(value) || value < 0)
                return double.NaN;
            sum += value;
        }

        return double.IsFinite(sum) ? sum : double.NaN;
    }
}
