using Ariva.Core.Availability;
using Ariva.Core.Queueing;
using Ariva.Core.Reports;
using FluentAssertions;

namespace Ariva.UnitTests.Reports;

/// <summary>
/// ARV-119: the weekly summary's rules. The local week (Monday start) in the site's time zone, with 167 or 169 hours on a
/// clock change, a skipped midnight and a half-hour change; which weeks may be asked for; lane waits merged from hourly
/// histograms per day and week (F7, never averaged) with the daily report's peak rule; overflow minutes; data quality with
/// track completion from the sums; alerts with time to acknowledge over acknowledged alerts only; device uptime clipped
/// to the week and to now; availability per local date; time in each desk state with per-desk rows for border roles only,
/// small border lanes withheld from others and airport lanes only for airport roles; empty days.
/// </summary>
public sealed class WeeklySummaryTests
{
    private static readonly TimeZoneInfo Dubai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Dubai");
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    private static readonly DateOnly Week = new(2026, 9, 28);
    private static readonly DateTime After = new(2026, 10, 6, 8, 0, 0, DateTimeKind.Utc);

    private static readonly ReportZone Cit = new("DMO/A-CIT", "A-CIT", "CIT");
    private static readonly ReportZone Vis = new("DMO/A-VIS", "A-VIS", "VIS");

    /// <summary>Dubai is UTC+4 all year: local day <paramref name="day"/> of the week at hour:minute, in UTC.</summary>
    private static DateTime Local(int day, int hour, int minute = 0) => new DateTime(2026, 9, 28, hour, minute, 0, DateTimeKind.Utc).AddDays(day).AddHours(-4);

    /// <summary>An hour of a Dubai week whose waits all sit in one 30-second bucket (entries = waits).</summary>
    private static WeeklyHourRow Hour(ReportZone zone, int day, int hour, int waits, double minutes, bool provisional = false, int? length = null) =>
        new(zone.ZoneKey, (day * 24) + hour, waits, waits, length, provisional, false,
            waits > 0 ? [WaitStatistics.BucketOf(TimeSpan.FromMinutes(minutes))] : [], waits > 0 ? [waits] : []);

    private static WeeklySummaryInput Input(
        IReadOnlyList<WeeklyHourRow> hours = null,
        IReadOnlyList<WeeklyBinRow> bins = null,
        IReadOnlyList<WeeklyHealthRow> health = null,
        IReadOnlyList<WeeklyOverflowRow> overflow = null,
        IReadOnlyList<WeeklyAlertRow> alerts = null,
        IReadOnlyList<ReportDevice> devices = null,
        IReadOnlyDictionary<DateOnly, AvailabilityCounts> availability = null,
        IReadOnlyList<WeeklyDeskRow> desks = null,
        IReadOnlyList<ReportZone> zones = null,
        DateTime? now = null,
        bool border = true,
        bool airport = false,
        bool quality = true,
        TimeZoneInfo zone = null,
        DateOnly? week = null) =>
        new("DMO", week ?? Week, zone ?? Dubai, now ?? After, border, airport, quality, zones ?? [Cit, Vis], hours ?? [], bins ?? [], health ?? [], overflow ?? [],
            alerts ?? [], devices ?? [], availability ?? new Dictionary<DateOnly, AvailabilityCounts>(), desks ?? []);

    #region Week and calendar

    [Fact]
    public void Calendar_Should_Have168LocalHours_When_TheSiteHasNoClockChange()
    {
        var calendar = WeeklySummaries.Calendar(Week, Dubai);

        calendar.FromUtc.Should().Be(new DateTime(2026, 9, 27, 20, 0, 0, DateTimeKind.Utc), "Monday 00:00 in Dubai");
        calendar.ToUtc.Should().Be(new DateTime(2026, 10, 4, 20, 0, 0, DateTimeKind.Utc), "the next Monday 00:00");
        calendar.Days.Select(d => d.Date).Should().Equal(Enumerable.Range(0, 7).Select(Week.AddDays));
        calendar.Hours.Should().HaveCount(168);
        calendar.Hours[0].Should().Match<WeeklyCalendarHour>(h => h.Day == 0 && h.Start == "00:00" && h.End == "01:00");
        calendar.Hours[^1].Should().Match<WeeklyCalendarHour>(h => h.Day == 6 && h.Start == "23:00" && h.End == "00:00" && h.EndUtc == calendar.ToUtc);
    }

    [Fact]
    public void Calendar_Should_Have167Or169Hours_When_TheWeekHasAClockChange()
    {
        var spring = WeeklySummaries.Calendar(new DateOnly(2026, 3, 23), Berlin);
        var autumn = WeeklySummaries.Calendar(new DateOnly(2026, 10, 19), Berlin);

        spring.Hours.Should().HaveCount(167);
        spring.Hours.Where(h => h.Day == 6).Select(h => h.Start).Should().StartWith(["00:00", "01:00", "03:00"], "02:00 does not exist on 29 March");
        autumn.Hours.Should().HaveCount(169);
        var sunday = autumn.Hours.Where(h => h.Day == 6).ToList();
        sunday.Should().HaveCount(25);
        sunday.Count(h => h.Start == "02:00").Should().Be(2, "02:00 happens twice on 25 October");
        sunday.Where(h => h.Start == "02:00").Select(h => h.StartUtc).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Calendar_Should_StartTheDayAfterASkippedMidnight_When_TheClockSkipsIt()
    {
        var beirut = TimeZoneInfo.FindSystemTimeZoneById("Asia/Beirut");

        var calendar = WeeklySummaries.Calendar(new DateOnly(2026, 3, 23), beirut);

        calendar.Days[6].FromUtc.Should().Be(new DateTime(2026, 3, 28, 22, 0, 0, DateTimeKind.Utc), "29 March starts at 01:00 local");
        calendar.Days[5].ToUtc.Should().Be(calendar.Days[6].FromUtc, "the days follow each other without a gap");
        calendar.Hours.Where(h => h.Day == 6).Should().HaveCount(23).And.Subject.First().Start.Should().Be("01:00");
    }

    [Fact]
    public void Calendar_Should_EndAShortLastHour_When_TheClockMovesHalfAnHour()
    {
        // Lord Howe Island moves from 02:00 to 02:30 on the first Sunday of October: that Sunday has 23.5 hours.
        var lordHowe = TimeZoneInfo.FindSystemTimeZoneById("Australia/Lord_Howe");

        var sunday = WeeklySummaries.Calendar(Week, lordHowe).Hours.Where(h => h.Day == 6).ToList();

        sunday.Select(h => h.Start).Should().StartWith(["00:00", "01:00", "02:30"]);
        sunday[^1].Should().Match<WeeklyCalendarHour>(h => h.Start == "23:30" && h.End == "00:00" && h.EndUtc - h.StartUtc == TimeSpan.FromMinutes(30));
        sunday.Sum(h => (h.EndUtc - h.StartUtc).TotalHours).Should().Be(23.5);
    }

    [Theory]
    [InlineData("2026-09-28", true)]
    [InlineData("2026-10-05", false)]
    [InlineData("2026-09-29", false)]
    [InlineData("2025-08-25", true)]
    [InlineData("2025-08-18", false)]
    [InlineData(null, false)]
    public void IsValidWeek_Should_AcceptAStartedMondayUpTo57WeeksBack_When_Asked(string weekStart, bool valid)
    {
        // Thursday 1 October at the site: the current week started on Monday 28 September.
        DateOnly? start = weekStart is null ? null : DateOnly.Parse(weekStart, System.Globalization.CultureInfo.InvariantCulture);

        WeeklySummaries.IsValidWeek(start, new DateOnly(2026, 10, 1)).Should().Be(valid);
    }

    [Fact]
    public void IsValidWeek_Should_TakeTheCurrentWeek_When_TodayIsItsFirstOrLastDay()
    {
        WeeklySummaries.IsValidWeek(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 5)).Should().BeTrue("a Monday's own week has started");
        WeeklySummaries.IsValidWeek(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 4)).Should().BeFalse("on Sunday the next week has not started");
        WeeklySummaries.WeekStartOf(new DateOnly(2026, 10, 4)).Should().Be(Week);
    }

    [Fact]
    public void Instants_Should_WriteUtcIso8601_When_TheDatabaseGroupsByThem()
    {
        WeeklySummaries.Instants([new DateTime(2026, 9, 27, 20, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 27, 21, 30, 0, DateTimeKind.Utc)])
            .Should().Be("2026-09-27T20:00:00Z,2026-09-27T21:30:00Z");
    }

    #endregion

    #region Lane waits and peaks

    [Fact]
    public void Lane_Should_MergeHourHistograms_When_PercentilesAreTakenForADayAndTheWeek()
    {
        // Monday 09:00: 17 waits of 5 minutes; 10:00: 3 of 20. Thursday 18:00: 25 of 3.
        var summary = WeeklySummaries.Build(Input([Hour(Cit, 0, 9, 17, 5), Hour(Cit, 0, 10, 3, 20), Hour(Cit, 3, 18, 25, 3)]));

        var cit = summary.Lanes.Single(l => l.Zone == "A-CIT");
        var monday = cit.Days[0];
        monday.Hours.Should().HaveCount(24);
        monday.Hours[9].Should().Match<WeeklyHour>(h => h.Start == "09:00" && h.Waits == 17 && h.P90Minutes == 5.5 && h.StartUtc == Local(0, 9));
        monday.Should().Match<WeeklyLaneDay>(d => d.Passengers == 20 && d.Waits == 20 && d.P50Minutes == 5.5);
        monday.P90Minutes.Should().Be(20.5, "rank 18 of 20 merged; the average of the hours' P90s would say 13");
        cit.Days[3].Should().Match<WeeklyLaneDay>(d => d.P50Minutes == 3.5 && d.P90Minutes == 3.5 && d.Waits == 25);
        cit.Should().Match<WeeklyLane>(l => l.Passengers == 45 && l.Waits == 45 && l.LaneCategory == "CIT");
        cit.P50Minutes.Should().Be(3.5, "rank 23 of 45 is among the 25 waits of 3 minutes");
        cit.P90Minutes.Should().Be(5.5, "rank 41 of 45 is among the 17 waits of 5 minutes");
        summary.Lanes.Select(l => l.Zone).Should().Equal("A-CIT", "A-VIS");
    }

    [Fact]
    public void Peak_Should_BeTheWorstHourWithEnoughWaits_When_AQuietHourHasOneLongWait()
    {
        var summary = WeeklySummaries.Build(Input([
            Hour(Cit, 0, 6, 1, 40), // one passenger waiting 40 minutes: too few waits to be a peak
            Hour(Cit, 0, 8, 30, 12),
            Hour(Cit, 0, 17, 30, 18, length: 140),
            Hour(Vis, 0, 17, 30, 9),
            Hour(Cit, 1, 19, 30, 25),
            Hour(Cit, 2, 9, 19, 50) // 19 waits: one short of a peak
        ]));

        var cit = summary.Lanes.Single(l => l.Zone == "A-CIT");
        cit.Days[0].Peak.Should().Match<WeeklyHour>(h => h.Start == "17:00" && h.P90Minutes == 18.5 && h.MaxQueueLength == 140);
        cit.Days[2].Peak.Should().BeNull("no hour of Wednesday has 20 waits");
        cit.Days[2].P90Minutes.Should().Be(50.5, "the day's own percentiles count every wait");
        cit.Peak.Should().Match<WeeklyPeakHour>(p => p.Date == Week.AddDays(1) && p.Start == "19:00" && p.End == "20:00" && p.P90Minutes == 25.5 && p.Waits == 30);
        cit.MaxQueueLength.Should().Be(140);
        summary.PeakHours.Select(p => (p.Zone, p.Date, p.Start, p.P90Minutes)).Should().Equal(
            ("A-CIT", Week.AddDays(1), "19:00", 25.5), ("A-CIT", Week, "17:00", 18.5), ("A-CIT", Week, "08:00", 12.5), ("A-VIS", Week, "17:00", 9.5));
    }

    [Fact]
    public void PeakHours_Should_ListTheTenWorstEarliestFirstOnATie_When_TheWeekHasMore()
    {
        var hours = Enumerable.Range(0, 12).Select(i => Hour(i % 2 == 0 ? Cit : Vis, i % 7, 8 + i, 20, i < 2 ? 30 : 10)).ToList();

        var summary = WeeklySummaries.Build(Input(hours));

        summary.PeakHours.Should().HaveCount(WeeklySummaries.MaxPeakHours);
        summary.PeakHours.Take(2).Select(p => p.Zone).Should().Equal(["A-CIT", "A-VIS"], "both 30.5; Monday 08:00 is before Tuesday 09:00");
        summary.PeakHours.Skip(2).Should().OnlyContain(p => p.P90Minutes == 10.5);
        summary.PeakHours.Skip(2).Select(p => p.StartUtc).Should().BeInAscendingOrder();
    }

    [Fact]
    public void Days_Should_ListEveryLocalDayWithNothingInIt_When_TheWeekHasNoData()
    {
        var summary = WeeklySummaries.Build(Input());

        summary.Days.Should().HaveCount(7).And.OnlyContain(d => d.Hours == 24 && d.Started && d.Ended);
        summary.Complete.Should().BeTrue();
        summary.Hours.Should().Be(168);
        summary.Lanes.Should().HaveCount(2).And.OnlyContain(l => l.Passengers == 0 && l.P50Minutes == null && l.P90Minutes == null && l.Peak == null && l.MaxQueueLength == null);
        summary.Lanes[0].Days.Should().HaveCount(7).And.OnlyContain(d => d.Waits == 0 && d.P90Minutes == null && d.Peak == null && d.Hours.Count == 24 && d.Bins == 0);
        summary.PeakHours.Should().BeEmpty();
        summary.Alerts.Should().Match<WeeklyAlerts>(a => a.Total == 0 && a.AcknowledgeP50Minutes == null && a.AcknowledgeMaxMinutes == null && a.ByDay.Count == 7);
        summary.Availability.Total.Availability.Should().BeNull("no operating minute was recorded");
        summary.Availability.Days.Should().HaveCount(7);
        summary.DataQuality.Should().Match<WeeklyQuality>(q => q.Bins == 0 && q.TrackCompletion == null && q.MeanAbsResidual == null);
        summary.DeskStates.Lanes.Should().BeEmpty();
        summary.Devices.Should().BeEmpty();
    }

    [Fact]
    public void Hours_Should_FallOnTheirLocalDay_When_TheWeekEndsWithTheAutumnChange()
    {
        // Berlin, Sunday 25 October 2026: the hours 144 to 168 are Sunday's 25; 146 and 147 are the two 02:00s.
        var week = new DateOnly(2026, 10, 19);
        var summary = WeeklySummaries.Build(Input(
            [new WeeklyHourRow(Cit.ZoneKey, 146, 20, 20, null, false, false, [8], [20]), new WeeklyHourRow(Cit.ZoneKey, 147, 20, 20, null, false, false, [20], [20])],
            zone: Berlin, week: week, now: new DateTime(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc)));

        summary.Hours.Should().Be(169);
        summary.Days[6].Should().Match<WeeklyDay>(d => d.Hours == 25 && d.FromUtc == new DateTime(2026, 10, 24, 22, 0, 0, DateTimeKind.Utc));
        var sunday = summary.Lanes.Single(l => l.Zone == "A-CIT").Days[6];
        sunday.Passengers.Should().Be(40);
        sunday.Hours.Where(h => h.Start == "02:00").Select(h => (h.StartUtc, h.Waits)).Should().Equal(
            (new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc), 20L), (new DateTime(2026, 10, 25, 1, 0, 0, DateTimeKind.Utc), 20L));
        sunday.Peak.StartUtc.Should().Be(new DateTime(2026, 10, 25, 1, 0, 0, DateTimeKind.Utc), "the second 02:00 has the longer waits");
        summary.Lanes.Single(l => l.Zone == "A-CIT").Days.Take(6).Should().OnlyContain(d => d.Passengers == 0);
    }

    [Fact]
    public void Flags_Should_SayProvisionalOrMissing_When_AnHourSaysSo()
    {
        var missing = new WeeklyHourRow(Cit.ZoneKey, 10, 5, 5, null, false, true, [], []);
        var summary = WeeklySummaries.Build(Input([missing, Hour(Cit, 1, 11, 5, 3, provisional: true)]));

        var cit = summary.Lanes.Single(l => l.Zone == "A-CIT");
        cit.Days[0].Hours[10].Should().Match<WeeklyHour>(h => h.HistogramMissing && h.P90Minutes == null && h.Waits == 5);
        cit.Days[0].Should().Match<WeeklyLaneDay>(d => d.HistogramMissing && !d.Provisional);
        cit.Days[1].Should().Match<WeeklyLaneDay>(d => d.Provisional && !d.HistogramMissing);
        cit.Should().Match<WeeklyLane>(l => l.Provisional && l.HistogramMissing);
        summary.Lanes.Single(l => l.Zone == "A-VIS").Provisional.Should().BeFalse();
    }

    [Fact]
    public void Lane_Should_IgnoreRowsOutsideTheWeekOrOfAnotherZone_When_GivenThem()
    {
        var summary = WeeklySummaries.Build(Input([
            new WeeklyHourRow(Cit.ZoneKey, 168, 99, 99, null, false, false, [2], [99]),
            new WeeklyHourRow(Cit.ZoneKey, -1, 99, 99, null, false, false, [2], [99]),
            new WeeklyHourRow("ALX/A-CIT", 5, 99, 99, null, false, false, [2], [99]),
            new WeeklyHourRow(Cit.ZoneKey, 6, 3, 3, null, false, false, [-1], [3])
        ]));

        var cit = summary.Lanes.Single(l => l.Zone == "A-CIT");
        cit.Passengers.Should().Be(3, "only the row of this zone inside the week counts");
        cit.P90Minutes.Should().BeNull("a histogram that breaks the format adds nothing");
    }

    #endregion

    #region Overflow and data quality

    [Fact]
    public void Overflow_Should_AddTheDaysMinutes_When_BandsWereOccupied()
    {
        var summary = WeeklySummaries.Build(Input(overflow: [
            new WeeklyOverflowRow(Vis.ZoneKey, 0, 12, 180, 9),
            new WeeklyOverflowRow(Vis.ZoneKey, 3, 5, 60, 14),
            new WeeklyOverflowRow(Vis.ZoneKey, 9, 99, 99, 99)
        ]));

        var vis = summary.Lanes.Single(l => l.Zone == "A-VIS");
        vis.Should().Match<WeeklyLane>(l => l.OverflowMinutes == 17 && l.OverflowObservedMinutes == 240 && l.PeakBandOccupancy == 14);
        vis.Days.Select(d => d.OverflowMinutes).Should().Equal(12, 0, 0, 5, 0, 0, 0);
        summary.Lanes.Single(l => l.Zone == "A-CIT").Should().Match<WeeklyLane>(l => l.OverflowMinutes == 0 && l.PeakBandOccupancy == null);
    }

    [Fact]
    public void Quality_Should_AddBinsAndTakeTrackCompletionFromTheSums_When_LanesHaveChecks()
    {
        var res = new ReportZone("DMO/A-RES", "A-RES", "RES");
        var summary = WeeklySummaries.Build(Input(
            zones: [Cit, Vis, res],
            bins: [
                new WeeklyBinRow(Cit.ZoneKey, 0, 96, 96, 90, 5, 3, 1),
                new WeeklyBinRow(Cit.ZoneKey, 1, 96, 90, 96, 0, 0, 0),
                new WeeklyBinRow(Vis.ZoneKey, 0, 96, 96, 96, 0, 0, 0)
            ],
            health: [
                new WeeklyHealthRow(Cit.ZoneKey, 0, 96, 96, 100, 95, 1, 90, 45, 4, 1440, 1440, 3),
                new WeeklyHealthRow(Vis.ZoneKey, 0, 96, 96, 900, 891, 0, 0, 0, 0, 1440, 0, 0)
            ]));

        var cit = summary.Lanes.Single(l => l.Zone == "A-CIT");
        cit.Quality.Should().Match<WeeklyQuality>(q => q.Bins == 192 && q.FinalBins == 186 && q.GoodBins == 186 && q.DegradedBins == 5 &&
                                                       q.DegradedByCompletionOnlyBins == 3 && q.UnknownBins == 1 && q.BinsBelowTrackTarget == 1);
        cit.Quality.TrackCompletion.Should().Be(0.95);
        cit.Quality.MeanAbsResidual.Should().Be(0.5);
        cit.Days.Select(d => d.DegradedBins).Should().Equal(5, 0, 0, 0, 0, 0, 0);
        summary.Lanes.Single(l => l.Zone == "A-VIS").Quality.Should().Match<WeeklyQuality>(q => q.TrackCompletion == 0.99 && q.MeanAbsResidual == null);
        summary.Lanes.Single(l => l.Zone == "A-RES").Quality.Should().Match<WeeklyQuality>(q => q.TrackCompletion == null && q.HealthBins == 0);
        summary.DataQuality.TrackCompletion.Should().Be(0.986, "986 of 1,000 tracks; the average of the lanes' rates would say 0.97");
        summary.DataQuality.Should().Match<WeeklyQuality>(q => q.Bins == 288 && q.MaxAbsResidual == 4 && q.CapacityMinutes == 1440 && q.MinutesOutsideCapacity == 3);
    }

    [Fact]
    public void Quality_Should_BeLeftOut_When_TheReaderMayNotSeeDataQuality()
    {
        var summary = WeeklySummaries.Build(Input(
            bins: [new WeeklyBinRow(Cit.ZoneKey, 0, 96, 96, 90, 5, 3, 1)],
            availability: new Dictionary<DateOnly, AvailabilityCounts> { [Week] = new(1440, 1440, 1440, 0, 0, 0, 0, 0, 0, 0, 0) },
            quality: false));

        summary.DataQuality.Should().BeNull();
        summary.Availability.Should().BeNull();
        summary.Lanes.Should().OnlyContain(l => l.Quality == null && l.Days.All(d => d.Bins == null && d.DegradedBins == null));
    }

    [Fact]
    public void Availability_Should_FillTheWeeksDatesAndAddThem_When_TheLedgerHasSomeDays()
    {
        var summary = WeeklySummaries.Build(Input(availability: new Dictionary<DateOnly, AvailabilityCounts>
        {
            [Week] = new(1440, 1440, 1430, 10, 0, 0, 0, 10, 0, 0, 0),
            [Week.AddDays(1)] = new(1440, 1440, 1440, 0, 0, 0, 0, 0, 0, 0, 0),
            [Week.AddDays(7)] = new(1440, 1440, 0, 1440, 0, 0, 0, 0, 0, 0, 0)
        }));

        summary.Availability.Days.Select(d => d.Date).Should().Equal(Enumerable.Range(0, 7).Select(Week.AddDays));
        summary.Availability.Total.Should().Match<AvailabilityCounts>(c => c.RecordedMinutes == 2880 && c.AvailableMinutes == 2870 && c.StaleZoneMinutes == 10);
        summary.Availability.Total.Availability.Should().BeApproximately(2870.0 / 2880, 1e-12);
        summary.Availability.Days[2].Counts.Availability.Should().BeNull("Wednesday has no operating minute");
        summary.Availability.PilotTarget.Should().Be(0.99);
    }

    #endregion

    #region Alerts and devices

    [Fact]
    public void Alerts_Should_CountTimeToAcknowledgeOverAcknowledgedAlertsOnly_When_SomeWereNeverAcknowledged()
    {
        WeeklyAlertRow Alert(string rule, string zone, string severity, string state, DateTime raised, DateTime? acknowledged = null, DateTime? escalated = null) =>
            new(rule, rule == "R-001" ? "Long wait" : rule == "R-002" ? "Device offline" : "Overflow", zone, severity, state, raised, acknowledged, escalated);

        var summary = WeeklySummaries.Build(Input(alerts: [
            Alert("R-001", "A-CIT", "Critical", "Resolved", Local(0, 17, 5), Local(0, 17, 7)),
            Alert("R-001", "A-CIT", "Critical", "Resolved", Local(1, 9), Local(1, 9, 4), Local(1, 9, 3)),
            Alert("R-002", "A-VIS", "Warning", "Acknowledged", Local(2, 10), Local(2, 10, 6)),
            Alert("R-002", "A-VIS", "Warning", "Resolved", Local(2, 23, 50), Local(3, 0, 20)), // acknowledged on Thursday, counted on Wednesday
            Alert("R-003", "A-VIS", "Info", "Raised", Local(4, 12)),
            Alert("R-003", "A-VIS", "Info", "Resolved", Local(5, 12)),
            Alert("R-001", "A-CIT", "Critical", "Resolved", Local(0, 0).AddMinutes(-1), Local(0, 0, 5)), // Sunday before the week
            Alert("R-001", "A-CIT", "Critical", "Raised", Local(7, 0)) // the next Monday
        ]));

        var alerts = summary.Alerts;
        alerts.Should().Match<WeeklyAlerts>(a => a.Total == 6 && a.Critical == 2 && a.Acknowledged == 4 && a.Escalated == 1 && a.UnacknowledgedOpen == 1 &&
                                                 a.UnacknowledgedClosed == 1);
        alerts.BySeverity.Should().BeEquivalentTo(new Dictionary<string, int> { ["Critical"] = 2, ["Info"] = 2, ["Warning"] = 2 });
        alerts.AcknowledgeP50Minutes.Should().Be(4, "nearest rank 2 of 2, 4, 6 and 30 minutes");
        alerts.AcknowledgeP90Minutes.Should().Be(30);
        alerts.AcknowledgeMeanMinutes.Should().Be(10.5);
        alerts.AcknowledgeMaxMinutes.Should().Be(30);
        alerts.ByDay.Select(d => d.Count).Should().Equal(1, 1, 2, 0, 1, 1, 0);
        alerts.ByRule.Select(r => (r.RuleCode, r.Count, r.Acknowledged)).Should().Equal(("R-001", 2, 2), ("R-002", 2, 2), ("R-003", 2, 0));
        summary.Lanes.Single(l => l.Zone == "A-VIS").Should().Match<WeeklyLane>(l => l.Alerts == 4 && l.Days[2].Alerts == 2);
        summary.Lanes.Single(l => l.Zone == "A-CIT").Alerts.Should().Be(2);
    }

    [Fact]
    public void Devices_Should_ClipOutagesToTheWeekAndNow_When_TheWeekIsStillRunning()
    {
        // Wednesday 12:00 at the site: 3,600 minutes of the week have passed.
        var now = Local(2, 12);
        var summary = WeeklySummaries.Build(Input(now: now, devices: [
            new ReportDevice("S-1", "A-CIT", [(Local(0, 0).AddHours(-1), Local(0, 1))]),
            new ReportDevice("S-2", "A-VIS", [(Local(1, 14), null)]),
            new ReportDevice("S-3", "A-VIS", [])
        ]));

        summary.Complete.Should().BeFalse();
        summary.GeneratedUtc.Should().Be(now);
        summary.Days.Select(d => (d.Started, d.Ended)).Should().Equal((true, true), (true, true), (true, false), (false, false), (false, false), (false, false), (false, false));
        summary.Devices.Select(d => (d.Code, d.UptimePercent, d.OutageMinutes)).Should().Equal(("S-1", 98.33, 60), ("S-2", 63.33, 1320), ("S-3", 100.0, 0));
    }

    [Fact]
    public void Summary_Should_RoundTripThroughJson_When_HandedOnAsADocument()
    {
        // ARV-146 hands the figures to the report tool: every value must survive JSON (value tuples, for one, would not).
        var summary = WeeklySummaries.Build(Input(
            hours: [Hour(Cit, 0, 9, 30, 5), Hour(Vis, 2, 18, 25, 12, provisional: true)],
            bins: [new WeeklyBinRow(Cit.ZoneKey, 0, 96, 96, 90, 5, 3, 1)],
            health: [new WeeklyHealthRow(Cit.ZoneKey, 0, 96, 96, 100, 95, 1, 90, 45, 4, 1440, 1440, 3)],
            overflow: [new WeeklyOverflowRow(Vis.ZoneKey, 2, 12, 180, 9)],
            alerts: [new WeeklyAlertRow("R-001", "Long wait", "A-CIT", "Critical", "Resolved", Local(0, 17, 5), Local(0, 17, 7), null)],
            devices: [new ReportDevice("S-1", "A-CIT", [(Local(0, 1), Local(0, 2))])],
            availability: new Dictionary<DateOnly, AvailabilityCounts> { [Week] = new(1440, 1440, 1430, 10, 0, 0, 0, 10, 0, 0, 0) },
            desks: DeskRows));

        var json = System.Text.Json.JsonSerializer.Serialize(summary, System.Text.Json.JsonSerializerOptions.Web);
        var back = System.Text.Json.JsonSerializer.Deserialize<WeeklySummary>(json, System.Text.Json.JsonSerializerOptions.Web);

        back.Should().BeEquivalentTo(summary);
        json.Should().Contain("\"weekStart\":\"2026-09-28\"").And.Contain("\"acknowledgeP50Minutes\":2");
    }

    #endregion

    #region Desk states

    // Immigration CIT: three desks open over the week, but only AR-02 on Monday and two on Tuesday. CRW: one desk. Emigration
    // CIT: one desk, closed all the time. A check-in counter, a desk of an unknown checkpoint kind and a row outside the week.
    private static readonly WeeklyDeskRow[] DeskRows =
    [
        new("Immigration", "IMM", "AR-01", "CRW", 0, 60, 0, 600, 3000, 0, 0),
        new("Immigration", "IMM", "AR-02", "CIT", 0, 60, 0, 0, 3600, 0, 0),
        new("Immigration", "IMM", "AR-03", "CIT", 1, 60, 0, 3600, 0, 0, 0),
        new("Immigration", "IMM", "AR-04", "CIT", 1, 30, 0, 0, 0, 1800, 0),
        new("Emigration", "EMI", "DP-01", "CIT", 2, 10, 600, 0, 0, 0, 0),
        new("CheckIn", "CI", "A01", "", 0, 60, 0, 0, 3600, 0, 0),
        new("Unlisted", "X", "D1", "CIT", 0, 60, 0, 0, 0, 0, 3600),
        new("Immigration", "IMM", "AR-05", "RES", 9, 60, 0, 0, 3600, 0, 0)
    ];

    private static WeeklyDeskRow Desk(string desk, int day, double serving, double closed = 0, string lane = "CIT") =>
        new("Immigration", "IMM", desk, lane, day, (int)((serving + closed) / 60), closed, 0, serving, 0, 0);

    [Fact]
    public void DeskStates_Should_GiveEveryBorderDeskToABorderRole_When_ItHoldsBorderDesks()
    {
        var states = WeeklySummaries.Build(Input(desks: DeskRows, border: true, airport: false)).DeskStates;

        states.DesksIncluded.Should().BeTrue();
        states.Desks.Select(d => d.Desk).Should().Equal(["AR-01", "AR-02", "AR-03", "AR-04", "DP-01"], "border desks only; a check-in counter never has a row");
        states.Desks[0].Should().Match<WeeklyDesk>(d => d.Checkpoint == "IMM" && d.CheckpointKind == "Immigration" && d.Lanes.Single() == "CRW");
        states.Desks[0].Week.Should().Be(new DeskStateSeconds(60, 0, 600, 3000, 0, 0));
        states.Desks[0].Days[0].Desks.Should().Be(1, "open that day");
        states.Desks.Single(d => d.Desk == "DP-01").Days[2].Desks.Should().Be(0, "closed all its minutes");
        states.Lanes.Select(l => (l.CheckpointKind, l.Lane, l.Desks, l.WeekWithheld, l.DaysWithheld)).Should().Equal(
            ("Immigration", "CIT", 3, false, false), ("Immigration", "CRW", 1, false, false), ("Emigration", "CIT", 0, false, false));
        var cit = states.Lanes[0];
        cit.Week.Should().Be(new DeskStateSeconds(150, 0, 3600, 3600, 1800, 0));
        cit.Days.Should().HaveCount(7);
        cit.Days[0].Should().Be(new WeeklyDeskDay(Week, 1, new DeskStateSeconds(60, 0, 0, 3600, 0, 0)));
        cit.Days[1].Should().Be(new WeeklyDeskDay(Week.AddDays(1), 2, new DeskStateSeconds(90, 0, 3600, 0, 1800, 0)));
    }

    [Fact]
    public void DeskStates_Should_GiveAnAirportRoleNoDeskRowAndNoCellOfFewerThanThreeOpenDesks_When_ItLacksBorderDesks()
    {
        var states = WeeklySummaries.Build(Input(desks: DeskRows, border: false, airport: true)).DeskStates;

        states.DesksIncluded.Should().BeFalse();
        states.Desks.Should().BeEmpty("per-desk figures are border data");
        states.Lanes.Select(l => (l.CheckpointKind, l.Lane, l.Desks, l.WeekWithheld, l.DaysWithheld)).Should().Equal(
            ("Immigration", "CIT", 3, false, true), ("Immigration", "CRW", null, true, true), ("Emigration", "CIT", null, true, true), ("CheckIn", "", 1, false, false));
        var cit = states.Lanes[0];
        cit.Week.Serving.Should().Be(3600, "three desks were open over the week");
        cit.Days.Should().BeEmpty("Monday's figures would be AR-02's own, and showing only Tuesday would let the week less Tuesday give Monday");
        states.Lanes.Where(l => l.WeekWithheld).Should().OnlyContain(l => l.Week == null && l.Desks == null && l.Days.Count == 0);
        states.Lanes.Single(l => l.CheckpointKind == "CheckIn").Days.Should().HaveCount(7, "an airport role sees check-in counters one by one live");
    }

    [Fact]
    public void DeskStates_Should_WithholdALaneOfThreeConfiguredDesks_When_OnlyOneWasOpen()
    {
        // The desk engine writes every desk's minute, Closed included: three desks with minutes, one of them open.
        var rows = new[] { Desk("AR-02", 0, 3600), Desk("AR-03", 0, 0, closed: 3600), Desk("AR-04", 0, 0, closed: 3600) };

        var others = WeeklySummaries.Build(Input(desks: rows, border: false)).DeskStates.Lanes.Single();
        var border = WeeklySummaries.Build(Input(desks: rows, border: true)).DeskStates.Lanes.Single();

        others.Should().Match<WeeklyDeskLane>(l => l.WeekWithheld && l.DaysWithheld && l.Desks == null && l.Week == null && l.Days.Count == 0,
            "the open seconds are one desk's, however many closed desks sit beside it");
        border.Should().Match<WeeklyDeskLane>(l => l.Desks == 1 && !l.WeekWithheld && l.Week.Serving == 3600 && l.Week.Closed == 7200);
    }

    [Fact]
    public void DeskStates_Should_ShowNoDayAndNothingThatGivesIt_When_ADayHadOneOpenDesk()
    {
        // Three desks open on Monday; on Tuesday AR-02 alone.
        var rows = new[] { Desk("AR-02", 0, 600), Desk("AR-03", 0, 1200), Desk("AR-04", 0, 1800), Desk("AR-02", 1, 3000), Desk("AR-03", 1, 0, closed: 3600) };

        var summary = WeeklySummaries.Build(Input(desks: rows, border: false));
        var lane = summary.DeskStates.Lanes.Single();

        lane.Should().Match<WeeklyDeskLane>(l => !l.WeekWithheld && l.DaysWithheld && l.Desks == 3 && l.Days.Count == 0);
        lane.Week.Should().Be(new DeskStateSeconds(170, 3600, 0, 6600, 0, 0));
        summary.DeskStates.Desks.Should().BeEmpty();
        var json = System.Text.Json.JsonSerializer.Serialize(summary.DeskStates, System.Text.Json.JsonSerializerOptions.Web);
        json.Should().NotContain("2026-09-28").And.NotContain("2026-09-29").And.NotContain("3000", "no day cell, and no figure of AR-02's Tuesday, is published");
    }

    [Fact]
    public void DeskStates_Should_ShowEveryDay_When_EachDayWithOpenTimeHadThreeDesks()
    {
        // Monday and Wednesday with three open desks; Tuesday all closed (no open time, so no small cell).
        var rows = new[]
        {
            Desk("AR-02", 0, 600), Desk("AR-03", 0, 600), Desk("AR-04", 0, 600),
            Desk("AR-02", 1, 0, closed: 3600), Desk("AR-03", 1, 0, closed: 3600),
            Desk("AR-02", 2, 60), Desk("AR-03", 2, 120), Desk("AR-04", 2, 180)
        };

        var lane = WeeklySummaries.Build(Input(desks: rows, border: false)).DeskStates.Lanes.Single();

        lane.Should().Match<WeeklyDeskLane>(l => !l.WeekWithheld && !l.DaysWithheld && l.Desks == 3);
        lane.Days.Select(d => d.Desks).Should().Equal(3, 0, 3, 0, 0, 0, 0);
        lane.Days[1].Seconds.Should().Be(new DeskStateSeconds(120, 7200, 0, 0, 0, 0));
        lane.Days.Sum(d => d.Seconds.Serving).Should().Be(lane.Week.Serving, "the days shown add up to the week");
    }

    [Fact]
    public void DeskStates_Should_ShowAirportLanesOnlyToAirportRoles_When_ReadByOthers()
    {
        WeeklySummaries.Build(Input(desks: DeskRows, border: true, airport: false)).DeskStates.Lanes.Should().NotContain(l => l.CheckpointKind == "CheckIn");
        WeeklySummaries.Build(Input(desks: DeskRows, border: false, airport: false)).DeskStates.Lanes.Select(l => l.CheckpointKind).Should()
            .Equal("Immigration", "Immigration", "Emigration");
        var both = WeeklySummaries.Build(Input(desks: DeskRows, border: true, airport: true)).DeskStates;
        both.Lanes.Should().Contain(l => l.CheckpointKind == "CheckIn" && !l.WeekWithheld && !l.DaysWithheld);
        both.Desks.Should().NotContain(d => d.CheckpointKind == "CheckIn" || d.Desk == "D1" || d.Desk == "AR-05", "airport desks, unknown kinds and rows outside the week never");
    }

    #endregion
}
