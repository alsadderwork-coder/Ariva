namespace Ariva.Core.Alerting;

/// <summary>
/// The value a <see cref="Domain.Enums.AlertMetric.DesksBelowPlan"/> rule judges for one lane minute (F16, ARV-181b2): open
/// desks below recommendation, the desks the indicative recommendation of the lane's current interval (interval 0 of the
/// minute's <c>lane_staffing</c> rows, already capped at the lane's desks) asks for minus the desks open now by desk state
/// (F10), at least 0, in desks. More desks open than recommended is 0, never a negative shortfall (lead decision 2026-10-15).
/// No value (the minute neither counts toward nor breaks a sustain or clear run, <see cref="AlertEvaluator"/>) when the
/// recommendation or the open count is unknown, or the open count is degraded (its F11 flag: the desk states behind it are
/// not reliable, so neither is the difference; lead decision 2026-10-15). Which rows reach this (the job's interval 0 of the minute,
/// the read-back check, no gap) is the alert inputs' part. Pure: no I/O, no clock.
/// </summary>
public static class StaffingAlertSeries
{
    /// <summary>max(0, <paramref name="recommended"/> - <paramref name="openNow"/>); null when either is unknown or the open count is degraded.</summary>
    public static double? DesksBelow(int? recommended, int? openNow, bool openNowDegraded)
    {
        if (!recommended.HasValue || !openNow.HasValue || openNowDegraded)
            return null;
        return Math.Max(0, recommended.Value - openNow.Value);
    }
}
