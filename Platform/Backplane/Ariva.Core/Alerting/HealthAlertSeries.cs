using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Queueing;

namespace Ariva.Core.Alerting;

/// <summary>
/// One stored health row of a queue zone's bin as the alert evaluation reads it: the latest revision of the bin in
/// <c>zone_health_bin</c> (F18, ARV-114a). Plain data, read from the table or built by a test.
/// </summary>
public sealed record HealthBinReading(
    DateTime StartUtc,
    int LengthMinutes,
    BinStatus Status,
    long? ConservationResidual,
    int? OccupancyEnd,
    long TracksEntered,
    long TracksExited,
    int CapacityMinutes,
    int MinutesOutsideCapacity)
{
    public DateTime EndUtc => StartUtc.AddMinutes(LengthMinutes);
}

/// <summary>
/// The minutes a health-check rule judges (ARV-114b): the continuous checks of F18 per queue zone and bin, as alert metrics
/// in the typed rule model (no expression; CWE-94). Pure: no I/O, no clock.
/// <list type="bullet">
/// <item>Each minute of the zone (its <c>queue_minute</c> rows, as the nowcast and the bin P90 are judged) takes its value
/// from the bin that ended last by the minute's end: the bin [s, s + L) with s + L at most the minute's end and the minute's
/// end before s + 2L. A minute whose bin has no stored row has no value (skipped, as any minute without a value).</item>
/// <item>A bin's value counts once it is settled, when it can no longer change except through a recomputation's revision:
/// <c>ConservationResidual</c> once the residual is known (both boundaries measured, so the bin has ended and its entries
/// and exits are complete) or the bin is final; <c>OccupancyOutOfRange</c> once the end boundary was sampled (the watermark
/// passed the bin's end, so every minute of it was checked) or the bin is final; <c>TrackCompletion</c> once the bin is
/// final (every track that entered is resolved: a provisional bin's open tracks are still queuing, not lost), or once it
/// has ended with no track or with every track exited (nothing can change the rate then). An unsettled bin gives its
/// minutes no value.</item>
/// <item>Values: the size of the residual |r| in people; the completion rate in percent, 100 x exited / entered (no value
/// without a track); the minutes outside 0 to the capacity (no value when no minute was checked against a capacity and none
/// was outside).</item>
/// <item>The live evaluation takes a zone's minutes only up to the last one whose bin is settled
/// (<see cref="SettledThrough"/>): it waits for a bin rather than judge its minutes without a value and never again, so the
/// live alerts are the backtest's over the same stored rows. A bin still unsettled <see cref="SettleLimit"/> after its end
/// (a bin can stay provisional at most T_censor plus the lateness) no longer holds the evaluation back, and its minutes are
/// judged without a value, as a backtest would judge them while it stays so.</item>
/// </list>
/// </summary>
public static class HealthAlertSeries
{
    /// <summary>How long after its end an unsettled bin holds the live evaluation back (Proposed: the evaluation's catch-up, 180 minutes).</summary>
    public static readonly TimeSpan SettleLimit = TimeSpan.FromMinutes(180);

    /// <summary>Whether a bin's value of <paramref name="metric"/> can no longer change (except through a recomputation's revision).</summary>
    public static bool Settled(AlertMetric metric, HealthBinReading row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Status == BinStatus.Final || metric switch
        {
            AlertMetric.ConservationResidual => row.ConservationResidual is not null,
            AlertMetric.OccupancyOutOfRange => row.OccupancyEnd is not null,
            // Once the bin has ended no track enters it: none entered (no rate), or every one exited (100), stays so.
            AlertMetric.TrackCompletion => row.OccupancyEnd is not null && (row.TracksEntered == 0 || row.TracksExited >= row.TracksEntered),
            _ => false
        };
    }

    /// <summary>The bin's value of a health-check metric once settled; null when unsettled or when there is nothing to judge.</summary>
    public static double? ValueOf(AlertMetric metric, HealthBinReading row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!AlertRuleValues.IsHealthCheck(metric) || !Settled(metric, row))
            return null;
        return metric switch
        {
            AlertMetric.ConservationResidual => row.ConservationResidual is { } r ? Math.Abs((double)r) : null,
            // 100 x exited / entered in one division, so a whole percent is exact (0.9 x 100 is not).
            AlertMetric.TrackCompletion => row.TracksEntered > 0 ? 100.0 * Math.Clamp(row.TracksExited, 0, row.TracksEntered) / row.TracksEntered : null,
            _ => row.CapacityMinutes == 0 && row.MinutesOutsideCapacity == 0 ? null : row.MinutesOutsideCapacity
        };
    }

    /// <summary>Times the look-ups place (a day of margin at either end of the calendar, so no step around them overflows).</summary>
    private static bool Placeable(DateTime time) => time >= DateTime.MinValue.AddDays(2) && time <= DateTime.MaxValue.AddDays(-2);

    /// <summary>The zone's bins in the order the look-ups need (by end, then start); rows that cannot be a bin are left out.</summary>
    public static IReadOnlyList<HealthBinReading> Ordered(IEnumerable<HealthBinReading> rows) =>
        [.. (rows ?? []).Where(r => r is not null && r.LengthMinutes is >= 1 and <= 1440 && Placeable(r.StartUtc))
            .OrderBy(r => r.EndUtc).ThenBy(r => r.StartUtc)];

    /// <summary>The bin that ended last by the end of <paramref name="minuteUtc"/> (within one of its lengths), or null.</summary>
    public static HealthBinReading Governing(IReadOnlyList<HealthBinReading> ordered, DateTime minuteUtc)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        if (!Placeable(minuteUtc))
            return null;
        var end = minuteUtc.AddMinutes(1);
        int lo = 0, hi = ordered.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (ordered[mid].EndUtc <= end)
                lo = mid + 1;
            else
                hi = mid;
        }

        if (lo == 0)
            return null;
        var bin = ordered[lo - 1];
        return end < bin.EndUtc.AddMinutes(bin.LengthMinutes) ? bin : null;
    }

    /// <summary>
    /// The minutes of one zone for a health-check rule, one per stored queue minute in time order: the governing bin's
    /// settled value, the queue length (for the record; health rules take no minimum queue) and the bin's start.
    /// </summary>
    public static IReadOnlyList<AlertMinute> Minutes(AlertMetric metric, IEnumerable<(DateTime MinuteUtc, int? QueueLength)> minutes, IEnumerable<HealthBinReading> rows)
    {
        ArgumentNullException.ThrowIfNull(minutes);
        var ordered = Ordered(rows);
        var list = new List<AlertMinute>();
        foreach (var (minute, queueLength) in minutes.OrderBy(m => m.MinuteUtc))
        {
            var bin = Governing(ordered, minute);
            list.Add(bin is null ? new AlertMinute(minute, null, queueLength) : new AlertMinute(minute, ValueOf(metric, bin), queueLength, bin.StartUtc));
        }

        return list;
    }

    /// <summary>
    /// The last minute up to <paramref name="latestMinuteUtc"/> (the zone's latest stored live minute) that the live
    /// evaluation may take: before the first minute of the earliest bin that is not settled yet, ended by then and less
    /// than <see cref="SettleLimit"/> before it.
    /// </summary>
    public static DateTime SettledThrough(AlertMetric metric, DateTime latestMinuteUtc, IEnumerable<HealthBinReading> rows)
    {
        if (!Placeable(latestMinuteUtc))
            return latestMinuteUtc;
        var end = latestMinuteUtc.AddMinutes(1);
        var limit = end - SettleLimit;
        var upTo = latestMinuteUtc;
        foreach (var row in Ordered(rows))
        {
            if (row.EndUtc > end)
                break;
            // The bin's first minute is the one ending at its end; the evaluation stops at the minute before it.
            if (row.EndUtc > limit && !Settled(metric, row) && row.EndUtc.AddMinutes(-2) < upTo)
                upTo = row.EndUtc.AddMinutes(-2);
        }

        return upTo;
    }
}
