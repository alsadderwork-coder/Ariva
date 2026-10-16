using Ariva.Core.Flights;
using Ariva.Core.Staffing;

namespace Ariva.Infra.Staffing;

/// <summary>
/// The one read-back check of a stored <c>lane_staffing</c> row (script 0053; CWE-501: the table is input like any other),
/// shared by every reader so that they accept the same rows: the staffing API's panel (ARV-181a,
/// <c>SvcStaffingRecommendations</c>) and the open desks below recommendation alert's inputs, live and backtest alike
/// (ARV-181b2, <c>AlertInputs</c>). Moved here from the staffing service by ARV-181b2 unchanged.
/// </summary>
internal static class StaffingReadBack
{
    #region Fields

    private static readonly HashSet<string> Gaps = new(Enum.GetNames<StaffingGap>(), StringComparer.Ordinal);
    private static readonly HashSet<string> Bounds = new(Enum.GetNames<StaffingBound>(), StringComparer.Ordinal);
    private static readonly HashSet<string> Sources = new(Enum.GetNames<DeskThroughputSource>(), StringComparer.Ordinal);

    #endregion

    #region Methods

    /// <summary>
    /// One stored row within script 0053's bounds and pairings, checked again as read (CWE-501), and the engine's own
    /// pairings the script leaves out: every number finite and in its range, every name one Ariva writes, the lane a lane
    /// category code, whole UTC minutes with the start within the plan's 195 minutes, a gap exactly when there is no
    /// recommendation, no recommendation under a lane-wide gap, the lane-wide gap the one its desks, throughput and queue
    /// give, arrivals known where there is a recommendation, at most the lane's desks recommended and all of them at the
    /// limit, a shortfall only at the limit, a bound
    /// exactly when a desk is needed, a wait exactly with a recommendation, a wait with the desks open now only when they
    /// are known and serve, and the throughput with its source.
    /// </summary>
    internal static bool Plausible(StaffingRow r) =>
        r is not null && r.Lane is not null && LaneCategory.IsValid(r.Lane) && WholeMinute(r.MinuteUtc) && WholeMinute(r.StartUtc) &&
        r.StartUtc - r.MinuteUtc is var offset && offset >= TimeSpan.Zero && offset < TimeSpan.FromMinutes(ArrivalWave.MaxProjectionWindow) &&
        r.Minutes is >= 1 and <= StaffingRecommendation.IntervalMinutes &&
        (r.Arrivals is not { } a || (double.IsFinite(a) && a is >= 0 and <= StaffingRecommendation.MaxPassengers)) &&
        r.Recommended is null or (>= 0 and <= StaffingRecommendation.MaxDesks) && r.Bound is not null && Bounds.Contains(r.Bound) &&
        r.Shortfall is null or (>= 1 and <= StaffingRecommendation.MaxShortfall) &&
        (r.WaitRecommended is not { } w || (double.IsFinite(w) && w is >= 0 and <= StaffingRecommendation.WaitCapMinutes)) &&
        (r.WaitOpen is not { } o || (double.IsFinite(o) && o is >= 0 and <= StaffingRecommendation.WaitCapMinutes)) &&
        r.Gap is not null && Gaps.Contains(r.Gap) && r.LaneGap is not null && Gaps.Contains(r.LaneGap) &&
        (r.QueueNow is not { } q || (double.IsFinite(q) && q is >= 0 and <= StaffingRecommendation.MaxPassengers)) &&
        r.OpenNow is null or (>= 0 and <= StaffingRecommendation.MaxDesks) && r.Desks is >= 0 and <= StaffingRecommendation.MaxDesks &&
        double.IsFinite(r.TargetMinutes) && r.TargetMinutes is >= 1 and <= StaffingRecommendation.MaxTargetMinutes &&
        double.IsFinite(r.BusyCap) && r.BusyCap is >= 0.5 and <= 1 &&
        (r.ThroughputPerDesk is not { } rate
            ? r.ThroughputSource is null
            : double.IsFinite(rate) && rate is > 0 and <= 1_000 && r.ThroughputSource is not null && Sources.Contains(r.ThroughputSource)) &&
        // Pairings: script 0053's, then the engine's.
        (r.Gap == nameof(StaffingGap.None)) == r.Recommended is not null &&
        (r.Gap != nameof(StaffingGap.None) || r.Arrivals is not null) &&
        (r.LaneGap == nameof(StaffingGap.None) || r.Recommended is null) &&
        (r.Recommended is null || r.Recommended <= r.Desks) &&
        (!r.AtLimit || r.Recommended == r.Desks) &&
        (r.Shortfall is null || r.AtLimit) &&
        r.LaneGap == LaneGapOf(r) &&
        (r.Bound == nameof(StaffingBound.None)) == r.Recommended is null or 0 &&
        (r.WaitRecommended is null) == (r.Recommended is null) &&
        (r.WaitOpen is null || (r.Recommended is not null && r.OpenNow is not null)) &&
        (!r.NoServiceOpen || (r.WaitOpen is null && r.Recommended is not null && r.OpenNow is not null));

    // The lane-wide gap the engine gives (StaffingRecommendation.Recommend): no desks, then no throughput, then no queue now.
    private static string LaneGapOf(StaffingRow r) =>
        r.Desks == 0 ? nameof(StaffingGap.NoDesks)
        : r.ThroughputPerDesk is null ? nameof(StaffingGap.NoThroughput)
        : r.QueueNow is null ? nameof(StaffingGap.NoQueueLength)
        : nameof(StaffingGap.None);

    private static bool WholeMinute(DateTime value) => value.Ticks % TimeSpan.TicksPerMinute == 0;

    #endregion

    /// <summary>One stored row of <c>lane_staffing</c> as read (internal for the read-back tests).</summary>
    internal sealed class StaffingRow
    {
        public string ZoneKey { get; set; }
        public DateTime MinuteUtc { get; set; }
        public int IntervalIndex { get; set; }
        public string Lane { get; set; }
        public DateTime StartUtc { get; set; }
        public int Minutes { get; set; }
        public double? Arrivals { get; set; }
        public int? Recommended { get; set; }
        public string Bound { get; set; }
        public bool AtLimit { get; set; }
        public int? Shortfall { get; set; }
        public double? WaitRecommended { get; set; }
        public double? WaitOpen { get; set; }
        public bool NoServiceOpen { get; set; }
        public string Gap { get; set; }
        public string LaneGap { get; set; }
        public double? QueueNow { get; set; }
        public int? OpenNow { get; set; }
        public bool OpenNowDegraded { get; set; }
        public int Desks { get; set; }
        public double TargetMinutes { get; set; }
        public double BusyCap { get; set; }
        public double? ThroughputPerDesk { get; set; }
        public string ThroughputSource { get; set; }
    }
}
