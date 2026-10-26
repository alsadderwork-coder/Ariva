using Ariva.Core.Services.Border;
using Ariva.Core.Staffing;
using Ariva.Infra.Services.Border;
using Ariva.Infra.Staffing;
using FluentAssertions;
using Row = Ariva.Infra.Staffing.StaffingReadBack.StaffingRow;

namespace Ariva.UnitTests.Border;

/// <summary>
/// ARV-181a, CWE-501: the staffing API checks every stored row of <c>lane_staffing</c> again as it reads it, because the
/// table is input like any other. Each column's edge is accepted and the value just outside it refused, every double refuses
/// NaN and both infinities, every name refuses one Ariva never writes, and the pairings of script 0053 and of the engine
/// hold. A lane's minute that does not hold together (12 or 14 rows, a duplicated or out-of-order index, rows of two
/// minutes, a start off the plan) is not shown: the lane is answered Stale with no numbers.
/// </summary>
public sealed class StaffingReadBackTests
{
    private const string Key = "DMO/A-VIS";

    // 14:07: the first interval runs 8 minutes to 14:15, then 12 quarter hours.
    private static readonly DateTime Minute = new(2026, 10, 15, 14, 7, 0, DateTimeKind.Utc);
    private static readonly StaffingLane Lane = new("A-VIS", "VIS");

    /// <summary>A minute as the engine writes it: 30 arriving, 3 of 4 desks recommended for the wait, 2 open now, measured.</summary>
    private static List<Row> Minute13()
    {
        var plan = StaffingRecommendation.Plan(Minute);
        return [.. plan.Select((slot, k) => Valid(k, slot.StartUtc, slot.Minutes))];
    }

    private static Row Valid(int index = 0, DateTime? start = null, int minutes = 8) => new()
    {
        ZoneKey = Key,
        MinuteUtc = Minute,
        IntervalIndex = index,
        Lane = "VIS",
        StartUtc = start ?? Minute,
        Minutes = minutes,
        Arrivals = 30,
        Recommended = 3,
        Bound = "Wait",
        AtLimit = false,
        Shortfall = null,
        WaitRecommended = 6,
        WaitOpen = 8,
        NoServiceOpen = false,
        Gap = "None",
        LaneGap = "None",
        QueueNow = 42,
        OpenNow = 2,
        OpenNowDegraded = false,
        Desks = 4,
        TargetMinutes = 10,
        BusyCap = 0.85,
        ThroughputPerDesk = 0.5,
        ThroughputSource = "Measured"
    };

    // An interval without a recommendation, as the engine writes one (StaffingRecommendation.Empty).
    private static void NoRecommendation(Row r, string gap)
    {
        r.Gap = gap;
        r.Recommended = null;
        r.Bound = "None";
        r.WaitRecommended = null;
        r.WaitOpen = null;
    }

    private static readonly Dictionary<string, Action<Row>> Changes = BuildChanges();

    private static Dictionary<string, Action<Row>> BuildChanges()
    {
        var changes = new Dictionary<string, Action<Row>>(StringComparer.Ordinal)
        {
            ["as written"] = _ => { },

            ["lane 2 letters"] = r => r.Lane = "AB",
            ["lane 4 letters"] = r => r.Lane = "ABCD",
            ["lane vis"] = r => r.Lane = "vis",
            ["lane VISAS"] = r => r.Lane = "VISAS",
            ["lane 1 letter"] = r => r.Lane = "V",
            ["lane with a digit"] = r => r.Lane = "V1S",
            ["lane null"] = r => r.Lane = null,

            ["minute not whole"] = r => { r.MinuteUtc = Minute.AddSeconds(1); r.StartUtc = r.MinuteUtc; },
            ["start not whole"] = r => r.StartUtc = Minute.AddSeconds(30),
            ["start at the minute"] = r => r.StartUtc = Minute,
            ["start 194 minutes on"] = r => r.StartUtc = Minute.AddMinutes(194),
            ["start 195 minutes on"] = r => r.StartUtc = Minute.AddMinutes(195),
            ["start a minute before"] = r => r.StartUtc = Minute.AddMinutes(-1),

            ["minutes 1"] = r => r.Minutes = 1,
            ["minutes 15"] = r => r.Minutes = 15,
            ["minutes 0"] = r => r.Minutes = 0,
            ["minutes 16"] = r => r.Minutes = 16,

            ["arrivals 0"] = r => r.Arrivals = 0,
            ["arrivals a million"] = r => r.Arrivals = 1_000_000,
            ["arrivals just below 0"] = r => r.Arrivals = -1e-9,
            ["arrivals just above a million"] = r => r.Arrivals = 1_000_000.001,
            ["arrivals unknown with a recommendation"] = r => r.Arrivals = null,
            ["arrivals unknown without a recommendation"] = r => { NoRecommendation(r, "FlightWithoutPassengers"); r.Arrivals = null; },

            ["recommended 0, no desk needed"] = r => { r.Recommended = 0; r.Bound = "None"; r.WaitRecommended = 0; },
            ["recommended 1,000 of 1,000 desks"] = r => { r.Desks = 1_000; r.Recommended = 1_000; },
            ["recommended 1,001 of 1,001 desks"] = r => { r.Desks = 1_001; r.Recommended = 1_001; },
            ["recommended -1"] = r => r.Recommended = -1,
            ["recommended above the desks"] = r => r.Recommended = 5,

            ["bound Busy"] = r => r.Bound = "Busy",
            ["bound Both"] = r => r.Bound = "Both",
            ["bound None with a desk needed"] = r => r.Bound = "None",
            ["bound Wait with no desk needed"] = r => { r.Recommended = 0; r.WaitRecommended = 0; },
            ["bound wait"] = r => r.Bound = "wait",
            ["bound Guess"] = r => r.Bound = "Guess",
            ["bound empty"] = r => r.Bound = "",
            ["bound null"] = r => r.Bound = null,

            ["at the limit with every desk"] = r => { r.AtLimit = true; r.Recommended = 4; },
            ["at the limit below the desks"] = r => r.AtLimit = true,
            ["shortfall 1 at the limit"] = r => { r.AtLimit = true; r.Recommended = 4; r.Shortfall = 1; },
            ["shortfall a million at the limit"] = r => { r.AtLimit = true; r.Recommended = 4; r.Shortfall = 1_000_000; },
            ["shortfall 0 at the limit"] = r => { r.AtLimit = true; r.Recommended = 4; r.Shortfall = 0; },
            ["shortfall above a million at the limit"] = r => { r.AtLimit = true; r.Recommended = 4; r.Shortfall = 1_000_001; },
            ["shortfall without the limit"] = r => r.Shortfall = 1,

            ["wait 0"] = r => r.WaitRecommended = 0,
            ["wait 120"] = r => r.WaitRecommended = 120,
            ["wait just below 0"] = r => r.WaitRecommended = -0.001,
            ["wait just above 120"] = r => r.WaitRecommended = 120.001,
            ["wait unknown with a recommendation"] = r => r.WaitRecommended = null,
            ["wait without a recommendation"] = r => { NoRecommendation(r, "BacklogUnknown"); r.WaitRecommended = 6; },

            ["wait open 0"] = r => r.WaitOpen = 0,
            ["wait open 120"] = r => r.WaitOpen = 120,
            ["wait open just below 0"] = r => r.WaitOpen = -0.001,
            ["wait open just above 120"] = r => r.WaitOpen = 120.001,
            // ARV-185: the engine writes the wait with the desks open now whenever there is a recommendation and they are known,
            // but when nothing is open while people wait.
            ["wait open unknown"] = r => r.WaitOpen = null,
            ["wait open unknown without open now"] = r => { r.OpenNow = null; r.WaitOpen = null; },
            ["wait open without open now"] = r => r.OpenNow = null,
            ["wait open without a recommendation"] = r => { NoRecommendation(r, "BacklogUnknown"); r.WaitOpen = 8; },
            ["no service open now without a recommendation"] = r => { NoRecommendation(r, "BacklogUnknown"); r.NoServiceOpen = true; r.OpenNow = 0; },
            ["no service open now"] = r => { r.NoServiceOpen = true; r.WaitOpen = null; r.OpenNow = 0; },
            ["no service open now with a wait"] = r => r.NoServiceOpen = true,
            ["no service open now unknown"] = r => { r.NoServiceOpen = true; r.WaitOpen = null; r.OpenNow = null; },

            ["gap Guess"] = r => { NoRecommendation(r, "Guess"); },
            ["gap none"] = r => { NoRecommendation(r, "none"); },
            ["gap Stale"] = r => { NoRecommendation(r, StaffingErrors.Stale); },
            ["gap null"] = r => { NoRecommendation(r, null); },
            ["gap ArrivalsOutOfRange"] = r => { NoRecommendation(r, "ArrivalsOutOfRange"); r.Arrivals = null; },
            ["gap with a recommendation"] = r => r.Gap = "FlightWithoutPassengers",
            ["gap None without a recommendation"] = r => { NoRecommendation(r, "BacklogUnknown"); r.Gap = "None"; },

            ["lane gap NoDesks"] = r => { NoRecommendation(r, "NoDesks"); r.LaneGap = "NoDesks"; r.Desks = 0; },
            ["lane gap NoThroughput"] = r => { NoRecommendation(r, "NoThroughput"); r.LaneGap = "NoThroughput"; r.ThroughputPerDesk = null; r.ThroughputSource = null; },
            ["lane gap NoQueueLength"] = r => { NoRecommendation(r, "NoQueueLength"); r.LaneGap = "NoQueueLength"; r.QueueNow = null; },
            ["lane gap with a recommendation"] = r => { r.LaneGap = "NoQueueLength"; r.QueueNow = null; },
            ["lane gap NoThroughput with a throughput"] = r => { NoRecommendation(r, "NoThroughput"); r.LaneGap = "NoThroughput"; },
            ["lane gap None without desks"] = r => { NoRecommendation(r, "NoDesks"); r.Desks = 0; },
            ["lane gap Guess"] = r => { NoRecommendation(r, "NoDesks"); r.LaneGap = "Guess"; },
            ["lane gap Stale"] = r => { NoRecommendation(r, "NoDesks"); r.LaneGap = StaffingErrors.Stale; },
            ["lane gap FlightWithoutPassengers"] = r => { NoRecommendation(r, "FlightWithoutPassengers"); r.LaneGap = "FlightWithoutPassengers"; },
            ["lane gap null"] = r => { NoRecommendation(r, "NoDesks"); r.LaneGap = null; },

            ["queue 0"] = r => r.QueueNow = 0,
            ["queue a million"] = r => r.QueueNow = 1_000_000,
            ["queue just below 0"] = r => r.QueueNow = -0.5,
            ["queue just above a million"] = r => r.QueueNow = 1_000_001,

            ["open now 0"] = r => r.OpenNow = 0,
            ["open now 1,000"] = r => r.OpenNow = 1_000,
            ["open now -1"] = r => r.OpenNow = -1,
            ["open now 1,001"] = r => r.OpenNow = 1_001,

            ["desks 1,000"] = r => r.Desks = 1_000,
            ["desks 1,001"] = r => r.Desks = 1_001,
            ["desks -1"] = r => { NoRecommendation(r, "NoDesks"); r.LaneGap = "NoDesks"; r.Desks = -1; },

            ["target 1"] = r => r.TargetMinutes = 1,
            ["target 120"] = r => r.TargetMinutes = 120,
            ["target just below 1"] = r => r.TargetMinutes = 0.999,
            ["target just above 120"] = r => r.TargetMinutes = 120.001,

            ["busy cap 0.5"] = r => r.BusyCap = 0.5,
            ["busy cap 1"] = r => r.BusyCap = 1,
            ["busy cap just below 0.5"] = r => r.BusyCap = 0.499,
            ["busy cap just above 1"] = r => r.BusyCap = 1.001,

            ["rate 1,000"] = r => r.ThroughputPerDesk = 1_000,
            ["rate just above 0"] = r => r.ThroughputPerDesk = 1e-9,
            ["rate 0"] = r => r.ThroughputPerDesk = 0,
            ["rate -1"] = r => r.ThroughputPerDesk = -1,
            ["rate just above 1,000"] = r => r.ThroughputPerDesk = 1_000.001,
            ["rate without a source"] = r => r.ThroughputSource = null,
            ["source without a rate"] = r => { NoRecommendation(r, "NoThroughput"); r.LaneGap = "NoThroughput"; r.ThroughputPerDesk = null; },
            ["source Fallback"] = r => r.ThroughputSource = "Fallback",
            ["source measured"] = r => r.ThroughputSource = "measured",
            ["source Guess"] = r => r.ThroughputSource = "Guess"
        };

        // Every double refuses NaN and both infinities.
        var doubles = new Dictionary<string, Action<Row, double>>(StringComparer.Ordinal)
        {
            ["arrivals"] = (r, v) => r.Arrivals = v,
            ["wait"] = (r, v) => r.WaitRecommended = v,
            ["wait open"] = (r, v) => r.WaitOpen = v,
            ["queue"] = (r, v) => r.QueueNow = v,
            ["target"] = (r, v) => r.TargetMinutes = v,
            ["busy cap"] = (r, v) => r.BusyCap = v,
            ["rate"] = (r, v) => r.ThroughputPerDesk = v
        };
        foreach (var (name, set) in doubles)
        {
            foreach (var (label, value) in new[] { ("NaN", double.NaN), ("+Infinity", double.PositiveInfinity), ("-Infinity", double.NegativeInfinity) })
                changes[$"{name} {label}"] = r => set(r, value);
        }

        return changes;
    }

    private static readonly HashSet<string> Accepted = new(StringComparer.Ordinal)
    {
        "as written", "lane 2 letters", "lane 4 letters", "start at the minute", "start 194 minutes on", "minutes 1", "minutes 15", "arrivals 0",
        "arrivals a million", "arrivals unknown without a recommendation", "recommended 0, no desk needed", "recommended 1,000 of 1,000 desks", "bound Busy",
        "bound Both", "at the limit with every desk", "shortfall 1 at the limit", "shortfall a million at the limit", "wait 0", "wait 120", "wait open 0",
        "wait open 120", "wait open unknown without open now", "no service open now", "gap ArrivalsOutOfRange", "lane gap NoDesks", "lane gap NoThroughput",
        "lane gap NoQueueLength", "queue 0", "queue a million", "open now 0", "open now 1,000", "desks 1,000", "target 1", "target 120", "busy cap 0.5",
        "busy cap 1", "rate 1,000", "rate just above 0", "source Fallback"
    };

    public static TheoryData<string, bool> Cases()
    {
        var data = new TheoryData<string, bool>();
        foreach (var name in Changes.Keys)
            data.Add(name, Accepted.Contains(name));
        return data;
    }

    [Fact]
    public void Cases_Should_NameOnlyChangesThatExist_When_Listed()
    {
        Accepted.Should().BeSubsetOf(Changes.Keys);
        Changes.Keys.Should().Contain(["arrivals NaN", "rate -Infinity", "lane vis", "lane VISAS", "minutes 0", "minutes 16", "bound Guess", "gap Guess",
            "lane gap Guess", "source Guess", "gap with a recommendation", "rate without a source", "source without a rate"]);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Plausible_Should_AcceptTheEdgeAndRefuseJustOutsideIt_When_AStoredRowIsRead(string change, bool accepted)
    {
        var row = Valid();
        Changes[change](row);

        StaffingReadBack.Plausible(row).Should().Be(accepted, change);
    }

    public static TheoryData<string> Refused()
    {
        var data = new TheoryData<string>();
        foreach (var name in Changes.Keys.Where(n => !Accepted.Contains(n)))
            data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public void Answer_Should_SayStaleWithNoNumbers_When_OneRowOfTheMinuteIsRefused(string change)
    {
        // The change on one interval in the middle of the minute (index 5, a quarter hour): the whole lane reads Stale.
        var rows = Minute13();
        Changes[change](rows[5]);

        SvcStaffingRecommendations.View(Lane, Key, rows).Should().BeNull(change);
        ShouldBeStale(SvcStaffingRecommendations.Answer(Lane, Key, rows), change);
    }

    [Theory]
    [InlineData("arrivals 0")]
    [InlineData("arrivals a million")]
    [InlineData("arrivals unknown without a recommendation")]
    [InlineData("recommended 0, no desk needed")]
    [InlineData("bound Both")]
    [InlineData("at the limit with every desk")]
    [InlineData("shortfall a million at the limit")]
    [InlineData("wait 120")]
    [InlineData("gap ArrivalsOutOfRange")]
    public void Answer_Should_ShowTheMinute_When_OneIntervalIsChangedWithinItsBounds(string change)
    {
        var rows = Minute13();
        Changes[change](rows[5]);

        var answer = SvcStaffingRecommendations.Answer(Lane, Key, rows);

        answer.Gap.Should().Be("None", change);
        answer.Intervals.Should().HaveCount(13, change);
    }

    [Fact]
    public void Answer_Should_ShowTheMinute_When_ItHoldsTogether()
    {
        var answer = SvcStaffingRecommendations.Answer(Lane, Key, Minute13());

        answer.Should().Match<LaneStaffingViewModel>(l => l.Zone == "A-VIS" && l.ZoneKey == Key && l.Lane == "VIS" && l.MinuteUtc == Minute && l.Gap == "None" &&
            l.Desks == 4 && l.OpenNow == 2 && l.QueueNow == 42 && l.CycleMinutes == 2 && l.ThroughputSource == "Measured" && l.TargetMinutes == 10 && l.BusyCap == 0.85);
        answer.Intervals.Should().HaveCount(13);
        answer.Intervals[0].Should().Match<StaffingIntervalViewModel>(i => i.StartUtc == Minute && i.Minutes == 8 && i.Recommended == 3 && i.Bound == "Wait");
        answer.Intervals[12].StartUtc.Should().Be(new DateTime(2026, 10, 15, 17, 0, 0, DateTimeKind.Utc));
    }

    public static TheoryData<string> BrokenMinutes() =>
    [
        "12 rows", "14 rows", "a duplicated index", "an out-of-order index", "no rows", "a null row", "rows of two minutes", "another zone's row",
        "another lane category than the profile's", "a start off the plan", "the first interval a full quarter hour", "lane values that differ",
        "a minute at the end of time"
    ];

    [Theory]
    [MemberData(nameof(BrokenMinutes))]
    public void Answer_Should_SayStaleWithNoNumbers_When_TheMinuteDoesNotHoldTogether(string broken)
    {
        var rows = Minute13();
        switch (broken)
        {
            case "12 rows":
                rows.RemoveAt(12);
                break;
            case "14 rows":
                var extra = Valid(13, rows[12].StartUtc.AddMinutes(15), 15);
                rows.Add(extra);
                break;
            case "a duplicated index":
                rows[6].IntervalIndex = 5;
                break;
            case "an out-of-order index":
                (rows[5], rows[6]) = (rows[6], rows[5]);
                break;
            case "no rows":
                rows.Clear();
                break;
            case "a null row":
                rows[3] = null;
                break;
            case "rows of two minutes":
                rows[7].MinuteUtc = Minute.AddMinutes(-1);
                break;
            case "another zone's row":
                rows[2].ZoneKey = "DMO/A-CIT";
                break;
            case "another lane category than the profile's":
                rows.ForEach(r => r.Lane = "CIT");
                break;
            case "a start off the plan":
                rows[4].StartUtc = rows[4].StartUtc.AddMinutes(1);
                break;
            case "the first interval a full quarter hour":
                rows[0].Minutes = 15;
                break;
            case "lane values that differ":
                rows[9].QueueNow = 43;
                break;
            case "a minute at the end of time":
                var last = DateTime.SpecifyKind(DateTime.MaxValue.AddMinutes(-10), DateTimeKind.Utc);
                var end = new DateTime(last.Ticks - last.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
                rows.ForEach(r => { r.MinuteUtc = end; r.StartUtc = end; });
                break;
        }

        SvcStaffingRecommendations.View(Lane, Key, rows).Should().BeNull(broken);
        ShouldBeStale(SvcStaffingRecommendations.Answer(Lane, Key, rows), broken);
    }

    [Fact]
    public void Answer_Should_SayStale_When_TheLaneHasNoStoredMinute()
    {
        ShouldBeStale(SvcStaffingRecommendations.Answer(Lane, Key, null), "no rows read");
    }

    private static void ShouldBeStale(LaneStaffingViewModel answer, string because)
    {
        answer.Should().Be(new LaneStaffingViewModel("A-VIS", Key, "VIS", null, null, null, null, null, false, null, null, null, StaffingErrors.Stale, answer.Intervals),
            because);
        answer.Intervals.Should().BeEmpty(because);
    }
}
