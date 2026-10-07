using Ariva.Core.Availability;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Services.Quality;
using FluentAssertions;
using Fluentx;

namespace Ariva.UnitTests.Domain.Availability;

/// <summary>
/// ARV-118: the calendar requests (Fx.Specification) and entities. Entries are recorded before they take effect: weekly
/// versions and exceptions for a local day after today, maintenance windows starting after now (server time); a window
/// recorded at or after its start is refused outright. Messages are fixed texts, never the request's values (CWE-501).
/// </summary>
public sealed class SiteCalendarRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 7);
    private static readonly DateTime Now = new(2026, 10, 7, 8, 30, 15, DateTimeKind.Utc);

    private static async Task<IReadOnlyList<string>> Errors<T>(ISpecification<T> rules, T request) =>
        [.. (await rules.ValidateAllAsync(request)).ErrorMessages ?? []];

    #region Weekly versions

    private static SetOperatingWeekRequest Week(string effectiveFrom, params (string Day, string Opens, string Closes)[] hours) =>
        new(effectiveFrom, [.. hours.Select(h => new OperatingDayHoursRequest(h.Day, h.Opens, h.Closes))]);

    [Fact]
    public async Task Week_Should_Pass_When_TheVersionStartsTomorrow()
    {
        (await Errors(SiteCalendarRules.Week(Today), Week("2026-10-08", ("Monday", "06:00", "22:00"), ("Friday", "18:00", "02:00")))).Should().BeEmpty();
        (await Errors(SiteCalendarRules.Week(Today), Week("2027-10-08"))).Should().BeEmpty("366 days ahead; an empty week is closed every day");
    }

    [Theory]
    [InlineData("2026-10-07")]
    [InlineData("2026-10-06")]
    [InlineData("2027-10-09")]
    [InlineData("2026-10-8")]
    [InlineData("08/10/2026")]
    [InlineData("2026-02-30")]
    [InlineData("2026-10-08' OR '1'='1")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Week_Should_RefuseTheDate_When_ItIsNotAFutureLocalDate(string effectiveFrom)
    {
        var errors = await Errors(SiteCalendarRules.Week(Today), Week(effectiveFrom, ("Monday", "06:00", "22:00")));

        errors.Should().Equal(SiteCalendarErrors.InvalidDate);
    }

    [Fact]
    public async Task Week_Should_GiveTheHoursProblem_When_TheHoursAreInvalid()
    {
        (await Errors(SiteCalendarRules.Week(Today), Week("2026-10-08", ("Mon", "06:00", "22:00")))).Should().Equal(WeeklyHours.InvalidDay);
        (await Errors(SiteCalendarRules.Week(Today), Week("2026-10-08", ("Monday", "06:00", "06:00")))).Should().Equal(WeeklyHours.InvalidInterval);
        (await Errors(SiteCalendarRules.Week(Today), Week("2026-10-08", ("Monday", "06:00", "12:00"), ("Monday", "11:00", "13:00")))).Should().Equal(WeeklyHours.Overlapping);
        (await Errors(SiteCalendarRules.Week(Today), new SetOperatingWeekRequest("2026-10-08", null))).Should().Equal(WeeklyHours.InvalidInterval);
        (await Errors(SiteCalendarRules.Week(Today), new SetOperatingWeekRequest("2026-10-08", [null]))).Should().Equal(WeeklyHours.InvalidDay);
    }

    #endregion

    #region Exceptions

    [Fact]
    public async Task Exception_Should_Pass_When_ClosedOrOpenAsDeclared()
    {
        (await Errors(SiteCalendarRules.Exception(Today), new AddOperatingExceptionRequest("2026-12-02", true, [], "National Day"))).Should().BeEmpty();
        (await Errors(SiteCalendarRules.Exception(Today), new AddOperatingExceptionRequest("2026-12-02", true, null, "National Day"))).Should().BeEmpty();
        (await Errors(SiteCalendarRules.Exception(Today), new AddOperatingExceptionRequest("2026-12-02", false, [new("10:00", "14:00")], "Short day"))).Should().BeEmpty();
    }

    [Fact]
    public async Task Exception_Should_Refuse_When_ClosedAndHoursDisagree()
    {
        (await Errors(SiteCalendarRules.Exception(Today), new AddOperatingExceptionRequest("2026-12-02", true, [new("10:00", "14:00")], "x"))).Should().Equal(SiteCalendarErrors.InvalidClosed);
        (await Errors(SiteCalendarRules.Exception(Today), new AddOperatingExceptionRequest("2026-12-02", false, [], "x"))).Should().Equal(SiteCalendarErrors.InvalidClosed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("line\nbreak")]
    [InlineData("bidi \u202E override")]
    [InlineData("zero\u200Bwidth")]
    public async Task Exception_Should_RefuseTheReason_When_ItIsBlankOrHidesCharacters(string reason)
    {
        (await Errors(SiteCalendarRules.Exception(Today), new AddOperatingExceptionRequest("2026-12-02", true, [], reason))).Should().Equal(SiteCalendarErrors.InvalidReason);
    }

    [Fact]
    public async Task Exception_Should_AcceptMarkupAsText_When_TheReasonIsCleanText()
    {
        // Stored and returned as JSON text, never rendered as markup; there is no screen in this story.
        (await Errors(SiteCalendarRules.Exception(Today), new AddOperatingExceptionRequest("2026-12-02", true, [], "<script>alert(1)</script>"))).Should().BeEmpty();
        (await Errors(SiteCalendarRules.Exception(Today), new AddOperatingExceptionRequest("2026-12-02", true, [], new string('x', 201)))).Should().Equal(SiteCalendarErrors.InvalidReason);
    }

    #endregion

    #region Maintenance windows

    [Fact]
    public void Window_Should_AcceptWholeMinutesAfterNow_When_AtMostSevenDays()
    {
        SiteCalendarRules.Window(new AddMaintenanceWindowRequest("2026-10-07T08:31:00Z", "2026-10-14T08:31:00Z", "x"), Now)
            .Should().Be((new DateTime(2026, 10, 7, 8, 31, 0, DateTimeKind.Utc), new DateTime(2026, 10, 14, 8, 31, 0, DateTimeKind.Utc)));
    }

    [Theory]
    [InlineData("2026-10-07T08:30:00Z", "2026-10-07T09:00:00Z", "started 15 seconds ago: recorded after its start")]
    [InlineData("2026-10-07T07:00:00Z", "2026-10-07T09:00:00Z", "in the past")]
    [InlineData("2026-10-07T09:00:30Z", "2026-10-07T10:00:00Z", "not a whole minute")]
    [InlineData("2026-10-07T09:00:00Z", "2026-10-07T09:00:00Z", "empty")]
    [InlineData("2026-10-07T10:00:00Z", "2026-10-07T09:00:00Z", "ends before it starts")]
    [InlineData("2026-10-08T00:00:00Z", "2026-10-15T00:01:00Z", "longer than 7 days")]
    [InlineData("2027-10-09T00:00:00Z", "2027-10-09T01:00:00Z", "more than 366 days ahead")]
    [InlineData("2026-10-08T09:00:00+04:00", "2026-10-08T10:00:00Z", "an offset, not UTC")]
    [InlineData("2026-10-08T09:00:00", "2026-10-08T10:00:00Z", "a local time")]
    [InlineData("tomorrow", "2026-10-08T10:00:00Z", "not a time")]
    [InlineData(null, "2026-10-08T10:00:00Z", "missing")]
    public void Window_Should_Refuse_When_TheWindowIsNotAFutureWholeMinuteSpan(string starts, string ends, string because)
    {
        SiteCalendarRules.Window(new AddMaintenanceWindowRequest(starts, ends, "x"), Now).Should().BeNull(because);
    }

    [Fact]
    public async Task Maintenance_Should_GiveFixedMessages_When_TheRequestIsHostile()
    {
        var errors = await Errors(SiteCalendarRules.Maintenance(Now), new AddMaintenanceWindowRequest("2026-10-08' OR '1'='1", "<script>", "\u0000"));

        errors.Should().Equal(SiteCalendarErrors.InvalidWindow, SiteCalendarErrors.InvalidReason);
    }

    #endregion

    #region Entities

    [Fact]
    public void MaintenanceWindow_Should_Refuse_When_RecordedAtOrAfterItsStart()
    {
        var starts = new DateTime(2026, 10, 8, 1, 0, 0, DateTimeKind.Utc);

        FluentActions.Invoking(() => new MaintenanceWindow("DMO", starts, starts.AddHours(1), "Upgrade", starts)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new MaintenanceWindow("DMO", starts, starts.AddDays(8), "Upgrade", Now)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new MaintenanceWindow("DMO", starts.AddSeconds(1), starts.AddHours(1), "Upgrade", Now)).Should().Throw<ArgumentException>();
        new MaintenanceWindow("DMO", starts, starts.AddHours(1), " Upgrade ", Now).Reason.Should().Be("Upgrade");
    }

    [Fact]
    public void OperatingWeek_Should_KeepCanonicalHours_When_Created()
    {
        var (hours, _) = WeeklyHours.Parse([new("Tuesday", "06:00", "22:00")]);
        var week = new OperatingWeek("DMO", new DateOnly(2026, 10, 8), hours, Now);

        week.EffectiveFrom.Should().Be("2026-10-08");
        week.EffectiveDate.Should().Be(new DateOnly(2026, 10, 8));
        week.Week.Should().NotBeNull();
        week.Week.On(DayOfWeek.Tuesday).Should().ContainSingle();
        week.AuditSummary().Should().Contain("effectiveFrom=2026-10-08");
        FluentActions.Invoking(() => new OperatingWeek("dmo", new DateOnly(2026, 10, 8), hours, Now)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void OperatingDayException_Should_StoreAClosedDayAsEmptyHours_When_Closed()
    {
        var exception = new OperatingDayException("DMO", new DateOnly(2026, 12, 2), [], "National Day", Now);

        exception.Hours.Should().Be("[]");
        exception.Intervals.Should().BeEmpty();
        exception.LocalDate.Should().Be(new DateOnly(2026, 12, 2));
        exception.AuditSummary().Should().Contain("reason=\"National Day\"");
    }

    #endregion
}
