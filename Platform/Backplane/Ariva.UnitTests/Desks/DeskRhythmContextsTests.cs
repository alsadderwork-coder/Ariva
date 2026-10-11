using Ariva.Core.Desks;
using FluentAssertions;

namespace Ariva.UnitTests.Desks;

/// <summary>
/// ARV-149: the lane context the desk feed gives each desk without staff or service zones once a minute (F10 rows 5a to
/// 5f): s from the AMAN intervals of every desk serving its queue zones (ARV-117d, each interval once, refused ones left out
/// and counted, CWE-501), and Q from each zone's latest live minute of the last 5 minutes.
/// </summary>
public sealed class DeskRhythmContextsTests
{
    private static readonly DateTime At = new(2026, 10, 10, 18, 0, 0, DateTimeKind.Utc);

    private static LaneIntervalSample Interval(string zone, string id, int transactions, int documents, double service) =>
        new(zone, id, new DeskIntervalSample(transactions, documents, service, service * 1.5, service));

    private static LaneQueueSample Queue(string zone, int minutesAgo, int length, bool degraded = false) => new(zone, At.AddMinutes(-minutesAgo), length, degraded);

    private static DeskLaneContext Single(IEnumerable<(string, string)> served, IEnumerable<LaneQueueSample> queues, IEnumerable<LaneIntervalSample> intervals = null) =>
        DeskRhythm.Contexts(served, queues, intervals ?? [], At).Contexts.Single();

    [Fact]
    public void Contexts_Should_GiveTheLaneCycleTimePerPersonAndTheQueue_When_TheDeskServesOneZone()
    {
        // ARV-117d's families case: 4 approaches of 5 people at 90 s, and one traveller at 120 s: (4 x 90 + 120) / 6 = 80 s a person.
        var context = Single([("DMO/IMM/AR-09", "A-VIS")], [Queue("A-VIS", 2, 7)],
            [Interval("A-VIS", "i1", 4, 5, 90), Interval("A-VIS", "i2", 1, 1, 120), Interval("A-CIT", "i3", 1, 1, 30)]);

        context.Should().Be(new DeskLaneContext("DMO/IMM/AR-09", At, 80, 7, false));
    }

    // case, the desk's zones' latest minutes (zone, minutes ago, length, degraded; null: no minute), expected Q, expected degraded
    public static TheoryData<string, (int Ago, int Length, bool Degraded)?, (int Ago, int Length, bool Degraded)?, int?, bool> Queues => new()
    {
        { "both zones known: the sum", (1, 3, false), (2, 4, false), 7, false },
        { "people in one zone, the other not known: people wait", (1, 3, false), null, 3, false },
        { "people only in a degraded zone: degraded", (1, 3, true), (1, 0, false), 3, true },
        { "people in a zone that is not degraded: not degraded", (1, 3, false), (1, 2, true), 5, false },
        { "every zone known and empty: 0", (1, 0, false), (1, 0, false), 0, false },
        { "every zone empty, one degraded: 0, degraded", (1, 0, false), (1, 0, true), 0, true },
        { "one zone empty, the other not known: not known", (1, 0, false), null, null, false },
        { "a minute older than 5 minutes is not known", (1, 0, false), (6, 4, false), null, false },
        { "a minute exactly 5 minutes old is fresh", (1, 0, false), (5, 4, false), 4, false },
        { "no zone known: not known", null, null, null, false }
    };

    [Theory]
    [MemberData(nameof(Queues))]
    public void Contexts_Should_CountThePeopleWaiting_When_TheDeskServesTwoZones(string because, (int Ago, int Length, bool Degraded)? visitors,
        (int Ago, int Length, bool Degraded)? residents, int? waiting, bool degraded)
    {
        var queues = new List<LaneQueueSample>();
        if (visitors.HasValue)
            queues.Add(Queue("A-VIS", visitors.Value.Ago, visitors.Value.Length, visitors.Value.Degraded));
        if (residents.HasValue)
            queues.Add(Queue("A-RES", residents.Value.Ago, residents.Value.Length, residents.Value.Degraded));

        var context = Single([("DMO/IMM/AR-05", "A-VIS"), ("DMO/IMM/AR-05", "A-RES")], queues);

        (context.Waiting, context.Degraded).Should().Be((waiting, degraded), because);
    }

    [Fact]
    public void Contexts_Should_TakeEachZonesLatestMinute_When_SeveralAreFresh()
    {
        Single([("D1", "Z")], [Queue("Z", 1, 0), Queue("Z", 3, 9), Queue("Z", 0, 2), Queue("Z", -1, 50)]).Waiting
            .Should().Be(2, "the latest minute not after the moment; a minute after it is left out");
    }

    [Fact]
    public void Contexts_Should_LeaveOutAndCountWhatIsOutOfBounds_When_ReadBack()
    {
        // CWE-501: a zone whose latest minute is out of bounds is not known (its earlier minute does not stand in); an interval
        // outside contract V1 is left out of s.
        var result = DeskRhythm.Contexts([("D1", "Z"), ("D2", "Y"), ("D3", "X")],
            [Queue("Z", 1, -1), Queue("Z", 2, 3), Queue("Y", 1, DeskLaneContext.MaxWaiting + 1), Queue("X", 2, -5), Queue("X", 1, 2)],
            [Interval("Z", "ok", 1, 1, 60), Interval("Z", "nan", 1, 1, double.NaN), Interval("Z", "more", 3, 2, 60), Interval("Y", "zero", 1, 1, 0)], At);

        result.RefusedQueues.Should().Be(3);
        result.RefusedIntervals.Should().Be(3);
        result.Contexts.Should().Equal(new DeskLaneContext("D1", At, 60, null, false), new DeskLaneContext("D2", At, null, null, false),
            new DeskLaneContext("D3", At, null, 2, false));
        DeskRhythm.Contexts([("D1", "Z")], [Queue("Z", 1, 4), Queue("Z", 1, -1)], [], At).Contexts.Single().Waiting
            .Should().BeNull("of two lengths for one minute, the one out of bounds wins");
    }

    [Fact]
    public void Contexts_Should_CountAnIntervalOnce_When_ItIsReadUnderTwoZones()
    {
        // A desk of two lanes is matched to both queue zones, so its intervals are read once per zone: each counts once.
        var result = DeskRhythm.Contexts([("D1", "Z"), ("D1", "Y")], [],
            [Interval("Z", "a", 1, 1, 60), Interval("Y", "a", 1, 1, 60), Interval("Y", "b", 1, 1, 120), Interval("Y", "bad", 2, 1, 60), Interval("Z", "bad", 2, 1, 60)], At);

        result.Contexts.Single().ServiceSeconds.Should().Be(90);
        result.RefusedIntervals.Should().Be(1);
    }

    [Fact]
    public void Contexts_Should_GiveNoServiceTime_When_TheIntervalsCarryNoDocuments()
    {
        Single([("D1", "Z")], [Queue("Z", 1, 0)], [Interval("Z", "idle", 0, 0, 0)]).Should().Be(new DeskLaneContext("D1", At, null, 0));
        Single([("D1", "Z")], [Queue("Z", 1, 0)]).ServiceSeconds.Should().BeNull("no interval read");
    }

    [Fact]
    public void Contexts_Should_GiveOneContextPerDeskInKeyOrder_When_DesksShareZones()
    {
        var result = DeskRhythm.Contexts([("D2", "Z"), ("D1", "Z"), ("D2", "Z"), (new string('D', 65), "Z"), ("", "Z"), (null, "Z"), ("D3", null), ("D3", "")],
            [Queue("Z", 1, 1)], [], At);

        result.Contexts.Select(c => c.DeskCode).Should().Equal("D1", "D2");
        result.Contexts.Should().OnlyContain(c => c.TimeUtc == At && c.Waiting == 1);
    }

    [Fact]
    public void Contexts_Should_BeEmpty_When_NoDeskServesAZone()
    {
        var result = DeskRhythm.Contexts([], [Queue("Z", 1, 1)], [Interval("Z", "a", 1, 1, 60)], At);

        result.Contexts.Should().BeEmpty("a desk that serves no queue zone keeps rows 5 and 7");
        (result.RefusedIntervals, result.RefusedQueues).Should().Be((0, 0));
    }
}
