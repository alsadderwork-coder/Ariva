using Ariva.Core.Domain.Enums;
using Ariva.Core.Queueing;
using FluentAssertions;

namespace Ariva.UnitTests.Queueing;

/// <summary>
/// ARV-113: per-line minute counts. Every crossing of a line of the zone (entry, exit, count and overflow entry lines)
/// lands on its line and minute as the engine applies it; interval counts are spread as the engine spreads them; a
/// minute is released once the watermark has passed it, so late events inside the lateness allowance revise the open
/// minute only, and events behind the watermark never change a released minute. Snapshots carry the open minute.
/// </summary>
public sealed class LineCountsTests
{
    #region Helpers

    private static readonly DateTime T0 = new(2026, 9, 28, 18, 0, 0, DateTimeKind.Utc);

    private static DateTime At(double minutes) => T0.AddMinutes(minutes);

    private static readonly QueueZoneGeometry Geometry = new(
        "A-VIS",
        new HashSet<string>(StringComparer.Ordinal) { "A-VIS entry" },
        new HashSet<string>(StringComparer.Ordinal) { "A-VIS exit" },
        new HashSet<string>(StringComparer.Ordinal) { "A-OV entry" },
        new HashSet<string>(StringComparer.Ordinal) { "A-OV" },
        new HashSet<string>(StringComparer.Ordinal) { "A-VIS count" });

    private static readonly QueueEngineSettings Thirty = new() { Lateness = TimeSpan.FromSeconds(30) };

    private static QueueCrossing Cross(string line, CrossingDirection direction, double minute, string track = null) => new(line, direction, track, At(minute));

    /// <summary>Offers each event when it happened and advances to <paramref name="until"/>; returns the closed line minutes.</summary>
    private static IReadOnlyList<LineMinute> Play(QueueStateEngine engine, LineCounts counts, double until, params QueueInput[] inputs)
    {
        foreach (var input in inputs)
            engine.Offer(input, input.TimeUtc);
        return counts.Accept(engine.Advance(At(until)));
    }

    #endregion

    [Fact]
    public void Accept_Should_CountEachCrossingOnItsLineAndMinute_When_EntryExitCountAndOverflowEntryLinesAreCrossed()
    {
        var engine = new QueueStateEngine(Geometry, Thirty);
        var counts = new LineCounts();

        var closed = Play(engine, counts, 5,
            Cross("A-OV entry", CrossingDirection.In, 0.2, "S-15/1"),
            Cross("A-VIS entry", CrossingDirection.In, 0.5, "S-15/1"),
            Cross("A-VIS entry", CrossingDirection.In, 0.6),
            Cross("A-VIS count", CrossingDirection.In, 1.1),
            Cross("A-VIS count", CrossingDirection.Out, 1.2),
            Cross("A-VIS entry", CrossingDirection.Out, 1.4),
            Cross("A-VIS exit", CrossingDirection.Out, 2.3, "S-15/1"),
            Cross("A-VIS exit", CrossingDirection.In, 2.4),
            Cross("Elsewhere", CrossingDirection.In, 2.5));

        closed.Should().Equal(
            new LineMinute("A-OV entry", QueueLineRole.OverflowEntry, At(0), 1, 0),
            new LineMinute("A-VIS entry", QueueLineRole.Entry, At(0), 2, 0),
            new LineMinute("A-VIS count", QueueLineRole.Count, At(1), 1, 1),
            new LineMinute("A-VIS entry", QueueLineRole.Entry, At(1), 0, 1),
            new LineMinute("A-VIS exit", QueueLineRole.Exit, At(2), 1, 1));
        closed.Should().OnlyContain(l => l.Source == LineCountSource.Ariva);
        counts.Open.Should().Be(0);
    }

    [Fact]
    public void Advance_Should_LeaveTheQueueAlone_When_ACountLineIsCrossed()
    {
        var engine = new QueueStateEngine(Geometry, Thirty);

        engine.Offer(Cross("A-VIS count", CrossingDirection.In, 0.1), At(0.1));
        engine.Offer(new QueueInterval("A-VIS count", 3, 2, At(0.2), At(0.9)), At(0.9));
        var step = engine.Advance(At(5));

        step.Movements.Should().BeEmpty("a count line is no entry or exit of the queue");
        step.Rejections.UnknownGeometry.Should().Be(0, "a count line is one of the zone's lines");
        step.OpenEntrants.Should().Be(0);
        step.Lines.Should().ContainSingle().Which.Should().Be(new LineMovement("A-VIS count", QueueLineRole.Count, At(0), 4, 2));
    }

    [Fact]
    public void Accept_Should_SpreadIntervalCountsOverTheirMinutes_When_AnIntervalSpansTwoMinutes()
    {
        var engine = new QueueStateEngine(Geometry, Thirty);
        var counts = new LineCounts();

        var closed = Play(engine, counts, 5,
            new QueueInterval("A-VIS entry", 4, 2, At(0), At(2)),
            new QueueInterval("A-VIS exit", 1, 6, At(2), At(3)),
            new QueueInterval("A-OV entry", 2, 0, At(3), At(4)));

        closed.Should().Equal(
            new LineMinute("A-VIS entry", QueueLineRole.Entry, At(0), 2, 1),
            new LineMinute("A-VIS entry", QueueLineRole.Entry, At(1), 2, 1),
            new LineMinute("A-VIS exit", QueueLineRole.Exit, At(2), 1, 6),
            new LineMinute("A-OV entry", QueueLineRole.OverflowEntry, At(3), 2, 0));
    }

    [Fact]
    public void Accept_Should_ReviseTheOpenMinuteOnly_When_LateEventsArriveInsideTheWatermark()
    {
        var engine = new QueueStateEngine(Geometry, Thirty);
        var counts = new LineCounts();

        // 18:00:10 arrives on time; at 18:00:50 the watermark is 18:00:20 and minute 18:00 is still open.
        engine.Offer(Cross("A-VIS entry", CrossingDirection.In, 10 / 60.0), At(10 / 60.0));
        counts.Accept(engine.Advance(At(50 / 60.0))).Should().BeEmpty("the minute is open until the watermark passes it");
        counts.Open.Should().Be(1);

        // 18:00:25 arrives 30 seconds late, still ahead of the watermark: it revises the open minute.
        engine.Offer(Cross("A-VIS entry", CrossingDirection.In, 25 / 60.0), At(55 / 60.0));
        var closed = counts.Accept(engine.Advance(At(1 + 40 / 60.0)));
        closed.Should().Equal(new LineMinute("A-VIS entry", QueueLineRole.Entry, At(0), 2, 0));

        // 18:00:40 arrives after the watermark passed 18:01:10: late, not applied, and minute 18:00 is not released again.
        engine.Offer(Cross("A-VIS entry", CrossingDirection.In, 40 / 60.0), At(1 + 45 / 60.0));
        engine.Offer(Cross("A-VIS entry", CrossingDirection.In, 1 + 30 / 60.0), At(1 + 45 / 60.0));
        var step = engine.Advance(At(4));
        var later = counts.Accept(step);

        step.Rejections.Late.Should().Be(1);
        later.Should().Equal(new LineMinute("A-VIS entry", QueueLineRole.Entry, At(1), 1, 0));
    }

    [Fact]
    public void Restore_Should_GiveTheSameLineMinutes_When_TheZoneRestartsMidMinute()
    {
        QueueInput[] events =
        [
            Cross("A-VIS entry", CrossingDirection.In, 0.1), Cross("A-VIS entry", CrossingDirection.In, 0.7),
            Cross("A-VIS exit", CrossingDirection.Out, 1.2), Cross("A-VIS count", CrossingDirection.In, 1.25),
            Cross("A-VIS entry", CrossingDirection.In, 1.3), Cross("A-VIS exit", CrossingDirection.Out, 2.6)
        ];
        var straight = new QueueStateEngine(Geometry, Thirty);
        var straightCounts = new LineCounts();
        var expected = new List<LineMinute>();
        foreach (var e in events)
        {
            straight.Offer(e, e.TimeUtc);
            expected.AddRange(straightCounts.Accept(straight.Advance(e.TimeUtc)));
        }

        expected.AddRange(straightCounts.Accept(straight.Advance(At(10))));

        var first = new QueueStateEngine(Geometry, Thirty);
        var firstCounts = new LineCounts();
        var got = new List<LineMinute>();
        foreach (var e in events.Take(4))
        {
            first.Offer(e, e.TimeUtc);
            got.AddRange(firstCounts.Accept(first.Advance(e.TimeUtc)));
        }

        firstCounts.Open.Should().BeGreaterThan(0, "the restart falls inside an open minute");
        var engine = QueueStateEngine.Restore(Geometry, Thirty, first.Capture());
        var counts = LineCounts.Restore(Geometry, firstCounts.Capture());
        foreach (var e in events.Skip(4))
        {
            engine.Offer(e, e.TimeUtc);
            got.AddRange(counts.Accept(engine.Advance(e.TimeUtc)));
        }

        got.AddRange(counts.Accept(engine.Advance(At(10))));
        got.Should().Equal(expected);
        expected.Should().NotBeEmpty();
    }

    [Fact]
    public void Restore_Should_Refuse_When_TheSnapshotHoldsHostileLineMinutes()
    {
        var good = new LineMinuteState("A-VIS entry", QueueLineRole.Entry, At(0), 3, 0);
        IReadOnlyList<LineMinuteState>[] hostile =
        [
            [good with { LineName = "Elsewhere" }],
            [good with { Role = QueueLineRole.Exit }],
            [good with { LineName = null }],
            [good with { In = -1 }],
            [good with { Out = -1 }],
            [good with { MinuteUtc = At(0.5) }],
            [good with { MinuteUtc = DateTime.MinValue }],
            [good, good],
            [null],
            [.. Enumerable.Range(0, LineCounts.MaxOpenLineMinutes + 1).Select(i => good with { MinuteUtc = At(i) })]
        ];

        LineCounts.Restore(Geometry, [good]).Capture().Should().Equal(good);
        foreach (var state in hostile)
        {
            var restore = () => LineCounts.Restore(Geometry, state);
            restore.Should().Throw<InvalidDataException>();
        }
    }

    [Fact]
    public void Restore_Should_Refuse_When_TheEngineSnapshotCountsALineThatIsNotTheZones()
    {
        var engine = new QueueStateEngine(Geometry, Thirty);
        var state = engine.Capture() with { Lines = [new LineMovement("Elsewhere", QueueLineRole.Entry, At(0), 1, 0)] };

        var restore = () => QueueStateEngine.Restore(Geometry, Thirty, state);

        restore.Should().Throw<InvalidDataException>();
    }

    public static TheoryData<string, LineMovement[]> HostileEngineLines()
    {
        var good = new LineMovement("A-VIS entry", QueueLineRole.Entry, At(0), 1, 0);
        return new()
        {
            { "unaligned minute", [good with { MinuteUtc = At(0.5) }] },
            { "DateTime.MinValue", [good with { MinuteUtc = DateTime.MinValue }] },
            { "DateTime.MaxValue", [good with { MinuteUtc = DateTime.MaxValue }] },
            { "negative In", [good with { In = -1 }] },
            { "negative Out", [good with { Out = -1 }] },
            { "wrong role", [good with { Role = QueueLineRole.Exit }] },
            { "no line name", [good with { LineName = null }] },
            { "twice", [good, good] }
        };
    }

    [Theory]
    [MemberData(nameof(HostileEngineLines))]
    public void Restore_Should_Refuse_When_TheEngineSnapshotHoldsHostileLineMovements(string because, LineMovement[] lines)
    {
        var engine = new QueueStateEngine(Geometry, Thirty);
        var good = engine.Capture() with { Lines = [new LineMovement("A-VIS entry", QueueLineRole.Entry, At(0), 1, 0)] };
        QueueStateEngine.Restore(Geometry, Thirty, good).Capture().Lines.Should().Equal(good.Lines);

        var restore = () => QueueStateEngine.Restore(Geometry, Thirty, good with { Lines = lines });

        restore.Should().Throw<InvalidDataException>(because);
    }

    /// <summary>A step at <paramref name="watermark"/> carrying <paramref name="lines"/> (LineCounts reads nothing else).</summary>
    private static QueueStep Step(DateTime watermark, params LineMovement[] lines) =>
        new QueueStateEngine(Geometry, Thirty).Advance(T0) with { WatermarkUtc = watermark, Lines = lines };

    [Fact]
    public void Accept_Should_StayWithinTheBoundAndAddEveryPart_When_MoreLineMinutesThanTheBoundAreOpen()
    {
        const int beyond = 5;
        var counts = new LineCounts();
        var ahead = Enumerable.Range(1, LineCounts.MaxOpenLineMinutes + beyond)
            .Select(i => new LineMovement("A-VIS entry", QueueLineRole.Entry, At(i), 1, 0)).ToArray();

        // More open minutes than the bound: the earliest are released early, as parts that add to a written row.
        var first = counts.Accept(Step(At(0), ahead));
        counts.Open.Should().Be(LineCounts.MaxOpenLineMinutes);
        first.Should().Equal(Enumerable.Range(1, beyond)
            .Select(i => new LineMinute("A-VIS entry", QueueLineRole.Entry, At(i), 1, 0) { Additive = true }));
        counts.ReleasedEarly.Should().Be(beyond);
        counts.ReleasedEarlyThroughUtc.Should().Be(At(beyond));

        // A restart keeps the mark, so the minute released early still adds after it.
        var restored = LineCounts.Restore(Geometry, counts.Capture(), counts.ReleasedEarlyThroughUtc);

        // The watermark has not passed minute 1: a later step adds to it, and the new part adds to the written one.
        foreach (var c in new[] { counts, restored })
        {
            var second = c.Accept(Step(At(0), new LineMovement("A-VIS entry", QueueLineRole.Entry, At(1), 2, 1)));
            c.Open.Should().BeLessThanOrEqualTo(LineCounts.MaxOpenLineMinutes);
            second.Should().Equal(new LineMinute("A-VIS entry", QueueLineRole.Entry, At(1), 2, 1) { Additive = true });
            first.Concat(second).Where(l => l.MinuteUtc == At(1)).Sum(l => l.In).Should().Be(3, "no count of minute 1 is lost");

            // Past the mark, minutes are whole again (they replace), and once the watermark passes the mark it is cleared.
            var third = c.Accept(Step(At(beyond + 3)));
            third.Should().Equal(
                new LineMinute("A-VIS entry", QueueLineRole.Entry, At(beyond + 1), 1, 0),
                new LineMinute("A-VIS entry", QueueLineRole.Entry, At(beyond + 2), 1, 0));
            third.Should().OnlyContain(l => !l.Additive);
            c.ReleasedEarlyThroughUtc.Should().BeNull();
        }
    }

    [Fact]
    public void Accept_Should_ReleaseTheRestOfAnEarlyMinuteAsParts_When_OtherLinesOfThatMinuteCloseLater()
    {
        var counts = LineCounts.Restore(Geometry,
            [new LineMinuteState("A-VIS exit", QueueLineRole.Exit, At(3), 4, 0)], releasedEarlyThroughUtc: At(3));

        var closed = counts.Accept(Step(At(4)));

        closed.Should().Equal(new LineMinute("A-VIS exit", QueueLineRole.Exit, At(3), 4, 0) { Additive = true });
        counts.ReleasedEarlyThroughUtc.Should().BeNull();
    }

    [Fact]
    public void Restore_Should_Refuse_When_TheEarlyReleaseMarkIsNotAMinute()
    {
        foreach (var mark in new[] { At(0.5), DateTime.MinValue, DateTime.MaxValue })
        {
            var restore = () => LineCounts.Restore(Geometry, [], mark);
            restore.Should().Throw<InvalidDataException>();
        }

        LineCounts.Restore(Geometry, [], At(2)).ReleasedEarlyThroughUtc.Should().Be(At(2));
    }
}
