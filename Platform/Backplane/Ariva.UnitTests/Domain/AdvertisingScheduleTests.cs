using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using FluentAssertions;

namespace Ariva.UnitTests.Domain;

/// <summary>
/// ARV-143a: a schedule assigns a playlist to a site's displays (the slides' Site, Zone and Display scopes) on chosen days
/// within a window of the site's local time, possibly past midnight. Which applies to a display: the most specific target,
/// then the window opened last, then the newest; none on an empty schedule, and never a retired schedule or one whose
/// playlist is retired. Windows keep their local hours across daylight saving (Europe/London 2026: the clocks go forward on
/// 29 March, skipping 01:00 to 02:00, and back on 25 October, repeating it).
/// </summary>
public sealed class AdvertisingScheduleTests
{
    private const string Site = "AUH-TA";
    private static readonly Guid Playlist = Guid.Parse("0199a000-0000-7000-8000-0000000000a1");
    private static readonly Guid Other = Guid.Parse("0199a000-0000-7000-8000-0000000000a2");
    private static readonly IReadOnlySet<Guid> Active = new HashSet<Guid> { Playlist, Other };
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly string[] Zones = ["A-CIT", "A-VIS"];

    private static AdvertisingScheduleValues Values(InstructionScope scope = InstructionScope.Site, string[] targets = null, string[] days = null,
        string opens = "09:00", string closes = "17:00", Guid? playlist = null) =>
        new(playlist ?? Playlist, scope, targets ?? [], days ?? ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"], opens, closes);

    private static int _sequence;

    private static AdvertisingSchedule Schedule(AdvertisingScheduleValues values, string site = Site, DateTime? created = null)
    {
        var schedule = new AdvertisingSchedule(site, values)
        {
            Id = Guid.CreateVersion7(),
            CreatedOn = created ?? new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc).AddMinutes(Interlocked.Increment(ref _sequence))
        };
        return schedule;
    }

    private static AdvertisingSchedule Resolve(DateTime local, string display = "AD-1", params AdvertisingSchedule[] schedules) =>
        AdvertisingSchedules.Resolve(schedules, Active, Site, display, Zones, local);

    [Fact]
    public void Values_Should_BeTaken_When_TheyNameAPlaylistATargetDaysAndAWindow()
    {
        Values().Problems().Should().BeEmpty();
        Values(InstructionScope.Zone, ["A-CIT"]).Problems().Should().BeEmpty();
        Values(InstructionScope.Display, ["AD-1", "AD-2"]).Problems().Should().BeEmpty();
        Values(days: ["Sunday"], opens: "22:00", closes: "02:00").Problems().Should().BeEmpty("a window may run past midnight");
        Values(opens: "00:00", closes: "24:00").Problems().Should().BeEmpty("the whole day");
        (Values() with { Targets = null }).Problems().Should().BeEmpty("no targets is the whole site's empty list");
        (Values(InstructionScope.Zone, ["A-CIT"]) with { Targets = null }).Problems().Should().ContainSingle("a zone schedule names its zones");
    }

    [Theory]
    [InlineData("no playlist")]
    [InlineData("a site target naming a zone")]
    [InlineData("a zone target naming nothing")]
    [InlineData("a display target that is not a code")]
    [InlineData("no day")]
    [InlineData("a day twice")]
    [InlineData("a day in lower case")]
    [InlineData("a day that is not a day")]
    [InlineData("a window of no length")]
    [InlineData("a window in seconds")]
    [InlineData("a window closing past 24:00")]
    [InlineData("targets too long to audit")]
    public void Values_Should_BeRefused_When_ARuleIsBroken(string reason)
    {
        var values = reason switch
        {
            "no playlist" => Values(playlist: Guid.Empty),
            "a site target naming a zone" => Values(InstructionScope.Site, ["A-CIT"]),
            "a zone target naming nothing" => Values(InstructionScope.Zone, []),
            "a display target that is not a code" => Values(InstructionScope.Display, ["<script>"]),
            "no day" => Values(days: []),
            "a day twice" => Values(days: ["Monday", "Monday"]),
            "a day in lower case" => Values(days: ["monday"]),
            "a day that is not a day" => Values(days: ["7"]),
            "a window of no length" => Values(opens: "09:00", closes: "09:00"),
            "a window in seconds" => Values(opens: "09:00:00", closes: "17:00"),
            "a window closing past 24:00" => Values(opens: "09:00", closes: "24:30"),
            _ => Values(InstructionScope.Zone, [.. Enumerable.Range(0, 20).Select(i => $"{i:00}" + new string('z', 198))])
        };

        values.Problems().Should().NotBeEmpty(reason);
        ((Action)(() => new AdvertisingSchedule(Site, values))).Should().Throw<ArgumentException>(reason);
    }

    [Fact]
    public void Constructor_Should_KeepTheDaysInTheWeeksOrderAndTheWindowAsLocalMinutes()
    {
        var schedule = Schedule(Values(days: ["Sunday", "Monday", "Friday"], opens: "22:30", closes: "01:15"));

        schedule.Days.Should().Be("Monday,Friday,Sunday");
        (schedule.OpensMinute, schedule.LengthMinutes).Should().Be((22 * 60 + 30, 165));
        schedule.Values().Should().BeEquivalentTo(new AdvertisingScheduleValues(Playlist, InstructionScope.Site, [], ["Monday", "Friday", "Sunday"], "22:30", "01:15"));
        schedule.AuditSummary().Should().Contain("\"days\":\"Monday,Friday,Sunday\"").And.Contain("\"opens\":\"22:30\"").And.Contain("\"closes\":\"01:15\"");
    }

    [Theory]
    [InlineData("2026-10-19T08:59:00", false, "a minute before it opens")]
    [InlineData("2026-10-19T09:00:00", true, "as it opens")]
    [InlineData("2026-10-19T16:59:00", true, "its last minute")]
    [InlineData("2026-10-19T17:00:00", false, "as it closes")]
    [InlineData("2026-10-18T12:00:00", false, "a Sunday, not one of its days")]
    public void OpenedBefore_Should_CoverTheWindowOfItsDays_When_AskedAtALocalMoment(string local, bool covered, string reason) =>
        (Schedule(Values()).OpenedBefore(DateTime.Parse(local, System.Globalization.CultureInfo.InvariantCulture)) is not null).Should().Be(covered, reason);

    [Theory]
    [InlineData("2026-10-23T23:30:00", "2026-10-23T22:00:00", "Friday evening")]
    [InlineData("2026-10-24T01:59:00", "2026-10-23T22:00:00", "past midnight into Saturday, opened on Friday")]
    [InlineData("2026-10-26T01:00:00", "2026-10-25T22:00:00", "Sunday's window runs into Monday: the week wraps")]
    public void OpenedBefore_Should_RunPastMidnight_When_TheWindowCrossesIt(string local, string opened, string reason)
    {
        var schedule = Schedule(Values(days: ["Friday", "Sunday"], opens: "22:00", closes: "02:00"));
        schedule.OpenedBefore(DateTime.Parse(local, System.Globalization.CultureInfo.InvariantCulture))
            .Should().Be(DateTime.Parse(opened, System.Globalization.CultureInfo.InvariantCulture), reason);
        schedule.OpenedBefore(new DateTime(2026, 10, 24, 2, 0, 0)).Should().BeNull("it closes at 02:00 on Saturday");
        schedule.OpenedBefore(new DateTime(2026, 10, 25, 1, 0, 0)).Should().BeNull("Saturday's evening is not one of its days");
    }

    [Fact]
    public void Resolve_Should_PreferTheMostSpecificTarget_When_SchedulesOverlap()
    {
        var site = Schedule(Values(opens: "08:00", closes: "20:00", playlist: Other));
        var zone = Schedule(Values(InstructionScope.Zone, ["A-VIS"], opens: "06:00", closes: "20:00"));
        var display = Schedule(Values(InstructionScope.Display, ["AD-1"], opens: "05:00", closes: "20:00"));
        var monday = new DateTime(2026, 10, 19, 12, 0, 0);

        Resolve(monday, "AD-1", site, zone, display).Should().BeSameAs(display, "a named display before a zone, though it opened earliest");
        Resolve(monday, "AD-2", site, zone, display).Should().BeSameAs(zone, "a zone the display shows before the site");
        AdvertisingSchedules.Resolve([site, zone, display], Active, Site, "AD-3", ["B-CIT"], monday).Should().BeSameAs(site, "only the site's targets this display");
    }

    [Fact]
    public void Resolve_Should_PreferTheWindowOpenedLastThenTheNewest_When_TargetsAreAlike()
    {
        var morning = Schedule(Values(opens: "06:00", closes: "20:00"));
        var peak = Schedule(Values(opens: "11:00", closes: "13:00", playlist: Other));
        var monday = new DateTime(2026, 10, 19, 12, 0, 0);

        Resolve(monday, "AD-1", morning, peak).Should().BeSameAs(peak, "the peak bank opened last");
        Resolve(monday.AddHours(2), "AD-1", morning, peak).Should().BeSameAs(morning, "once the peak closes");

        var first = Schedule(Values(opens: "11:00", closes: "13:00"), created: new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc));
        var second = Schedule(Values(opens: "11:00", closes: "13:00", playlist: Other), created: new DateTime(2026, 10, 2, 8, 0, 0, DateTimeKind.Utc));
        Resolve(monday, "AD-1", first, second).Should().BeSameAs(second, "the same window: the newest");
        Resolve(monday, "AD-1", second, first).Should().BeSameAs(second, "whatever the order they are read in");
    }

    [Fact]
    public void Resolve_Should_IgnoreRetiredSchedulesRetiredPlaylistsAndOtherSites_When_Choosing()
    {
        var site = Schedule(Values(playlist: Other));
        var display = Schedule(Values(InstructionScope.Display, ["AD-1"]));
        var monday = new DateTime(2026, 10, 19, 12, 0, 0);

        display.Retire();
        Resolve(monday, "AD-1", site, display).Should().BeSameAs(site, "a retired schedule applies nowhere");
        display.Restore();
        AdvertisingSchedules.Resolve([site, display], new HashSet<Guid> { Other }, Site, "AD-1", Zones, monday)
            .Should().BeSameAs(site, "a schedule whose playlist is retired stops applying, and the next one does");
        AdvertisingSchedules.Resolve([Schedule(Values(), site: "DMO")], Active, Site, "AD-1", Zones, monday).Should().BeNull("another site's schedule");
        AdvertisingSchedules.Resolve([], Active, Site, "AD-1", Zones, monday).Should().BeNull("an empty schedule leaves the instructions");
    }

    [Fact]
    public void Timeline_Should_MergeMinutesIntoStretches_When_AWeekIsPreviewed()
    {
        var schedule = Schedule(Values(days: ["Monday", "Tuesday"]));
        // Monday 19 October 2026, local midnight in London (British Summer Time, UTC+1).
        var from = new DateTime(2026, 10, 18, 23, 0, 0, DateTimeKind.Utc);

        var timeline = AdvertisingSchedules.Timeline([schedule], Active, Site, "AD-1", Zones, London, from, 2);

        timeline.Select(s => (s.FromUtc, s.ToUtc, s.Schedule)).Should().Equal(
            (from, new DateTime(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc), null),
            (new DateTime(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 19, 16, 0, 0, DateTimeKind.Utc), schedule),
            (new DateTime(2026, 10, 19, 16, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc), null),
            (new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 20, 16, 0, 0, DateTimeKind.Utc), schedule),
            (new DateTime(2026, 10, 20, 16, 0, 0, DateTimeKind.Utc), from.AddDays(2), null));
        AdvertisingSchedules.Timeline([], Active, Site, "AD-1", Zones, London, from, 7).Should().ContainSingle()
            .Which.Should().Be(new AdvertisingSchedules.Segment(from, from.AddDays(7), null), "an empty schedule: instructions all week");
    }

    [Fact]
    public void Timeline_Should_KeepLocalHours_When_TheClocksChange()
    {
        var working = Schedule(Values(days: ["Monday"]));
        // 23 March 2026 (GMT, UTC+0) and 30 March 2026 (BST, UTC+1): 09:00 local is 09:00 UTC, then 08:00 UTC.
        var march = AdvertisingSchedules.Timeline([working], Active, Site, "AD-1", Zones, London, new DateTime(2026, 3, 23, 0, 0, 0, DateTimeKind.Utc), 8)
            .Where(s => s.Schedule is not null).Select(s => (s.FromUtc, s.ToUtc)).ToList();

        march.Should().Equal(
            (new DateTime(2026, 3, 23, 9, 0, 0, DateTimeKind.Utc), new DateTime(2026, 3, 23, 17, 0, 0, DateTimeKind.Utc)),
            (new DateTime(2026, 3, 30, 8, 0, 0, DateTimeKind.Utc), new DateTime(2026, 3, 30, 16, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Timeline_Should_SkipAMissingHourAndRepeatARepeatedOne_When_AWindowFallsInIt()
    {
        var night = Schedule(Values(days: ["Sunday"], opens: "01:15", closes: "01:45"));

        // 29 March 2026: 01:00 to 02:00 local never happens (the clocks go from 01:00 GMT to 02:00 BST).
        AdvertisingSchedules.Timeline([night], Active, Site, "AD-1", Zones, London, new DateTime(2026, 3, 28, 23, 0, 0, DateTimeKind.Utc), 1)
            .Should().OnlyContain(s => s.Schedule == null, "the window's local minutes are skipped that day");

        // 25 October 2026: 01:00 to 02:00 local happens twice (00:00 to 01:00 UTC in summer time, then 01:00 to 02:00 UTC).
        AdvertisingSchedules.Timeline([night], Active, Site, "AD-1", Zones, London, new DateTime(2026, 10, 24, 23, 0, 0, DateTimeKind.Utc), 1)
            .Where(s => s.Schedule is not null).Select(s => (s.FromUtc, s.ToUtc)).Should().Equal(
                (new DateTime(2026, 10, 25, 0, 15, 0, DateTimeKind.Utc), new DateTime(2026, 10, 25, 0, 45, 0, DateTimeKind.Utc)),
                (new DateTime(2026, 10, 25, 1, 15, 0, DateTimeKind.Utc), new DateTime(2026, 10, 25, 1, 45, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Timeline_Should_BeRefused_When_ItIsNotUtcOrTooLong()
    {
        ((Action)(() => AdvertisingSchedules.Timeline([], Active, Site, "AD-1", Zones, London, new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Local), 7)))
            .Should().Throw<ArgumentException>();
        ((Action)(() => AdvertisingSchedules.Timeline([], Active, Site, "AD-1", Zones, London, new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc), 15)))
            .Should().Throw<ArgumentOutOfRangeException>();
    }
}
