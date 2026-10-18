using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using FluentAssertions;

namespace Ariva.UnitTests.Domain;

/// <summary>
/// ARV-143b: what a display plays. One cycle of a playlist plays each item as many times as its weight, spread through the
/// cycle; the board says which schedule applies now and until when (the first later minute that differs, at most the
/// display's stale threshold ahead and never past the site's next local midnight, when advertisements' dates turn); a
/// display's wait rule is 1 to 240 minutes or none.
/// Europe/London 2026: summer time from 29 March to 25 October.
/// </summary>
public sealed class AdvertisingBoardTests
{
    private const string Site = "AUH-TA";
    private static readonly Guid Playlist = Guid.Parse("0199a000-0000-7000-8000-0000000000b1");
    private static readonly Guid Other = Guid.Parse("0199a000-0000-7000-8000-0000000000b2");
    private static readonly IReadOnlySet<Guid> Active = new HashSet<Guid> { Playlist, Other };
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly string[] Zones = ["A-CIT"];
    private static int _sequence;

    private static AdvertisingSchedule Schedule(string opens, string closes, Guid? playlist = null, InstructionScope scope = InstructionScope.Site,
        string[] targets = null, string[] days = null) =>
        new(Site, new AdvertisingScheduleValues(playlist ?? Playlist, scope, targets ?? [], days ?? ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"],
            opens, closes))
        {
            Id = Guid.CreateVersion7(),
            CreatedOn = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc).AddMinutes(Interlocked.Increment(ref _sequence))
        };

    private static DateTime Utc(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(new[] { 2, 1 }, new[] { 0, 1, 0 })]
    [InlineData(new[] { 1, 1, 1 }, new[] { 0, 1, 2 })]
    [InlineData(new[] { 3, 1 }, new[] { 0, 0, 1, 0 })]
    [InlineData(new[] { 1, 2, 1 }, new[] { 1, 0, 2, 1 })]
    [InlineData(new[] { 1 }, new[] { 0 })]
    [InlineData(new int[0], new int[0])]
    public void PlayingOrder_Should_SpreadEachItemAsOftenAsItsWeight_When_ACycleIsBuilt(int[] weights, int[] expected) =>
        AdvertisingPlaylist.PlayingOrder(weights).Should().Equal(expected);

    [Fact]
    public void PlayingOrder_Should_GiveEachItemItsWeightInPlaces_When_WeightsAreMixed()
    {
        var weights = Enumerable.Range(0, AdvertisingPlaylist.MaxItems).Select(i => i % 10 + 1).ToArray();

        var order = AdvertisingPlaylist.PlayingOrder(weights);

        order.Should().HaveCount(weights.Sum(), "a cycle of 40 items of weights 1 to 10");
        order.GroupBy(i => i).Select(g => (g.Key, g.Count())).OrderBy(g => g.Key).Should().Equal(weights.Select((w, i) => (i, w)));
        // Spread: in each half of the cycle, an item of weight 10 plays 5 times, give or take one.
        var heavy = weights.Select((w, i) => (w, i)).First(p => p.w == 10).i;
        order.Take(order.Count / 2).Count(i => i == heavy).Should().BeInRange(4, 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void PlayingOrder_Should_BeRefused_When_AWeightIsOutOfRange(int weight) =>
        ((Action)(() => AdvertisingPlaylist.PlayingOrder([1, weight]))).Should().Throw<ArgumentException>();

    [Fact]
    public void PlayingOrder_Should_BeRefused_When_ThereAreMoreItemsThanAPlaylistHolds() =>
        ((Action)(() => AdvertisingPlaylist.PlayingOrder(Enumerable.Repeat(1, AdvertisingPlaylist.MaxItems + 1).ToList()))).Should().Throw<ArgumentException>();

    private static readonly TimeSpan Ahead = AdvertisingSchedules.MaxHorizon;

    [Fact]
    public void Now_Should_PlayTheScheduleUntilItsWindowCloses_When_ItApplies()
    {
        var working = Schedule("09:00", "17:00");

        // Monday 19 October 2026, 16:45 in London (summer time, UTC+1): the window closes at 17:00 local, 16:00 UTC.
        var (schedule, until) = AdvertisingSchedules.Now([working], Active, Site, "AD-1", Zones, London, Utc(10, 19, 15, 45).AddSeconds(25), Ahead);

        schedule.Should().BeSameAs(working);
        until.Should().Be(Utc(10, 19, 16));
    }

    [Fact]
    public void Now_Should_StopAtTheNextChange_When_APeakWindowOpensOrAMoreSpecificOneCloses()
    {
        var allDay = Schedule("06:00", "20:00");
        var peak = Schedule("11:00", "13:00", Other);
        var own = Schedule("12:30", "12:45", Other, InstructionScope.Display, ["AD-1"]);

        AdvertisingSchedules.Now([allDay, peak, own], Active, Site, "AD-1", Zones, London, Utc(10, 19, 9, 40), Ahead)
            .Should().Be((allDay, Utc(10, 19, 10, 0)), "the peak opens at 11:00 local");
        AdvertisingSchedules.Now([allDay, peak, own], Active, Site, "AD-1", Zones, London, Utc(10, 19, 11, 31), Ahead)
            .Should().Be((own, Utc(10, 19, 11, 45)), "the display's own window, until it closes and the peak plays again");
        AdvertisingSchedules.Now([allDay, peak, own], Active, Site, "AD-2", Zones, London, Utc(10, 19, 11, 31), Ahead)
            .Should().Be((peak, Utc(10, 19, 12, 0)), "another display: the peak, until it closes at 13:00 local");
    }

    [Fact]
    public void Now_Should_EndAtTheSitesNextLocalMidnight_When_NothingChangesBefore()
    {
        var overnight = Schedule("20:00", "08:00");

        // Monday 23:45 local: the window runs to Tuesday 08:00, but the board is judged again at midnight, 23:00 UTC.
        AdvertisingSchedules.Now([overnight], Active, Site, "AD-1", Zones, London, Utc(10, 19, 22, 45), Ahead)
            .Should().Be((overnight, Utc(10, 19, 23)));
        AdvertisingSchedules.Now([], Active, Site, "AD-1", Zones, London, Utc(10, 19, 22, 45), Ahead)
            .Should().Be(((AdvertisingSchedule)null, Utc(10, 19, 23)), "an empty schedule: instructions until midnight");
        AdvertisingSchedules.Now([Schedule("09:00", "17:00")], new HashSet<Guid>(), Site, "AD-1", Zones, London, Utc(10, 19, 11), Ahead)
            .Schedule.Should().BeNull("a retired playlist's schedule plays nowhere");
    }

    [Theory]
    [InlineData(150)]
    [InlineData(1800)]
    public void Now_Should_LookNoFurtherThanTheHorizon_When_ASiteHasAsManySchedulesAsItMay(int staleSeconds)
    {
        var schedules = Enumerable.Range(0, AdvertisingSchedule.MaxSchedulesPerSite).Select(_ => Schedule("09:00", "17:00")).ToList();
        var now = Utc(10, 19, 11, 0).AddSeconds(7);

        var (schedule, until) = AdvertisingSchedules.Now(schedules, Active, Site, "AD-1", Zones, London, now, TimeSpan.FromSeconds(staleSeconds));

        schedule.Should().BeSameAs(schedules[^1], "the newest of alike schedules");
        until.Should().Be(now.AddSeconds(staleSeconds), "the plan holds no longer than the display's board may be shown");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1801)]
    public void Now_Should_BeRefused_When_TheHorizonIsOutOfRange(int seconds) =>
        ((Action)(() => AdvertisingSchedules.Now([], Active, Site, "AD-1", Zones, London, Utc(10, 19, 11), TimeSpan.FromSeconds(seconds))))
            .Should().Throw<ArgumentOutOfRangeException>();

    [Fact]
    public void NextLocalMidnightUtc_Should_FollowTheClocks_When_TheyChangeOvernight()
    {
        // Saturday 28 March (winter time, UTC+0): Sunday starts at 00:00 UTC, before the clocks go forward at 01:00.
        AdvertisingSchedules.NextLocalMidnightUtc(Utc(3, 28, 12), London).Should().Be(Utc(3, 29, 0));
        // Sunday 29 March in summer time: Monday starts at 23:00 UTC.
        AdvertisingSchedules.NextLocalMidnightUtc(Utc(3, 29, 12), London).Should().Be(Utc(3, 29, 23));
        // Saturday 24 October (summer time): Sunday starts at 23:00 UTC; on Sunday, after the clocks go back, at 00:00 UTC.
        AdvertisingSchedules.NextLocalMidnightUtc(Utc(10, 24, 12), London).Should().Be(Utc(10, 24, 23));
        AdvertisingSchedules.NextLocalMidnightUtc(Utc(10, 25, 12), London).Should().Be(Utc(10, 26, 0));
    }

    [Fact]
    public void Now_Should_BeRefused_When_TheInstantIsNotUtc() =>
        ((Action)(() => AdvertisingSchedules.Now([], Active, Site, "AD-1", Zones, London, new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Local), Ahead)))
            .Should().Throw<ArgumentException>();

    [Theory]
    [InlineData(null, true)]
    [InlineData(1, true)]
    [InlineData(240, true)]
    [InlineData(0, false)]
    [InlineData(241, false)]
    public void DisplayValues_Should_TakeAWaitRuleOf1To240Minutes_When_OneIsSet(int? minutes, bool taken)
    {
        var values = new DisplayValues("Arrivals", null, DisplayOrientation.Landscape, ["en"], 5, 1, 150,
            [new DisplayEntry("A-CIT", new Dictionary<string, string> { ["en"] = "Citizens" })], new Dictionary<string, string> { ["en"] = "Follow the signs" },
            true, DisplayLayout.Split, 60, DisplaySecondRegion.Advertising, minutes);

        values.Problems().Should().HaveCount(taken ? 0 : 1);
        if (taken)
            new Display("DMO", "AD-1", values).AdvertisingPauseMinutes.Should().Be(minutes);
    }
}
