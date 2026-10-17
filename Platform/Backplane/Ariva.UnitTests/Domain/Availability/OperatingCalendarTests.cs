using Ariva.Core.Availability;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Availability;

/// <summary>
/// ARV-118: the site operating calendar (formulas F18, Proposed). Weekly hours in the site's local time with overnight
/// intervals, dated exceptions and announced maintenance windows, evaluated per UTC minute; entries count only when
/// recorded before they take effect; local days at a site without daylight saving (Dubai), with it (London) and with a
/// skipped midnight (Beirut).
/// </summary>
public sealed class OperatingCalendarTests
{
    #region Helpers

    private static readonly TimeZoneInfo Dubai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Dubai");
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly TimeZoneInfo Beirut = TimeZoneInfo.FindSystemTimeZoneById("Asia/Beirut");

    private static readonly DateTime LongAgo = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime Utc(int month, int day, int hour, int minute) => new(2026, month, day, hour, minute, 0, DateTimeKind.Utc);

    private static WeeklyHours Week(params (string Day, string Opens, string Closes)[] intervals)
    {
        var (hours, problem) = WeeklyHours.Parse(intervals.Select(i => new OperatingDayHours(i.Day, i.Opens, i.Closes)));
        problem.Should().BeNull();
        return hours;
    }

    private static WeeklyHours EveryDay(string opens, string closes) =>
        Week([.. WeeklyHours.Days.Select(d => (d.ToString(), opens, closes))]);

    private static IReadOnlyList<OperatingInterval> Day(params (string Opens, string Closes)[] intervals)
    {
        var (hours, problem) = WeeklyHours.ParseDay(intervals.Select(i => new OperatingHoursText(i.Opens, i.Closes)));
        problem.Should().BeNull();
        return hours;
    }

    private static OperatingCalendar Calendar(TimeZoneInfo zone, IEnumerable<CalendarWeek> weeks, IEnumerable<CalendarException> exceptions = null,
        IEnumerable<CalendarMaintenance> windows = null) => new(zone, weeks, exceptions ?? [], windows ?? []);

    private static CalendarWeek From(DateOnly date, WeeklyHours hours, DateTime? recorded = null, DateTime? cancelled = null) =>
        new(date, recorded ?? LongAgo, cancelled, hours);

    /// <summary>The operating minutes of a local day: the UTC minutes from its start to the next day's start that the calendar counts.</summary>
    private static int OperatingMinutes(OperatingCalendar calendar, DateOnly date)
    {
        var from = OperatingCalendar.DayStartUtc(date, calendar.Zone);
        var to = OperatingCalendar.DayStartUtc(date.AddDays(1), calendar.Zone);
        var count = 0;
        for (var m = from; m < to; m = m.AddMinutes(1))
        {
            var minute = calendar.Classify(m);
            minute.LocalDate.Should().Be(date);
            if (minute.State == CalendarState.Operating)
                count++;
        }

        return count;
    }

    #endregion

    #region Weekly hours in local time

    [Fact]
    public void Classify_Should_CountEveryMinute_When_TheSiteHasNoCalendar()
    {
        var calendar = Calendar(Dubai, []);

        calendar.Classify(Utc(10, 5, 0, 0)).State.Should().Be(CalendarState.Operating);
        OperatingMinutes(calendar, new DateOnly(2026, 10, 5)).Should().Be(1_440);
    }

    [Theory]
    [InlineData(1, 59, CalendarState.Closed)]      // 05:59 in Dubai
    [InlineData(2, 0, CalendarState.Operating)]    // 06:00, the opening minute
    [InlineData(17, 59, CalendarState.Operating)]  // 21:59, the last minute
    [InlineData(18, 0, CalendarState.Closed)]      // 22:00, the closing time is not inside
    public void Classify_Should_UseTheSitesLocalTime_When_TheHoursAreSixToTwentyTwo(int hour, int minute, CalendarState expected)
    {
        var calendar = Calendar(Dubai, [From(new DateOnly(2026, 10, 1), EveryDay("06:00", "22:00"))]);

        calendar.Classify(Utc(10, 5, hour, minute)).State.Should().Be(expected);
    }

    [Fact]
    public void Classify_Should_GiveTheLocalDate_When_TheUtcDateDiffers()
    {
        var calendar = Calendar(Dubai, []);

        calendar.Classify(Utc(10, 5, 20, 30)).LocalDate.Should().Be(new DateOnly(2026, 10, 6), "20:30 UTC is 00:30 the next day in Dubai");
        calendar.Classify(Utc(10, 5, 19, 59)).LocalDate.Should().Be(new DateOnly(2026, 10, 5));
    }

    [Fact]
    public void OperatingMinutes_Should_BeSixteenHours_When_ADubaiDayIsSixToTwentyTwo()
    {
        var calendar = Calendar(Dubai, [From(new DateOnly(2026, 10, 1), EveryDay("06:00", "22:00"))]);

        OperatingMinutes(calendar, new DateOnly(2026, 10, 5)).Should().Be(16 * 60);
    }

    [Fact]
    public void Classify_Should_RunIntoTheNextDay_When_AnIntervalIsOvernight()
    {
        // Friday 18:00 to 02:00 (Saturday); Saturday has no hours of its own.
        var calendar = Calendar(Dubai, [From(new DateOnly(2026, 10, 1), Week(("Friday", "18:00", "02:00")))]);
        var friday = new DateOnly(2026, 10, 9);

        calendar.Classify(Utc(10, 9, 13, 59)).State.Should().Be(CalendarState.Closed, "17:59 Friday");
        calendar.Classify(Utc(10, 9, 14, 0)).State.Should().Be(CalendarState.Operating, "18:00 Friday");
        calendar.Classify(Utc(10, 9, 21, 59)).State.Should().Be(CalendarState.Operating, "01:59 Saturday");
        calendar.Classify(Utc(10, 9, 22, 0)).State.Should().Be(CalendarState.Closed, "02:00 Saturday");
        OperatingMinutes(calendar, friday).Should().Be(6 * 60, "a day's own minutes: 18:00 to midnight");
        OperatingMinutes(calendar, friday.AddDays(1)).Should().Be(2 * 60, "the overnight part counts on the day it falls on");
    }

    [Fact]
    public void Classify_Should_WrapIntoMonday_When_SundayIsOvernight()
    {
        var calendar = Calendar(Dubai, [From(new DateOnly(2026, 10, 1), Week(("Sunday", "20:00", "03:00")))]);

        calendar.Classify(Utc(10, 11, 22, 30)).State.Should().Be(CalendarState.Operating, "02:30 on Monday 12 October, from Sunday's interval");
        calendar.Classify(Utc(10, 11, 23, 0)).State.Should().Be(CalendarState.Closed, "03:00 Monday");
    }

    [Fact]
    public void Classify_Should_CountTheWholeDay_When_TheHoursAreMidnightToTwentyFour()
    {
        var calendar = Calendar(Dubai, [From(new DateOnly(2026, 10, 1), Week(("Monday", "00:00", "24:00")))]);

        OperatingMinutes(calendar, new DateOnly(2026, 10, 5)).Should().Be(1_440);
        OperatingMinutes(calendar, new DateOnly(2026, 10, 6)).Should().Be(0, "Tuesday has no hours: closed");
    }

    [Fact]
    public void WeekOn_Should_TakeTheLatestVersionInForce_When_SeveralAreRecorded()
    {
        var first = EveryDay("06:00", "22:00");
        var second = EveryDay("08:00", "20:00");
        var calendar = Calendar(Dubai, [From(new DateOnly(2026, 10, 1), first), From(new DateOnly(2026, 10, 10), second)]);

        calendar.WeekOn(new DateOnly(2026, 9, 30)).Should().BeSameAs(WeeklyHours.AlwaysOpen, "before the first version: open around the clock");
        calendar.WeekOn(new DateOnly(2026, 10, 9)).Should().BeSameAs(first);
        calendar.WeekOn(new DateOnly(2026, 10, 10)).Should().BeSameAs(second);
        calendar.WeekOn(new DateOnly(2027, 1, 1)).Should().BeSameAs(second);
    }

    [Fact]
    public void WeekOn_Should_IgnoreAVersion_When_ItWasRecordedAfterItsDayStarted()
    {
        // Dubai's 5 October starts at 20:00 UTC on the 4th.
        var hours = EveryDay("06:00", "22:00");
        var day = new DateOnly(2026, 10, 5);

        Calendar(Dubai, [From(day, hours, Utc(10, 4, 19, 59))]).WeekOn(day).Should().BeSameAs(hours, "recorded a minute before the day");
        Calendar(Dubai, [From(day, hours, Utc(10, 4, 20, 0))]).WeekOn(day).Should().BeSameAs(WeeklyHours.AlwaysOpen, "recorded as the day starts");
        Calendar(Dubai, [From(day, hours, Utc(10, 5, 9, 0))]).WeekOn(day.AddDays(3)).Should().BeSameAs(WeeklyHours.AlwaysOpen,
            "a late version never counts, on later days either");
    }

    [Fact]
    public void WeekOn_Should_IgnoreAVersion_When_ItWasRemovedBeforeItsDayStarted()
    {
        var hours = EveryDay("06:00", "22:00");
        var day = new DateOnly(2026, 10, 5);

        Calendar(Dubai, [From(day, hours, cancelled: Utc(10, 4, 19, 0))]).WeekOn(day).Should().BeSameAs(WeeklyHours.AlwaysOpen);
        Calendar(Dubai, [From(day, hours, cancelled: Utc(10, 6, 9, 0))]).WeekOn(day).Should().BeSameAs(hours,
            "removed after it took effect (only an operator could): the record stands");
    }

    #endregion

    #region Dated exceptions

    [Fact]
    public void Classify_Should_CloseTheDay_When_AnExceptionClosesIt()
    {
        var day = new DateOnly(2026, 10, 7);
        var calendar = Calendar(Dubai, [From(new DateOnly(2026, 10, 1), EveryDay("06:00", "22:00"))],
            [new CalendarException(day, Utc(10, 2, 0, 0), null, [])]);

        OperatingMinutes(calendar, day).Should().Be(0);
        OperatingMinutes(calendar, day.AddDays(1)).Should().Be(16 * 60, "the next day has its weekly hours");
    }

    [Fact]
    public void Classify_Should_UseTheExceptionsHours_When_ItReplacesTheDay()
    {
        var day = new DateOnly(2026, 10, 7);
        var calendar = Calendar(Dubai, [From(new DateOnly(2026, 10, 1), EveryDay("06:00", "22:00"))],
            [new CalendarException(day, Utc(10, 2, 0, 0), null, Day(("10:00", "12:00"), ("14:00", "15:30")))]);

        OperatingMinutes(calendar, day).Should().Be(210);
        calendar.Classify(Utc(10, 7, 6, 0)).State.Should().Be(CalendarState.Operating, "10:00 local");
        calendar.Classify(Utc(10, 7, 8, 0)).State.Should().Be(CalendarState.Closed, "12:00 local");
    }

    [Fact]
    public void Classify_Should_KeepThePreviousDaysOvernightPart_When_AnExceptionClosesTheNextDay()
    {
        var friday = new DateOnly(2026, 10, 9);
        var calendar = Calendar(Dubai, [From(new DateOnly(2026, 10, 1), Week(("Friday", "18:00", "02:00"), ("Saturday", "18:00", "02:00")))],
            [new CalendarException(friday.AddDays(1), Utc(10, 2, 0, 0), null, [])]);

        OperatingMinutes(calendar, friday.AddDays(1)).Should().Be(120, "Saturday's own interval is gone; Friday's runs until 02:00 Saturday");
        OperatingMinutes(calendar, friday.AddDays(2)).Should().Be(0, "nothing opened on Saturday, so nothing runs into Sunday");
    }

    [Fact]
    public void HoursOpeningOn_Should_IgnoreAnException_When_ItWasRecordedLateOrRemovedInTime()
    {
        var day = new DateOnly(2026, 10, 7);
        var weekly = EveryDay("06:00", "22:00");
        var start = OperatingCalendar.DayStartUtc(day, Dubai);

        Calendar(Dubai, [From(new DateOnly(2026, 10, 1), weekly)], [new CalendarException(day, start, null, [])])
            .HoursOpeningOn(day).Should().HaveCount(1, "recorded as the day starts: the weekly hours stand");
        Calendar(Dubai, [From(new DateOnly(2026, 10, 1), weekly)], [new CalendarException(day, start.AddMinutes(-1), start.AddMinutes(-1), [])])
            .HoursOpeningOn(day).Should().HaveCount(1, "removed before the day started");
        Calendar(Dubai, [From(new DateOnly(2026, 10, 1), weekly)], [new CalendarException(day, start.AddMinutes(-1), null, [])])
            .HoursOpeningOn(day).Should().BeEmpty("recorded a minute before the day");
    }

    #endregion

    #region Maintenance windows

    private static OperatingCalendar WithWindow(DateTime recorded, DateTime? cancelled = null) =>
        Calendar(Dubai, [From(new DateOnly(2026, 10, 1), EveryDay("06:00", "22:00"))], [],
            [new CalendarMaintenance(Utc(10, 6, 4, 0), Utc(10, 6, 5, 0), recorded, cancelled)]);

    [Fact]
    public void Classify_Should_MarkMaintenance_When_TheWindowWasRecordedBeforeItStarted()
    {
        var calendar = WithWindow(Utc(10, 6, 3, 59));

        calendar.Classify(Utc(10, 6, 3, 59)).State.Should().Be(CalendarState.Operating);
        calendar.Classify(Utc(10, 6, 4, 0)).State.Should().Be(CalendarState.Maintenance);
        calendar.Classify(Utc(10, 6, 4, 59)).State.Should().Be(CalendarState.Maintenance);
        calendar.Classify(Utc(10, 6, 5, 0)).State.Should().Be(CalendarState.Operating, "the end is not inside");
        OperatingMinutes(calendar, new DateOnly(2026, 10, 6)).Should().Be((16 * 60) - 60);
    }

    [Theory]
    [InlineData(0, "recorded as it starts")]
    [InlineData(1, "recorded a minute after its start")]
    [InlineData(30, "recorded in the middle")]
    public void Classify_Should_NotCountTheWindowAtAll_When_ItWasRecordedAtOrAfterItsStart(int minutesAfterStart, string because)
    {
        var calendar = WithWindow(Utc(10, 6, 4, 0).AddMinutes(minutesAfterStart));

        calendar.Classify(Utc(10, 6, 4, 0)).State.Should().Be(CalendarState.Operating, because);
        calendar.Classify(Utc(10, 6, 4, 45)).State.Should().Be(CalendarState.Operating, because);
    }

    [Fact]
    public void Classify_Should_IgnoreTheWindow_When_ItWasCancelledBeforeItStarted()
    {
        WithWindow(Utc(10, 1, 0, 0), Utc(10, 6, 3, 0)).Classify(Utc(10, 6, 4, 30)).State.Should().Be(CalendarState.Operating);
        WithWindow(Utc(10, 1, 0, 0), Utc(10, 6, 4, 10)).Classify(Utc(10, 6, 4, 30)).State.Should().Be(CalendarState.Maintenance,
            "cancelled after it started (only an operator could): the record stands");
    }

    [Fact]
    public void Classify_Should_StayClosed_When_AWindowCoversClosedHours()
    {
        var calendar = Calendar(Dubai, [From(new DateOnly(2026, 10, 1), EveryDay("06:00", "22:00"))], [],
            [new CalendarMaintenance(Utc(10, 6, 0, 0), Utc(10, 6, 3, 0), LongAgo, null)]);

        calendar.Classify(Utc(10, 6, 1, 0)).State.Should().Be(CalendarState.Closed, "05:00 local");
        calendar.Classify(Utc(10, 6, 2, 30)).State.Should().Be(CalendarState.Maintenance, "06:30 local");
    }

    #endregion

    #region Daylight saving

    [Fact]
    public void OperatingMinutes_Should_LoseTheSkippedHour_When_LondonSpringsForward()
    {
        // 29 March 2026: 01:00 GMT becomes 02:00 BST at 01:00 UTC.
        var calendar = Calendar(London, [From(new DateOnly(2026, 1, 1), EveryDay("00:30", "03:00"))]);
        var day = new DateOnly(2026, 3, 29);

        OperatingMinutes(calendar, day).Should().Be(90, "00:30 to 01:00 GMT and 02:00 to 03:00 BST; 01:00 to 02:00 does not exist");
        OperatingMinutes(Calendar(London, []), day).Should().Be(23 * 60);
        OperatingMinutes(calendar, day.AddDays(1)).Should().Be(150);
    }

    [Fact]
    public void OperatingMinutes_Should_CountTheRepeatedHourTwice_When_LondonFallsBack()
    {
        // 25 October 2026: 02:00 BST becomes 01:00 GMT at 01:00 UTC.
        var calendar = Calendar(London, [From(new DateOnly(2026, 1, 1), EveryDay("00:30", "03:00"))]);
        var day = new DateOnly(2026, 10, 25);

        OperatingMinutes(calendar, day).Should().Be(210, "01:00 to 02:00 happens twice");
        OperatingMinutes(Calendar(London, []), day).Should().Be(25 * 60);
        OperatingCalendar.DayStartUtc(day, London).Should().Be(Utc(10, 24, 23, 0), "midnight BST");
        OperatingCalendar.DayStartUtc(day.AddDays(1), London).Should().Be(Utc(10, 26, 0, 0), "midnight GMT");
    }

    [Fact]
    public void OperatingMinutes_Should_BeUnchanged_When_TheHoursAvoidTheClockChange()
    {
        var calendar = Calendar(London, [From(new DateOnly(2026, 1, 1), EveryDay("06:00", "22:00"))]);

        OperatingMinutes(calendar, new DateOnly(2026, 3, 29)).Should().Be(16 * 60);
        OperatingMinutes(calendar, new DateOnly(2026, 10, 25)).Should().Be(16 * 60);
    }

    [Fact]
    public void DayStartUtc_Should_BeTheFirstValidMinute_When_TheClockSkipsMidnight()
    {
        // Beirut moves from 00:00 to 01:00 on the last Sunday of March: 29 March 2026 starts at 01:00 EEST, 22:00 UTC the day before.
        OperatingCalendar.DayStartUtc(new DateOnly(2026, 3, 29), Beirut).Should().Be(Utc(3, 28, 22, 0));
        OperatingMinutes(Calendar(Beirut, []), new DateOnly(2026, 3, 29)).Should().Be(23 * 60);
    }

    [Fact]
    public void Classify_Should_CountAnExceptionFromTheSkippedMidnight_When_RecordedBeforeTheFirstValidMinute()
    {
        var day = new DateOnly(2026, 3, 29);
        var start = OperatingCalendar.DayStartUtc(day, Beirut);
        var calendar = Calendar(Beirut, [], [new CalendarException(day, start.AddMinutes(-1), null, [])]);

        OperatingMinutes(calendar, day).Should().Be(0);
    }

    #endregion
}
