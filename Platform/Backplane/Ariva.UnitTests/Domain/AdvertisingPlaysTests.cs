using Ariva.Core.Advertising;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using FluentAssertions;

namespace Ariva.UnitTests.Domain;

/// <summary>
/// ARV-144: a reported play is what it says (an advertisement, started within the last day and not in the future beyond the
/// player's clock, at most a slot's ceiling long, ended by the time it is reported, within a few seconds) and was scheduled:
/// the advertisement is on the playlist that applied to that display when the play started, or a minute before (the board
/// the player last read). It lasted no longer than its item's slot, counts as completed only when it filled it, and overlaps
/// no other play of the display. The scheduled airtime gives each item of a playlist its weight times its slot of every
/// cycle, slides included in the cycle.
/// </summary>
public sealed class AdvertisingPlaysTests
{
    private const string Site = "DMO";
    private static readonly DateTime Now = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Dubai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Dubai");
    private static readonly Guid Ad = Guid.Parse("0199a000-0000-7000-8000-0000000000c1");
    private static readonly Guid Other = Guid.Parse("0199a000-0000-7000-8000-0000000000c2");
    private static int _sequence;

    private static AdvertisingPlayReport Play(DateTime? started = null, int duration = 10_000, Guid? media = null) =>
        new(media ?? Ad, started ?? Now.AddMinutes(-5), duration, true);

    private static AdvertisingPlaylist Playlist(params AdvertisingPlaylistItem[] items) =>
        new(Site, new AdvertisingPlaylistValues("Bank", items)) { Id = Guid.CreateVersion7() };

    private static AdvertisingSchedule Schedule(AdvertisingPlaylist playlist, string opens, string closes, InstructionScope scope = InstructionScope.Site,
        string[] targets = null) =>
        new(Site, new AdvertisingScheduleValues(playlist.Id.GetValueOrDefault(), scope, targets ?? [], ["Thursday"], opens, closes))
        {
            Id = Guid.CreateVersion7(),
            CreatedOn = Now.AddDays(-1).AddMinutes(Interlocked.Increment(ref _sequence))
        };

    [Fact]
    public void Problem_Should_BeNone_When_ThePlayIsWhatItSays()
    {
        AdvertisingPlays.Problem(Play(), Now).Should().BeNull();
        AdvertisingPlays.Problem(Play(Now.AddHours(-23).AddMinutes(-59)), Now).Should().BeNull("a day without the network");
        AdvertisingPlays.Problem(Play(Now.AddSeconds(4), duration: 1_000), Now).Should().BeNull("the player's clock may run a few seconds ahead");
        AdvertisingPlays.Problem(Play(duration: AdvertisingPlays.MinDurationMilliseconds), Now).Should().BeNull("a second on screen");
        AdvertisingPlays.Problem(Play(duration: AdvertisingPlays.MaxDurationMilliseconds), Now).Should().BeNull("a video's slot at its ceiling");
        AdvertisingPlays.Problem(Play(Now.AddSeconds(-10), duration: 15_000), Now).Should().BeNull("it ended within the player's timing of the report");
    }

    [Theory]
    [InlineData(10, 10_000, true, true)]
    [InlineData(10, 12_000, true, true)]
    [InlineData(10, 8_000, true, true)]
    [InlineData(10, 12_001, false, false)]
    [InlineData(10, 7_999, true, false)]
    [InlineData(10, 7_999, false, true)]
    [InlineData(10, 0, false, true)]
    [InlineData(120, 122_000, true, true)]
    public void SlotProblem_Should_HoldThePlayToItsSlot_When_ItIsChecked(int slotSeconds, int duration, bool completed, bool kept)
    {
        var play = new AdvertisingPlayReport(Ad, Now.AddMinutes(-5), duration, completed);

        (AdvertisingPlays.SlotProblem(play, slotSeconds) is null).Should().Be(kept,
            "a play lasts no longer than its slot and counts as completed only when it filled it, 2 seconds either way");
    }

    [Theory]
    [InlineData(0, 10_000, 10_000, 10_000, false)]
    [InlineData(0, 10_000, 9_000, 10_000, false)]
    [InlineData(0, 10_000, 8_999, 10_000, true)]
    [InlineData(0, 10_000, 2_000, 3_000, true)]
    [InlineData(5_000, 10_000, 0, 10_000, true)]
    [InlineData(20_000, 10_000, 0, 10_000, false)]
    [InlineData(0, 120_000, 50_000, 1_000, true)]
    [InlineData(0, 1_000, 1, 1_000, true)]
    [InlineData(0, 1_000, 750, 1_000, false)]
    [InlineData(0, 1_000, 749, 1_000, true)]
    [InlineData(0, 2_000, 1_500, 2_000, false)]
    [InlineData(0, 2_000, 1_499, 2_000, true)]
    [InlineData(0, 120_000, 119_000, 10_000, false)]
    [InlineData(0, 120_000, 118_999, 10_000, true)]
    public void Overlap_Should_FindTwoPlaysAtOnce_When_TheyShareMoreThanAQuarterOfTheShorterOrASecond(int aStart, int aDuration, int bStart, int bDuration, bool overlap)
    {
        AdvertisingPlays.Overlap(Now.AddMilliseconds(aStart), aDuration, Now.AddMilliseconds(bStart), bDuration).Should().Be(overlap);
        AdvertisingPlays.Overlap(Now.AddMilliseconds(bStart), bDuration, Now.AddMilliseconds(aStart), aDuration).Should().Be(overlap, "either way round");
    }

    public static TheoryData<string, AdvertisingPlayReport> Forged => new()
    {
        { "no advertisement", Play(media: Guid.Empty) },
        { "a local time", Play(DateTime.SpecifyKind(Now.AddMinutes(-5), DateTimeKind.Unspecified)) },
        { "older than a day", Play(Now.AddHours(-24).AddSeconds(-1)) },
        { "in the future", Play(Now.AddMinutes(3)) },
        { "started in the future, however short", Play(Now.AddSeconds(6), duration: 1_000) },
        { "shorter than a second", Play(duration: 999) },
        { "no time on screen", Play(duration: 0) },
        { "the last instant there is", Play(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)) },
        { "the first instant there is", Play(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc)) },
        { "a negative length", Play(duration: -1) },
        { "longer than any slot", Play(duration: AdvertisingPlays.MaxDurationMilliseconds + 1) },
        { "not over yet", Play(Now.AddSeconds(-10), duration: 60_000) },
        { "ended more than the player's timing after it is reported", Play(Now.AddSeconds(-10), duration: 15_001) },
        { "nothing", null }
    };

    [Theory]
    [MemberData(nameof(Forged))]
    public void Problem_Should_RefuseThePlay_When_ItCannotBeWhatItSays(string reason, AdvertisingPlayReport play) =>
        AdvertisingPlays.Problem(play, Now).Should().NotBeNull(reason);

    [Fact]
    public void OverlapsAny_Should_TakeOneOfShortPlaysThatStack_When_TheyAreReportedTogether()
    {
        var stored = new[] { (Now, 120_000) };
        AdvertisingPlays.OverlapsAny(Now.AddSeconds(50), 1_000, stored).Should().BeTrue("a short play inside a long one");

        var taken = new List<(DateTime, int)>();
        foreach (var start in Enumerable.Range(0, 10).Select(i => Now.AddMinutes(5).AddMilliseconds(i)))
            if (!AdvertisingPlays.OverlapsAny(start, 1_000, taken))
                taken.Add((start, 1_000));

        taken.Should().ContainSingle("ten plays of a second, a millisecond apart, are one second on screen");
    }

    [Fact]
    public void Overlap_Should_KeepTheAirtimeTakenToTheStretch_When_AnyPlaysAreClaimed()
    {
        // A seeded sequence through Bogus (not System.Random, CA5394).
        var random = new Bogus.Randomizer(144);
        for (var round = 0; round < 200; round++)
        {
            var claimed = Enumerable.Range(0, 100)
                .Select(_ => (Start: Now.AddMilliseconds(random.Number(0, 600_000)),
                    Length: random.Number(0, 3) == 0
                        ? random.Number(AdvertisingPlays.MinDurationMilliseconds, 3_000)
                        : random.Number(AdvertisingPlays.MinDurationMilliseconds, AdvertisingPlays.MaxDurationMilliseconds)))
                .OrderBy(p => p.Start)
                .ToList();
            var taken = new List<(DateTime StartedUtc, int DurationMilliseconds)>();
            foreach (var (start, length) in claimed)
                if (!AdvertisingPlays.OverlapsAny(start, length, taken))
                    taken.Add((start, length));

            // What the plays taken say exceeds the time they cover by at most the tolerance at each change of play.
            var said = taken.Sum(p => (double)p.DurationMilliseconds);
            var covered = 0d;
            var (from, to) = (DateTime.MinValue, DateTime.MinValue);
            foreach (var (start, length) in taken.OrderBy(p => p.StartedUtc))
            {
                var end = start.AddMilliseconds(length);
                if (start > to)
                {
                    covered += (to - from).TotalMilliseconds;
                    (from, to) = (start, end);
                }
                else if (end > to)
                    to = end;
            }

            covered += (to - from).TotalMilliseconds;
            (said - covered).Should().BeLessThanOrEqualTo((taken.Count - 1) * (double)AdvertisingPlays.OverlapTolerance, $"round {round}");
            var inside = taken.Where((a, i) => taken.Where((_, j) => j != i).Any(b => b.StartedUtc <= a.StartedUtc &&
                a.StartedUtc.AddMilliseconds(a.DurationMilliseconds) <= b.StartedUtc.AddMilliseconds(b.DurationMilliseconds)));
            inside.Should().BeEmpty("no play taken lies inside another");
        }
    }

    [Fact]
    public void ScheduledItem_Should_NameThePlaylistThatApplied_When_ItNamesTheAdvertisement()
    {
        var morning = Playlist(new AdvertisingPlaylistItem(AdvertisingItemKind.Media, Ad, 10));
        var peak = Playlist(new AdvertisingPlaylistItem(AdvertisingItemKind.Media, Other, 10));
        // Thursday 1 October at DMO (UTC+4): the morning bank 06:00 to 20:00, the peak bank 11:00 to 13:00 (07:00 to 09:00 UTC).
        var schedules = new[] { Schedule(morning, "06:00", "20:00"), Schedule(peak, "11:00", "13:00") };
        var playlists = new[] { morning, peak }.ToDictionary(p => p.Id.GetValueOrDefault());

        Scheduled(Ad, Now.AddHours(-3), schedules, playlists).Should().Be(morning.Id, "08:00 local: the morning bank");
        Scheduled(Ad, Now, schedules, playlists).Should().BeNull("12:00 local: the peak bank applies, and it does not name the advertisement");
        Scheduled(Other, Now, schedules, playlists).Should().Be(peak.Id);
        Scheduled(Other, Now.AddHours(-1).AddSeconds(30), schedules, playlists).Should().Be(peak.Id, "a play started a moment into the peak window");
        Scheduled(Ad, Now.AddHours(-1).AddSeconds(30), schedules, playlists).Should().Be(morning.Id, "a minute before, the morning bank still applied");
        Scheduled(Ad, Now.AddHours(-1).AddMinutes(2), schedules, playlists).Should().BeNull("well into the peak window");
        Scheduled(Ad, Now.AddHours(-3), schedules, new Dictionary<Guid, AdvertisingPlaylist>()).Should().BeNull("a retired playlist");
        Scheduled(Ad, Now.AddHours(13), schedules, playlists).Should().BeNull("01:00 on Friday: nothing applies");
    }

    [Fact]
    public void ScheduledItem_Should_RefuseThePlay_When_TheScheduleIsAnotherDisplays()
    {
        var bank = Playlist(new AdvertisingPlaylistItem(AdvertisingItemKind.Media, Ad, 10));
        var schedules = new[] { Schedule(bank, "06:00", "20:00", InstructionScope.Display, ["AD-2"]) };
        var playlists = new[] { bank }.ToDictionary(p => p.Id.GetValueOrDefault());

        Scheduled(Ad, Now, schedules, playlists, "AD-1").Should().BeNull();
        Scheduled(Ad, Now, schedules, playlists, "AD-2").Should().Be(bank.Id);
    }

    [Fact]
    public void ScheduledItem_Should_FindTheItemWhoseSlotThePlayKept_When_ThePlaylistNamesTheAdvertisement()
    {
        var bank = Playlist(new AdvertisingPlaylistItem(AdvertisingItemKind.Slide, Ad, 30), new AdvertisingPlaylistItem(AdvertisingItemKind.Media, Ad, 10),
            new AdvertisingPlaylistItem(AdvertisingItemKind.Media, Ad, 30));
        var schedules = new[] { Schedule(bank, "06:00", "20:00") };
        var playlists = new[] { bank }.ToDictionary(p => p.Id.GetValueOrDefault());
        var prepared = AdvertisingSchedules.Prepare(schedules, playlists.Keys.ToHashSet(), Site, "AD-1", ["A-CIT"]);
        var started = Now.AddHours(-3);

        AdvertisingPlays.ScheduledItem(new AdvertisingPlayReport(Ad, started, 10_000, true), prepared, playlists, Dubai)!.Value.Item.Seconds.Should().Be(10);
        var longer = AdvertisingPlays.ScheduledItem(new AdvertisingPlayReport(Ad, started, 30_000, true), prepared, playlists, Dubai);
        longer.Should().NotBeNull("the advertisement's second item, never the slide with the same id");
        longer!.Value.PlaylistId.Should().Be(bank.Id.GetValueOrDefault());
        longer.Value.Item.Seconds.Should().Be(30);
        AdvertisingPlays.ScheduledItem(new AdvertisingPlayReport(Ad, started, 40_000, false), prepared, playlists, Dubai).Should().BeNull("longer than any of its slots");
        AdvertisingPlays.ScheduledItem(new AdvertisingPlayReport(Ad, started, 20_000, true), prepared, playlists, Dubai).Should().BeNull("completed, yet it filled no slot");
        AdvertisingPlays.ScheduledItem(new AdvertisingPlayReport(Ad, started, 20_000, false), prepared, playlists, Dubai).Should().NotBeNull("the 30 second slot, cut short");
    }

    [Fact]
    public void Prepare_Should_ShareOneKey_When_DisplaysHaveTheSameSchedules()
    {
        var bank = Playlist(new AdvertisingPlaylistItem(AdvertisingItemKind.Media, Ad, 10));
        var schedules = new[] { Schedule(bank, "06:00", "20:00"), Schedule(bank, "06:00", "20:00", InstructionScope.Display, ["AD-2"]) };
        var active = new HashSet<Guid> { bank.Id.GetValueOrDefault() };

        var first = AdvertisingSchedules.Prepare(schedules, active, Site, "AD-1", ["A-CIT"]);
        var third = AdvertisingSchedules.Prepare(schedules, active, Site, "AD-3", ["A-CIT"]);
        var second = AdvertisingSchedules.Prepare(schedules, active, Site, "AD-2", ["A-CIT"]);

        third.Key.Should().Be(first.Key, "the same site-wide schedule");
        second.Key.Should().NotBe(first.Key, "a schedule of its own as well");
        AdvertisingSchedules.Prepare(schedules, new HashSet<Guid>(), Site, "AD-1", ["A-CIT"]).Timeline(Dubai, Now, 1)
            .Should().ContainSingle("nothing applies all day: one stretch").Which.Schedule.Should().BeNull();
    }

    private static Guid? Scheduled(Guid media, DateTime started, IReadOnlyList<AdvertisingSchedule> schedules, IReadOnlyDictionary<Guid, AdvertisingPlaylist> playlists,
        string display = "AD-1") =>
        AdvertisingPlays.ScheduledItem(Play(started, media: media), AdvertisingSchedules.Prepare(schedules, playlists.Keys.ToHashSet(), Site, display, ["A-CIT"]),
            playlists, Dubai)?.PlaylistId;

    [Fact]
    public void Scheduled_Should_GiveEachAdvertisementItsShareOfTheCycle_When_APlaylistApplied()
    {
        var bank = Playlist(new AdvertisingPlaylistItem(AdvertisingItemKind.Media, Ad, 10, 2), new AdvertisingPlaylistItem(AdvertisingItemKind.Media, Other, 20),
            new AdvertisingPlaylistItem(AdvertisingItemKind.Slide, Guid.Parse("0199a000-0000-7000-8000-0000000000c3"), 10));
        var schedule = Schedule(bank, "06:00", "20:00");
        var hour = new AdvertisingSchedules.Segment(Now, Now.AddHours(1), schedule);
        var none = new AdvertisingSchedules.Segment(Now.AddHours(1), Now.AddHours(2), null);

        var scheduled = AdvertisingAirtime.Scheduled([hour, none], id => id == bank.Id ? bank.ItemList : []);

        // A cycle of 2 x 10 + 20 + 10 = 50 seconds: the first advertisement 20 of them, the second 20, the slide 10.
        scheduled.Should().BeEquivalentTo(new Dictionary<Guid, double> { [Ad] = 3_600_000 * 20 / 50d, [Other] = 3_600_000 * 20 / 50d });
        AdvertisingAirtime.Scheduled([hour], _ => []).Should().BeEmpty("nothing playable that day");
        AdvertisingAirtime.Delivered(1_440_000, 1_440_000).Should().Be(1);
        AdvertisingAirtime.Delivered(720_000, 1_440_000).Should().Be(0.5);
        AdvertisingAirtime.Delivered(10_000, 0).Should().BeNull("nothing scheduled");
    }
}
