using System.Globalization;
using Ariva.Core.Availability;
using Ariva.Core.Queueing;

namespace Ariva.Core.Reports;

/// <summary>
/// The weekly summary's rules (ARV-119, formulas "Weekly summary"): the local week, which weeks may be asked for, and the
/// figures built from the rows read for it. Pure: no I/O, no clock. Lane waits follow the daily report (ARV-060): each
/// hour's P50 and P90 from its merged 30-second histogram (F7, never averaged), a day's and the week's from the merged
/// hours, and a peak hour is the hour with the highest P90 among hours with at least
/// <see cref="DailyReports.MinWaitsForPeak"/> waits, so a lane's day reads the same here as in that day's report.
/// </summary>
public static class WeeklySummaries
{
    #region Constants

    /// <summary>How far back a week may start, in weeks (about the daily report's 400 days).</summary>
    public const int MaxWeeksBack = 57;

    /// <summary>
    /// A border lane's desk-state cell (a day, or the week) is shown to a reader without <c>BorderDesks.View</c> only when at
    /// least this many desks had open time in it (idle, serving or paused above 0): with fewer, its figures are one or two
    /// desks' own. A small-cell rule like the immigration screen's (ARV-057), which counts desks with transactions instead.
    /// </summary>
    public const int MinDesksForLaneFigures = 3;

    /// <summary>The site's peak hours listed for the week, worst first.</summary>
    public const int MaxPeakHours = 10;

    private static readonly string[] BorderKinds = ["Immigration", "Emigration"];
    private static readonly string[] AirportKinds = ["CheckIn", "Security"];
    private static readonly string[] KindOrder = ["Immigration", "Emigration", "CheckIn", "Security"];

    #endregion

    #region Week

    /// <summary>The Monday of the week a local date belongs to (Monday start, Proposed).</summary>
    public static DateOnly WeekStartOf(DateOnly date) => AvailabilitySummary.WeekStart(date);

    /// <summary>
    /// Whether a week may be summarised on <paramref name="today"/> (the site's local date): it starts on a Monday, has started
    /// (the current week, still running, included) and starts at most <see cref="MaxWeeksBack"/> weeks before the current one.
    /// </summary>
    public static bool IsValidWeek(DateOnly? weekStart, DateOnly today)
    {
        if (!weekStart.HasValue)
            return false;
        var start = weekStart.Value;
        var current = WeekStartOf(today);
        return start.DayOfWeek == DayOfWeek.Monday && start <= current && start >= current.AddDays(-7 * MaxWeeksBack);
    }

    /// <summary>
    /// The local week from <paramref name="weekStart"/>: each day's UTC bounds as the daily report takes them (a skipped
    /// midnight starts the day at the first valid minute), and its local hours from the day's start, the last one ending at the
    /// day's end. A week with a clock change has 167 or 169 hours.
    /// </summary>
    public static WeeklyCalendar Calendar(DateOnly weekStart, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var days = new List<WeeklyCalendarDay>(7);
        var hours = new List<WeeklyCalendarHour>(170);
        for (var d = 0; d < 7; d++)
        {
            var date = weekStart.AddDays(d);
            var (from, to) = DailyReports.DayBounds(date, zone);
            days.Add(new WeeklyCalendarDay(date, from, to));
            for (var start = from; start < to; start = start.AddHours(1))
            {
                var end = start.AddHours(1) < to ? start.AddHours(1) : to;
                hours.Add(new WeeklyCalendarHour(d, start, end, Clock(start, zone), Clock(end, zone)));
            }
        }

        return new WeeklyCalendar(weekStart, days[0].FromUtc, days[^1].ToUtc, days, hours);
    }

    /// <summary>The instants as the database reads them (ISO 8601 UTC, comma separated), for grouping rows by hour or day.</summary>
    public static string Instants(IEnumerable<DateTime> utc) =>
        string.Join(',', (utc ?? []).Select(t => DateTime.SpecifyKind(t, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)));

    private static string Clock(DateTime utc, TimeZoneInfo zone) => DailyReports.LocalText(utc, zone)[^5..];

    #endregion

    #region Build

    public static WeeklySummary Build(WeeklySummaryInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var calendar = Calendar(input.WeekStart, input.TimeZone);
        var now = input.GeneratedUtc;
        var days = calendar.Days.Select((d, i) => new WeeklyDay(d.Date, d.FromUtc, d.ToUtc, calendar.Hours.Count(h => h.Day == i), now >= d.FromUtc, now >= d.ToUtc))
            .ToList();

        var alerts = (input.Alerts ?? []).Where(a => a.RaisedUtc >= calendar.FromUtc && a.RaisedUtc < calendar.ToUtc).ToList();
        var hours = (input.Hours ?? []).Where(r => r.Hour >= 0 && r.Hour < calendar.Hours.Count).ToLookup(r => r.ZoneKey, StringComparer.Ordinal);
        var bins = (input.Bins ?? []).Where(r => r.Day is >= 0 and < 7).ToLookup(r => r.ZoneKey, StringComparer.Ordinal);
        var health = (input.Health ?? []).Where(r => r.Day is >= 0 and < 7).ToLookup(r => r.ZoneKey, StringComparer.Ordinal);
        var overflow = (input.Overflow ?? []).Where(r => r.Day is >= 0 and < 7).ToLookup(r => r.ZoneKey, StringComparer.Ordinal);
        var alertsByZone = alerts.ToLookup(a => a.ZoneName ?? string.Empty, StringComparer.Ordinal);

        var built = (input.Zones ?? [])
            .GroupBy(z => z.ZoneKey, StringComparer.Ordinal).Select(g => g.First())
            .OrderBy(z => z.LaneCategory ?? "~", StringComparer.Ordinal).ThenBy(z => z.Name, StringComparer.Ordinal)
            .Select(z => Lane(z, calendar, hours[z.ZoneKey].ToList(), bins[z.ZoneKey].ToList(), health[z.ZoneKey].ToList(), overflow[z.ZoneKey].ToList(),
                alertsByZone[z.Name ?? string.Empty].ToList(), input.SeesDataQuality))
            .ToList();
        var lanes = built.Select(b => b.Lane).ToList();

        var peakHours = built
            .SelectMany(b => b.Hours.Where(h => h.Hour.Waits >= DailyReports.MinWaitsForPeak && h.Hour.P90Minutes.HasValue)
                .Select(h => Peak(b.Lane.Zone, b.Lane.LaneCategory, calendar.Days[h.Day].Date, h.Hour)))
            .OrderByDescending(p => p.P90Minutes).ThenBy(p => p.StartUtc).ThenBy(p => p.Zone, StringComparer.Ordinal)
            .Take(MaxPeakHours)
            .ToList();

        var devices = (input.Devices ?? []).OrderBy(d => d.Code, StringComparer.Ordinal)
            .Select(d => DailyReports.Uptime(d, calendar.FromUtc, calendar.ToUtc, now)).ToList();

        WeeklyAvailability availability = null;
        WeeklyQuality quality = null;
        if (input.SeesDataQuality)
        {
            var availabilityDays = AvailabilitySummary.Days(calendar.Days[0].Date, calendar.Days[^1].Date, input.Availability);
            availability = new WeeklyAvailability(AvailabilitySummary.PilotTarget, AvailabilitySummary.Total(availabilityDays), availabilityDays);
            quality = Total(lanes.Select(l => l.Quality));
        }

        return new WeeklySummary(input.SiteCode, input.TimeZone.Id, calendar.WeekStart, calendar.Days[^1].Date, calendar.FromUtc, calendar.ToUtc, now,
            now >= calendar.ToUtc, calendar.Hours.Count, days, lanes, peakHours, Alerts(alerts, calendar), devices, quality, availability,
            DeskStates(input, calendar));
    }

    #endregion

    #region Lanes

    private sealed record BuiltLane(WeeklyLane Lane, IReadOnlyList<(int Day, WeeklyHour Hour)> Hours);

    private static BuiltLane Lane(ReportZone zone, WeeklyCalendar calendar, List<WeeklyHourRow> rows, List<WeeklyBinRow> bins, List<WeeklyHealthRow> health,
        List<WeeklyOverflowRow> overflow, List<WeeklyAlertRow> alerts, bool seesQuality)
    {
        var byHour = rows.ToLookup(r => r.Hour);
        var histograms = new IReadOnlyList<(int Bucket, long Count)>[calendar.Hours.Count];
        var hours = new List<(int Day, WeeklyHour Hour)>(calendar.Hours.Count);
        for (var i = 0; i < calendar.Hours.Count; i++)
        {
            var slot = calendar.Hours[i];
            var inHour = byHour[i].ToList();
            histograms[i] = WaitStatistics.Merge([.. inHour.Select(Histogram)]);
            hours.Add((slot.Day, new WeeklyHour(
                slot.Start,
                slot.End,
                slot.StartUtc,
                inHour.Sum(r => r.Entries),
                inHour.Sum(r => r.Waits),
                Round(WaitStatistics.Percentile(histograms[i], 0.5)),
                Round(WaitStatistics.Percentile(histograms[i], 0.9)),
                inHour.Max(r => r.MaxQueueLength),
                inHour.Any(r => r.Provisional),
                inHour.Any(r => r.HistogramMissing))));
        }

        var days = new List<WeeklyLaneDay>(7);
        for (var d = 0; d < 7; d++)
        {
            var day = calendar.Days[d];
            var indexes = Enumerable.Range(0, hours.Count).Where(i => hours[i].Day == d).ToList();
            var dayHours = indexes.Select(i => hours[i].Hour).ToList();
            var merged = WaitStatistics.Merge([.. indexes.Select(i => histograms[i])]);
            var dayBins = bins.Where(b => b.Day == d).ToList();
            var dayOverflow = overflow.Where(o => o.Day == d).ToList();
            days.Add(new WeeklyLaneDay(
                day.Date,
                dayHours.Sum(h => h.Passengers),
                dayHours.Sum(h => h.Waits),
                Round(WaitStatistics.Percentile(merged, 0.5)),
                Round(WaitStatistics.Percentile(merged, 0.9)),
                dayHours.Max(h => h.MaxQueueLength),
                // As the daily report: the highest P90 among hours with enough waits, the earlier label on a tie.
                dayHours.Where(h => h.Waits >= DailyReports.MinWaitsForPeak && h.P90Minutes.HasValue)
                    .OrderByDescending(h => h.P90Minutes).ThenBy(h => h.Start, StringComparer.Ordinal).FirstOrDefault(),
                dayHours.Any(h => h.Provisional),
                dayHours.Any(h => h.HistogramMissing),
                dayOverflow.Sum(o => o.OverflowMinutes),
                dayOverflow.Sum(o => o.ObservedMinutes),
                seesQuality ? dayBins.Sum(b => b.Bins) : null,
                seesQuality ? dayBins.Sum(b => b.DegradedBins) : null,
                alerts.Count(a => a.RaisedUtc >= day.FromUtc && a.RaisedUtc < day.ToUtc),
                dayHours));
        }

        var week = WaitStatistics.Merge(histograms);
        var best = hours.Where(h => h.Hour.Waits >= DailyReports.MinWaitsForPeak && h.Hour.P90Minutes.HasValue)
            .OrderByDescending(h => h.Hour.P90Minutes).ThenBy(h => h.Hour.StartUtc).ToList();
        var lane = new WeeklyLane(
            zone.Name,
            zone.LaneCategory,
            days.Sum(d => d.Passengers),
            days.Sum(d => d.Waits),
            Round(WaitStatistics.Percentile(week, 0.5)),
            Round(WaitStatistics.Percentile(week, 0.9)),
            days.Max(d => d.MaxQueueLength),
            best.Count > 0 ? Peak(zone.Name, zone.LaneCategory, calendar.Days[best[0].Day].Date, best[0].Hour) : null,
            days.Any(d => d.Provisional),
            days.Any(d => d.HistogramMissing),
            overflow.Sum(o => o.OverflowMinutes),
            overflow.Sum(o => o.ObservedMinutes),
            overflow.Max(o => o.PeakBandOccupancy),
            alerts.Count,
            seesQuality ? Quality(bins, health) : null,
            days);
        return new BuiltLane(lane, hours);
    }

    private static WeeklyPeakHour Peak(string zone, string laneCategory, DateOnly date, WeeklyHour hour) =>
        new(zone, laneCategory, date, hour.Start, hour.End, hour.StartUtc, hour.Waits, hour.P50Minutes, hour.P90Minutes ?? 0);

    /// <summary>A row's histogram; one that breaks the format (the database already leaves such minutes out) adds nothing.</summary>
    private static IReadOnlyList<(int Bucket, long Count)> Histogram(WeeklyHourRow row)
    {
        var buckets = row.Buckets ?? [];
        var counts = row.Counts ?? [];
        if (buckets.Count != counts.Count)
            return [];
        var pairs = new List<(int Bucket, long Count)>(buckets.Count);
        for (var i = 0; i < buckets.Count; i++)
        {
            if (buckets[i] is < 0 or >= WaitStatistics.Buckets || counts[i] < 0)
                return [];
            pairs.Add((buckets[i], counts[i]));
        }

        return pairs;
    }

    private static double? Round(double? minutes) => minutes.HasValue ? Math.Round(minutes.Value, 2) : null;

    #endregion

    #region Data quality

    private static WeeklyQuality Quality(IReadOnlyList<WeeklyBinRow> bins, IReadOnlyList<WeeklyHealthRow> health)
    {
        var entered = health.Sum(h => h.TracksEntered);
        var exited = health.Sum(h => h.TracksExited);
        var residualBins = health.Sum(h => h.ResidualBins);
        var sumAbs = health.Sum(h => h.SumAbsResidual);
        return new WeeklyQuality(
            bins.Sum(b => b.Bins),
            bins.Sum(b => b.FinalBins),
            bins.Sum(b => b.GoodBins),
            bins.Sum(b => b.DegradedBins),
            bins.Sum(b => b.DegradedByCompletionOnlyBins),
            bins.Sum(b => b.UnknownBins),
            health.Sum(h => h.Bins),
            health.Sum(h => h.FinalBins),
            entered,
            exited,
            Rate(HealthChecks.TrackCompletionRate(entered, exited)),
            health.Sum(h => h.BinsBelowTrackTarget),
            residualBins,
            sumAbs,
            health.Count > 0 ? health.Max(h => h.MaxAbsResidual) : 0,
            residualBins > 0 ? Math.Round((double)sumAbs / residualBins, 2) : null,
            health.Sum(h => h.OccupancyMinutes),
            health.Sum(h => h.CapacityMinutes),
            health.Sum(h => h.MinutesOutsideCapacity));
    }

    /// <summary>The site's data quality: the lanes' counts added, the rates taken again from the sums (never averaged).</summary>
    private static WeeklyQuality Total(IEnumerable<WeeklyQuality> lanes)
    {
        var all = lanes.Where(q => q is not null).ToList();
        var entered = all.Sum(q => q.TracksEntered);
        var exited = all.Sum(q => q.TracksExited);
        var residualBins = all.Sum(q => q.ResidualBins);
        var sumAbs = all.Sum(q => q.SumAbsResidual);
        return new WeeklyQuality(
            all.Sum(q => q.Bins), all.Sum(q => q.FinalBins), all.Sum(q => q.GoodBins), all.Sum(q => q.DegradedBins), all.Sum(q => q.DegradedByCompletionOnlyBins),
            all.Sum(q => q.UnknownBins), all.Sum(q => q.HealthBins), all.Sum(q => q.FinalHealthBins), entered, exited,
            Rate(HealthChecks.TrackCompletionRate(entered, exited)), all.Sum(q => q.BinsBelowTrackTarget), residualBins, sumAbs,
            all.Count > 0 ? all.Max(q => q.MaxAbsResidual) : 0, residualBins > 0 ? Math.Round((double)sumAbs / residualBins, 2) : null,
            all.Sum(q => q.OccupancyMinutes), all.Sum(q => q.CapacityMinutes), all.Sum(q => q.MinutesOutsideCapacity));
    }

    private static double? Rate(double? rate) => rate.HasValue ? Math.Round(rate.Value, 4) : null;

    #endregion

    #region Alerts

    private static WeeklyAlerts Alerts(IReadOnlyList<WeeklyAlertRow> alerts, WeeklyCalendar calendar)
    {
        var bySeverity = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var a in alerts)
            bySeverity[a.Severity ?? string.Empty] = bySeverity.GetValueOrDefault(a.Severity ?? string.Empty) + 1;

        var byRule = alerts.GroupBy(a => a.RuleCode ?? string.Empty, StringComparer.Ordinal)
            .Select(g =>
            {
                var latest = g.OrderBy(a => a.RaisedUtc).Last();
                return new WeeklyAlertRule(g.Key, latest.RuleName, latest.Severity, g.Count(), g.Count(a => a.AcknowledgedUtc.HasValue));
            })
            .OrderByDescending(r => r.Count).ThenBy(r => r.RuleCode, StringComparer.Ordinal)
            .ToList();

        var byDay = calendar.Days.Select(d => new WeeklyCount(d.Date, alerts.Count(a => a.RaisedUtc >= d.FromUtc && a.RaisedUtc < d.ToUtc))).ToList();

        // Time to acknowledge counts the acknowledged alerts only; the others are counted apart, never as zero or as the week's end.
        var minutes = alerts.Where(a => a.AcknowledgedUtc.HasValue)
            .Select(a => Math.Max(0, (a.AcknowledgedUtc.Value - a.RaisedUtc).TotalMinutes)).ToList();
        var percentiles = WaitStatistics.Percentiles(minutes, 0.5, 0.9);
        var unacknowledged = alerts.Where(a => !a.AcknowledgedUtc.HasValue).ToList();
        return new WeeklyAlerts(
            alerts.Count,
            alerts.Count(a => a.Severity == "Critical"),
            bySeverity,
            byRule,
            byDay,
            minutes.Count,
            alerts.Count(a => a.EscalatedUtc.HasValue),
            unacknowledged.Count(a => a.State != "Resolved"),
            unacknowledged.Count(a => a.State == "Resolved"),
            Round(percentiles[0]),
            Round(percentiles[1]),
            minutes.Count > 0 ? Math.Round(minutes.Average(), 2) : null,
            minutes.Count > 0 ? Math.Round(minutes.Max(), 2) : null);
    }

    #endregion

    #region Desk states

    /// <summary>
    /// Time in each desk state. Border checkpoints' lanes go to every reader; for a reader without <c>BorderDesks.View</c> each
    /// border lane is shown cell by cell (<see cref="WeeklyDeskLane"/>): its week only when at least
    /// <see cref="MinDesksForLaneFigures"/> desks had open time in the week, its days only when every day with open time had
    /// at least that many (all days or none, so no withheld day can be had as the week less the days shown), and its desk count
    /// only with its week. Airport checkpoints' lanes go only to a reader with <c>AirportDesks.View</c> (who sees those desks one
    /// by one on the live screen); per-desk rows of border desks only with <c>BorderDesks.View</c>, never an airport desk's (the
    /// handler's own counters wait for its module); any other checkpoint kind is left out.
    /// </summary>
    private static WeeklyDeskStates DeskStates(WeeklySummaryInput input, WeeklyCalendar calendar)
    {
        var rows = (input.Desks ?? [])
            .Where(r => r.Day is >= 0 and < 7 && (BorderKinds.Contains(r.CheckpointKind) || (input.SeesAirportDesks && AirportKinds.Contains(r.CheckpointKind))))
            .ToList();

        var lanes = rows.GroupBy(r => (r.CheckpointKind, Lane: r.Lane ?? string.Empty))
            .Select(g => DeskLane(g.Key.CheckpointKind, g.Key.Lane, g.ToList(), !input.SeesBorderDesks && BorderKinds.Contains(g.Key.CheckpointKind), calendar))
            .OrderBy(l => Array.IndexOf(KindOrder, l.CheckpointKind)).ThenBy(l => l.Lane, StringComparer.Ordinal)
            .ToList();

        IReadOnlyList<WeeklyDesk> desksRows = [];
        if (input.SeesBorderDesks)
        {
            desksRows = rows.Where(r => BorderKinds.Contains(r.CheckpointKind))
                .GroupBy(r => (r.CheckpointKind, r.Checkpoint, r.Desk))
                .Select(g => new WeeklyDesk(g.Key.CheckpointKind, g.Key.Checkpoint, g.Key.Desk,
                    [.. g.Select(r => r.Lane ?? string.Empty).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)], Seconds(g), PerDay(g.ToList(), calendar)))
                .OrderBy(d => Array.IndexOf(KindOrder, d.CheckpointKind)).ThenBy(d => d.Checkpoint, StringComparer.Ordinal).ThenBy(d => d.Desk, StringComparer.Ordinal)
                .ToList();
        }

        return new WeeklyDeskStates(input.SeesBorderDesks, lanes, desksRows);
    }

    private static WeeklyDeskLane DeskLane(string kind, string lane, IReadOnlyList<WeeklyDeskRow> rows, bool smallCellRule, WeeklyCalendar calendar)
    {
        var week = OpenDesks(rows);
        var days = Enumerable.Range(0, 7).Select(d => OpenDesks(rows.Where(r => r.Day == d))).ToList();
        var weekWithheld = smallCellRule && week < MinDesksForLaneFigures;
        // Never only some days: with the week shown, the week less the days shown would give a withheld day.
        var daysWithheld = smallCellRule && (weekWithheld || days.Any(n => n > 0 && n < MinDesksForLaneFigures));
        return new WeeklyDeskLane(kind, lane, weekWithheld ? null : week, weekWithheld, daysWithheld, weekWithheld ? null : Seconds(rows),
            daysWithheld ? [] : PerDay(rows, calendar));
    }

    /// <summary>The desks with open time (idle, serving or paused above 0) in the rows: a closed or unknown desk is not counted.</summary>
    private static int OpenDesks(IEnumerable<WeeklyDeskRow> rows) =>
        rows.GroupBy(r => (r.Checkpoint, r.Desk)).Count(g => g.Sum(r => r.Idle + r.Serving + r.Paused) > 0);

    private static IReadOnlyList<WeeklyDeskDay> PerDay(IReadOnlyList<WeeklyDeskRow> rows, WeeklyCalendar calendar) =>
        [.. calendar.Days.Select((d, i) =>
        {
            var day = rows.Where(r => r.Day == i).ToList();
            return new WeeklyDeskDay(d.Date, OpenDesks(day), Seconds(day));
        })];

    private static DeskStateSeconds Seconds(IEnumerable<WeeklyDeskRow> rows)
    {
        var list = rows.ToList();
        return new DeskStateSeconds(
            list.Sum(r => r.Minutes),
            (long)Math.Round(list.Sum(r => r.Closed)),
            (long)Math.Round(list.Sum(r => r.Idle)),
            (long)Math.Round(list.Sum(r => r.Serving)),
            (long)Math.Round(list.Sum(r => r.Paused)),
            (long)Math.Round(list.Sum(r => r.Unknown)));
    }

    #endregion
}
