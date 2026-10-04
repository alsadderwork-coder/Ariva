using Ariva.Core.Queueing;
using Ariva.Core.Reports;
using FluentAssertions;

namespace Ariva.UnitTests.Reports;

/// <summary>
/// ARV-060: the daily report merges each hour's minute histograms for P50 and P90 (F7, never averaging percentiles),
/// finds each lane's peak hour among hours with enough waits, counts the day's alerts and computes device uptime, all in
/// the site's local day; its CSV never lets a cell run as a formula.
/// </summary>
public sealed class DailyReportTests
{
    private static readonly TimeZoneInfo Dubai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Dubai");
    private static readonly DateOnly Day = new(2026, 10, 3);

    // 04:00 local in Dubai (UTC+4) is 00:00 UTC.
    private static DateTime Local(int hour, int minute = 0) => new DateTime(2026, 10, 3, hour, minute, 0, DateTimeKind.Utc).AddHours(-4);

    /// <summary>A minute whose waits all sit in one 30-second bucket.</summary>
    private static ReportMinute Minute(string zone, DateTime utc, int waits, double minutes, bool final = true, int? length = null) =>
        new(zone, utc, final, waits, waits, length, waits > 0 ? [WaitStatistics.BucketOf(TimeSpan.FromMinutes(minutes))] : [], waits > 0 ? [waits] : []);

    private static DailyReportInput Input(IReadOnlyList<ReportMinute> minutes, IReadOnlyList<ReportAlert> alerts = null, IReadOnlyList<ReportDevice> devices = null) =>
        new("DMO", Day, Dubai,
            [new ReportZone("DMO/A-CIT", "A-CIT", "CIT"), new ReportZone("DMO/A-VIS", "A-VIS", "VIS")],
            minutes, alerts ?? [], devices ?? [], new DateTime(2026, 10, 4, 6, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void Hour_Should_MergeHistograms_When_MinutesDiffer()
    {
        // 17 waits of 5 minutes in one minute and 3 of 20 minutes in the next: the hour's P90 (rank 18 of 20) is in the long
        // bucket, while the average of the two minutes' P90s would say 12.5.
        var report = DailyReports.Build(Input([Minute("DMO/A-CIT", Local(9), 17, 5), Minute("DMO/A-CIT", Local(9, 1), 3, 20)]));

        var nine = report.Lanes.Single(l => l.Zone == "A-CIT").Hours.Single(h => h.Start == "09:00");
        nine.Should().Match<LaneHour>(h => h.Waits == 20 && h.Passengers == 20 && h.End == "10:00");
        nine.P50Minutes.Should().Be(5.5, "the upper edge of the 5-minute bucket");
        nine.P90Minutes.Should().Be(20.5);
        report.Lanes.Single(l => l.Zone == "A-CIT").P90Minutes.Should().Be(20.5, "the day merges the same histograms");
    }

    [Fact]
    public void Day_Should_FollowTheLocalCalendar_When_TheSiteIsNotInUtc()
    {
        var report = DailyReports.Build(Input([
            Minute("DMO/A-CIT", Local(0), 25, 3), // 00:00 local: the first hour of the day
            Minute("DMO/A-CIT", Local(0).AddMinutes(-1), 25, 30), // 23:59 the day before: not this report
            Minute("DMO/A-CIT", Local(23, 59), 25, 4)
        ]));

        report.FromUtc.Should().Be(new DateTime(2026, 10, 2, 20, 0, 0, DateTimeKind.Utc));
        report.ToUtc.Should().Be(new DateTime(2026, 10, 3, 20, 0, 0, DateTimeKind.Utc));
        var lane = report.Lanes.Single(l => l.Zone == "A-CIT");
        lane.Hours.Should().HaveCount(24);
        lane.Waits.Should().Be(50);
        lane.Hours[0].Start.Should().Be("00:00");
        lane.Hours[^1].Should().Match<LaneHour>(h => h.Start == "23:00" && h.End == "00:00" && h.Waits == 25);
    }

    [Fact]
    public void Day_Should_Have23Or25Hours_When_TheClockChanges()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var (springFrom, springTo) = DailyReports.DayBounds(new DateOnly(2026, 3, 29), berlin);
        var (autumnFrom, autumnTo) = DailyReports.DayBounds(new DateOnly(2026, 10, 25), berlin);

        (springTo - springFrom).Should().Be(TimeSpan.FromHours(23));
        (autumnTo - autumnFrom).Should().Be(TimeSpan.FromHours(25));
    }

    [Fact]
    public void Peak_Should_BeTheWorstHourWithEnoughWaits_When_AQuietHourHasOneLongWait()
    {
        var report = DailyReports.Build(Input([
            Minute("DMO/A-CIT", Local(6), 1, 40), // one passenger waiting 40 minutes: too few waits to be the peak
            Minute("DMO/A-CIT", Local(8), 30, 12),
            Minute("DMO/A-CIT", Local(17), 30, 18, length: 140),
            Minute("DMO/A-VIS", Local(17), 30, 9)
        ]));

        var cit = report.Lanes.Single(l => l.Zone == "A-CIT");
        cit.Peak.Should().Match<LaneHour>(h => h.Start == "17:00" && h.P90Minutes == 18.5);
        cit.MaxQueueLength.Should().Be(140);
        report.Headline.Should().Match<DailyHeadline>(h =>
            h.WorstPeakP90Minutes == 18.5 && h.WorstPeakLane == "A-CIT" && h.WorstPeakHour == "17:00" && h.ZoneHoursAboveTarget == 1);
        report.Lanes.Select(l => l.Zone).Should().Equal("A-CIT", "A-VIS");
    }

    [Fact]
    public void Hour_Should_SayProvisionalOrMissing_When_TheMinutesAreNotFinalOrHaveNoHistogram()
    {
        var old = new ReportMinute("DMO/A-CIT", Local(10), true, 5, 5, null, [], []);
        var report = DailyReports.Build(Input([old, Minute("DMO/A-CIT", Local(11), 5, 3, final: false)]));

        var hours = report.Lanes.Single(l => l.Zone == "A-CIT").Hours;
        hours.Single(h => h.Start == "10:00").Should().Match<LaneHour>(h => h.HistogramMissing && h.P90Minutes == null && h.Waits == 5);
        hours.Single(h => h.Start == "11:00").Provisional.Should().BeTrue();
        report.Lanes.Single(l => l.Zone == "A-CIT").Provisional.Should().BeTrue();
    }

    [Fact]
    public void Alerts_Should_CountTheDayBySeverityInLocalTime()
    {
        var report = DailyReports.Build(Input([], alerts: [
            new ReportAlert("R-001", "Long wait", "A-CIT", null, "Critical", "Resolved", Local(17, 5), Local(17, 47)),
            new ReportAlert("R-002", "Device offline", "A-CIT", "S-1", "Warning", "Raised", Local(18), null),
            new ReportAlert("R-001", "Long wait", "A-CIT", null, "Critical", "Resolved", Local(0).AddMinutes(-5), Local(0).AddMinutes(10))
        ]));

        report.AlertsBySeverity.Should().BeEquivalentTo(new Dictionary<string, int> { ["Critical"] = 1, ["Warning"] = 1 });
        report.Alerts.Should().HaveCount(2);
        report.Alerts[0].Should().Match<AlertLine>(a => a.RaisedLocal == "2026-10-03 17:05" && a.ClearedLocal == "2026-10-03 17:47" && a.OpenMinutes == 42);
        report.Alerts[1].Should().Match<AlertLine>(a => a.ClearedLocal == null && a.OpenMinutes == null);
        report.Headline.Should().Match<DailyHeadline>(h => h.Alerts == 2 && h.CriticalAlerts == 1);
    }

    [Fact]
    public void Uptime_Should_CountOverlappingOutagesOnce_And_ClipThemToTheDay()
    {
        var report = DailyReports.Build(Input([], devices: [
            new ReportDevice("S-1", "A-CIT", [(Local(10), Local(11)), (Local(10, 30), Local(12))]), // 120 minutes once
            new ReportDevice("S-2", "A-VIS", [(Local(0).AddHours(-2), Local(1))]), // only the first local hour counts
            new ReportDevice("S-3", "A-VIS", [(Local(22), null)]), // still open at the end of the day: two hours
            new ReportDevice("S-4", "A-VIS", [])
        ]));

        report.Devices.Should().BeEquivalentTo(new[]
        {
            new DeviceUptime("S-1", "A-CIT", 91.67, 120, 2),
            new DeviceUptime("S-2", "A-VIS", 95.83, 60, 1),
            new DeviceUptime("S-3", "A-VIS", 91.67, 120, 1),
            new DeviceUptime("S-4", "A-VIS", 100, 0, 0)
        }, o => o.WithStrictOrdering());
        report.Headline.LowestUptimePercent.Should().Be(91.67);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://x\")", "\"'=HYPERLINK(\"\"http://x\"\")\"")]
    [InlineData("+1+1", "'+1+1")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\tcmd", "\"'\tcmd\"")]
    [InlineData("x;=1+1", "\"x;=1+1\"")]
    [InlineData("Hall;east", "\"Hall;east\"")]
    [InlineData("  =1+1", "'  =1+1")]
    [InlineData("A-CIT", "A-CIT")]
    [InlineData("Hall, east", "\"Hall, east\"")]
    [InlineData("القادمون", "القادمون")]
    [InlineData(null, "")]
    public void CsvText_Should_NeutraliseFormulasAndQuote(string value, string expected) => ReportCsv.Text(value).Should().Be(expected);

    [Fact]
    public void Csv_Should_WriteEveryHourWithNamesAsText_When_ANameLooksLikeAFormula()
    {
        var input = Input([Minute("DMO/A-CIT", Local(9), 20, 5)]) with
        {
            Zones = [new ReportZone("DMO/A-CIT", "=cmd|' /C calc'!A0", "CIT")],
            Alerts = [new ReportAlert("R-001", "@evil", "A-CIT", null, "Critical", "Raised", Local(9), null)],
            Devices = [new ReportDevice("S-1", "+A-CIT", [])]
        };
        var report = DailyReports.Build(input);

        var hours = ReportCsv.Write(report, ReportCsv.Section.Hours).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        hours[0].Should().Be("site,date,hour_start_local,hour_end_local,zone,lane,passengers,waits,p50_wait_min,p90_wait_min,max_queue_length,status,histogram");
        hours.Should().HaveCount(25);
        hours[10].Should().Be("DMO,2026-10-03,09:00,10:00,'=cmd|' /C calc'!A0,CIT,20,20,5.5,5.5,,Final,Complete");
        ReportCsv.Write(report, ReportCsv.Section.Alerts).Should().Contain(",'@evil,");
        ReportCsv.Write(report, ReportCsv.Section.Devices).Should().Contain("DMO,2026-10-03,S-1,'+A-CIT,100,0,0");
    }
}
