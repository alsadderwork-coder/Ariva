using System.Text.Json;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Queueing;
using Ariva.Core.Sensing;
using Ariva.Infra.Messaging;
using FluentAssertions;

namespace Ariva.UnitTests.Queueing;

/// <summary>
/// ARV-173: interval readings that end at the same instant (the entry and exit lines of one T1 push, or two pushes, or two
/// devices) are each spread over their own interval, after the last event processed before that instant and the
/// watermark, so each lands in the minutes it covers whatever their order. Before, the second reading of an instant was
/// spread over nothing and all its people landed at the instant itself: the next minute. The instant's entry-line readings
/// are applied before its other readings by the zone's geometry, and a counted exit whose FIFO partner entered after it
/// is unmatched, so the pairing is the one time order gives, whatever the lines are called.
/// </summary>
public sealed class IntervalInstantTests
{
    private static readonly DateTime T0 = new(2026, 10, 13, 17, 0, 0, DateTimeKind.Utc);

    private static DateTime At(double minutes) => T0.AddMinutes(minutes);

    #region One push, two pushes, both orders (the zone, as the stream runs it)

    private const string Zone = "Q-T1";
    private const string Entry = Zone + " entry";
    private const string Exit = Zone + " exit";
    private const int Pushes = 12;
    private static readonly QueueZoneGeometry ZoneGeometry = new(Zone, new HashSet<string> { Entry }, new HashSet<string> { Exit }, new HashSet<string>(), new HashSet<string>());

    private static IntervalCountBatch Batch(int m, params (string Line, int In, int Out)[] readings)
    {
        var from = At(m);
        var to = At(m + 1);
        return new IntervalCountBatch
        {
            DeviceId = Guid.Parse("0199e0a0-0000-7000-8000-000000000081"),
            DeviceCode = "S-81",
            SiteCode = "DMO",
            QueueZoneName = Zone,
            Dialect = "canonical",
            Commissioned = true,
            ReceivedUtc = to.AddSeconds(1),
            OccurredOn = to.AddSeconds(1),
            Clock = new ClockReading(0, true, ClockState.Ok),
            Intervals = [.. readings.Select(r => new Sensed<IntervalCount>(new IntervalCount(r.Line, r.In, r.Out, from, to), to, SensedFlags.None))]
        };
    }

    /// <summary>How a T1 device's minute of 5 in and 3 out reaches the zone.</summary>
    public static TheoryData<string> Shapes =>
    [
        "one push, entry line first", "one push, exit line first", "two pushes, entry first", "two pushes, exit first"
    ];

    private static IEnumerable<SensingBatch> Minute(string shape, int m) => shape switch
    {
        "one push, entry line first" => [Batch(m, (Entry, 5, 0), (Exit, 0, 3))],
        "one push, exit line first" => [Batch(m, (Exit, 0, 3), (Entry, 5, 0))],
        "two pushes, entry first" => [Batch(m, (Entry, 5, 0)), Batch(m, (Exit, 0, 3))],
        _ => [Batch(m, (Exit, 0, 3)), Batch(m, (Entry, 5, 0))]
    };

    private static List<ZoneOutputs> Run(string shape)
    {
        var zone = new ZoneProcessor("DMO/" + Zone, ZoneGeometry, 3);
        var outputs = new List<ZoneOutputs>();
        for (var m = 0; m < Pushes; m++)
        {
            foreach (var batch in Minute(shape, m))
                zone.Offer(batch, DateTime.MaxValue);
        }

        zone.Tick(T0.AddHours(3));
        outputs.Add(zone.Drain());
        return outputs;
    }

    /// <summary>The line minutes by role and the zone's final minutes with their waits: what does not depend on the shape.</summary>
    private static string Counted(List<ZoneOutputs> outputs) => JsonSerializer.Serialize(new
    {
        Lines = outputs.SelectMany(o => o.Lines).OrderBy(l => l.Role).ThenBy(l => l.MinuteUtc).Select(l => new { l.Role, l.MinuteUtc, l.In, l.Out }).ToList(),
        Minutes = outputs.SelectMany(o => o.Minutes).Where(m => m.Status == BinStatus.Final).OrderBy(m => m.StartUtc).Select(m => new { m.StartUtc, m.Entries, m.Exits, m.Waits }).ToList()
    }, EventCatalog.Json);

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Zone_Should_CountEachLineInTheMinuteItsReadingCovers_When_TheReadingsOfAPushEndTogether(string shape)
    {
        var outputs = Run(shape);

        var lines = outputs.SelectMany(o => o.Lines).ToList();
        var minutes = outputs.SelectMany(o => o.Minutes).Where(m => m.Status == BinStatus.Final).ToList();
        for (var m = 0; m < Pushes; m++)
        {
            lines.Where(l => l.Role == QueueLineRole.Entry && l.MinuteUtc == At(m)).Sum(l => l.In).Should().Be(5, "{0}: minute {1}'s entries", shape, m);
            lines.Where(l => l.Role == QueueLineRole.Exit && l.MinuteUtc == At(m)).Sum(l => l.Out).Should().Be(3, "{0}: minute {1}'s exits, in their own minute", shape, m);
            minutes.Last(x => x.StartUtc == At(m)).Should().Match<MinuteResult>(x => x.Entries == 5 && x.Exits == 3, "{0}: the zone's minute {1}", shape, m);
        }

        lines.Where(l => l.MinuteUtc >= At(Pushes)).Should().BeEmpty("nothing reaches the minute after the last push ({0})", shape);
        minutes.Where(x => x.StartUtc >= At(Pushes)).Should().OnlyContain(x => x.Entries == 0 && x.Exits == 0);
        (lines.Sum(l => l.In), lines.Sum(l => l.Out)).Should().Be((5L * Pushes, 3L * Pushes), "the totals are the readings' ({0})", shape);
        Counted(outputs).Should().Be(Counted(Run("one push, entry line first")), "the shape never changes a count or a wait ({0})", shape);
    }

    #endregion

    #region Spread positions (the engine)

    private static readonly QueueZoneGeometry Geometry = new("A-VIS", new HashSet<string>(StringComparer.Ordinal) { "A-VIS entry", "A-VIS side" },
        new HashSet<string>(StringComparer.Ordinal) { "A-VIS exit" }, new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));

    private static QueueStateEngine Engine(QueueZoneGeometry geometry = null, int maxStepRecords = 100_000) =>
        new(geometry ?? Geometry, new QueueEngineSettings { Lateness = TimeSpan.Zero, MaxStepRecords = maxStepRecords });

    private static List<QueueStep> Play(QueueStateEngine engine, IEnumerable<QueueInput> inputs, double until = 30)
    {
        foreach (var input in inputs)
            engine.Offer(input, input.TimeUtc);
        var steps = new List<QueueStep> { engine.Advance(At(until)) };
        while (steps[^1].More)
            steps.Add(engine.Advance(At(until)));
        return steps;
    }

    private static Dictionary<(QueueLineRole Role, int Minute), (long In, long Out)> LineMinutes(IEnumerable<QueueStep> steps) =>
        steps.SelectMany(s => s.Lines).GroupBy(l => (l.Role, (int)(l.MinuteUtc - T0).TotalMinutes))
            .ToDictionary(g => g.Key, g => (g.Sum(l => l.In), g.Sum(l => l.Out)));

    private static Dictionary<int, (long Entries, long Exits)> ZoneMinutes(IEnumerable<QueueStep> steps) =>
        steps.SelectMany(s => s.Movements).GroupBy(m => (int)(m.MinuteUtc - T0).TotalMinutes).ToDictionary(g => g.Key, g => (g.Sum(m => m.Entries), g.Sum(m => m.Exits)));

    public static TheoryData<string, string, string> Instants => new()
    {
        // Each case: the readings and crossings, then the expected entry-line and exit-line counts and the zone's entries and
        // exits per minute ("minute:entry in/exit out/zone entries/zone exits"; minutes not listed have none).
        { "entry and exit of one push ending at 17:03, entry offered first", "E 2-3 5 0; X 2-3 0 3", "2:5/3/5/3" },
        { "the same, exit offered first", "X 2-3 0 3; E 2-3 5 0", "2:5/3/5/3" },
        { "a reading across a minute boundary with another of its instant", "E 2.5-3.5 6 0; X 2.5-3.5 0 2", "2:3/1/3/1 3:3/1/3/1" },
        { "another device's tracked entry inside the minute, before the instant", "I 2.75 S-90/1; E 2-3 5 0; X 2-3 0 3", "2:6/3/6/3" },
        { "two entry lines of five minutes ending together", "E 0-5 5 0; S 0-5 5 0", "0:2/0/2/0 1:2/0/2/0 2:2/0/2/0 3:2/0/2/0 4:2/0/2/0" },
        { "the same, the other line offered first", "S 0-5 5 0; E 0-5 5 0", "0:2/0/2/0 1:2/0/2/0 2:2/0/2/0 3:2/0/2/0 4:2/0/2/0" }
    };

    private static List<QueueInput> Inputs(string script) =>
    [
        .. script.Split(';', StringSplitOptions.TrimEntries).Select(part =>
        {
            var f = part.Split(' ');
            if (f[0] == "I")
                return (QueueInput)new QueueCrossing("A-VIS entry", CrossingDirection.In, f[2], At(double.Parse(f[1], System.Globalization.CultureInfo.InvariantCulture)));
            var span = f[1].Split('-').Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var line = f[0] switch { "E" => "A-VIS entry", "S" => "A-VIS side", _ => "A-VIS exit" };
            return new QueueInterval(line, int.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture), int.Parse(f[3], System.Globalization.CultureInfo.InvariantCulture), At(span[0]), At(span[1]));
        })
    ];

    [Theory]
    [MemberData(nameof(Instants))]
    public void Interval_Should_SpreadEachReadingOverItsOwnInterval_When_ReadingsEndAtTheSameInstant(string because, string script, string expected)
    {
        var steps = Play(Engine(), Inputs(script));

        var lines = LineMinutes(steps);
        var zone = ZoneMinutes(steps);
        var want = expected.Split(' ').Select(e => e.Split(':')).ToDictionary(e => int.Parse(e[0], System.Globalization.CultureInfo.InvariantCulture),
            e => e[1].Split('/').Select(x => long.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray());
        foreach (var m in Enumerable.Range(0, 10))
        {
            var w = want.GetValueOrDefault(m) ?? [0, 0, 0, 0];
            (lines.GetValueOrDefault((QueueLineRole.Entry, m)).In, lines.GetValueOrDefault((QueueLineRole.Exit, m)).Out).Should().Be((w[0], w[1]), "{0}: lines of minute {1}", because, m);
            zone.GetValueOrDefault(m).Should().Be((w[2], w[3]), "{0}: the zone's minute {1} (its entries counted where their reading puts them)", because, m);
        }

        steps.Sum(s => s.Rejections.NegativeWaits).Should().Be(0, because);
        steps.SelectMany(s => s.Waits).Should().NotContain(x => x.ExitUtc < x.EntryUtc, because);
    }

    #endregion

    #region Pairing in time order

    /// <summary>
    /// The pairs time order gives (the reference model): every entry and exit at its spread point, entries first at equal
    /// times; an exit pairs with the earliest person waiting, or with nobody.
    /// </summary>
    private static (List<(DateTime Entry, DateTime Exit)> Pairs, int Unmatched) InTimeOrder(IEnumerable<DateTime> held, int entries, int exits, DateTime from, DateTime to)
    {
        DateTime Spread(int k, int n) => from + (to - from) * ((k + 0.5) / n);
        var events = held.Select(t => (Time: t, Entry: true))
            .Concat(Enumerable.Range(0, entries).Select(k => (Time: Spread(k, entries), Entry: true)))
            .Concat(Enumerable.Range(0, exits).Select(k => (Time: Spread(k, exits), Entry: false)))
            .OrderBy(e => e.Time).ThenBy(e => e.Entry ? 0 : 1).ToList();
        var waiting = new Queue<DateTime>();
        var pairs = new List<(DateTime, DateTime)>();
        var unmatched = 0;
        foreach (var (time, entry) in events)
        {
            if (entry)
                waiting.Enqueue(time);
            else if (waiting.Count > 0)
                pairs.Add((waiting.Dequeue(), time));
            else
                unmatched++;
        }

        return (pairs, unmatched);
    }

    public static TheoryData<int, int, int, bool, bool> Pairings => new()
    {
        // People waiting before, entries and exits counted in the minute, the exit reading offered first, the exit line named so
        // that it sorts before the entry line.
        { 0, 2, 3, false, false },
        { 0, 2, 3, true, false },
        { 0, 2, 3, false, true },
        { 0, 1, 1, false, false },
        { 0, 3, 1, true, true },
        { 0, 0, 2, false, false },
        { 1, 2, 3, true, false },
        { 2, 1, 4, false, true },
        { 0, 5, 5, true, true },
        { 3, 0, 3, false, false }
    };

    [Theory]
    [MemberData(nameof(Pairings))]
    public void Exit_Should_PairAsTimeOrderDoes_When_EntryAndExitReadingsEndTogether(int held, int entries, int exits, bool exitFirst, bool exitNamedFirst)
    {
        // "A-VIS doors" sorts before "A-VIS gate": with the role rank the entry line's reading still goes first (ARV-173).
        var entryLine = exitNamedFirst ? "A-VIS gate" : "A-VIS entry";
        var exitLine = exitNamedFirst ? "A-VIS doors" : "A-VIS exit";
        var geometry = new QueueZoneGeometry("A-VIS", new HashSet<string>(StringComparer.Ordinal) { entryLine }, new HashSet<string>(StringComparer.Ordinal) { exitLine },
            new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
        var waiting = Enumerable.Range(0, held).Select(k => At(0.5 + (k * 0.01))).ToList();
        var inputs = waiting.Select(t => (QueueInput)new QueueCrossing(entryLine, CrossingDirection.In, null, t)).ToList();
        QueueInput inReading = new QueueInterval(entryLine, entries, 0, At(1), At(2));
        QueueInput outReading = new QueueInterval(exitLine, 0, exits, At(1), At(2));
        inputs.AddRange(exitFirst ? [outReading, inReading] : [inReading, outReading]);

        var steps = Play(Engine(geometry), inputs);

        var (pairs, unmatched) = InTimeOrder(waiting, entries, exits, At(1), At(2));
        steps.SelectMany(s => s.Waits).Select(w => (w.EntryUtc, w.ExitUtc)).Should().BeEquivalentTo(pairs, "the pairs time order gives");
        steps.Sum(s => s.Rejections.UnmatchedExits).Should().Be(unmatched);
        steps.Sum(s => s.Rejections.NegativeWaits).Should().Be(0, "an exit before its partner entered is unmatched, never a negative wait");
        steps.SelectMany(s => s.Resolutions).Should().NotContain(r => r.Outcome == EntrantOutcome.Rejected);
    }

    #endregion

    #region A crafted snapshot

    [Fact]
    public void Restore_Should_ApplyTheEntryReadingFirstAndCountTheOthersAsUnknown_When_ASnapshotHoldsReadingsWithANullOrUnknownLine()
    {
        // From the ARV-173 security review (CWE-501: a snapshot is data from storage): the role rank reads the zone's geometry
        // for every buffered interval reading, also when a restore rebuilds the buffer. A crafted snapshot holding readings with
        // a null line and an unknown line, at the instant of an entry-line reading, restores without an exception (the restore
        // checks do not refuse a null line name: QueueInputState.ToInput takes it as it comes, and RoleOf answers Unknown), sorts
        // the entry reading first and counts the other two as unknown geometry, exactly as the live engine does.
        QueueInterval entry = new("A-VIS entry", 2, 0, At(1), At(2)), nameless = new(null, 1, 0, At(1), At(2)), stray = new("A-VIS nowhere", 1, 1, At(1), At(2));
        var settings = new QueueEngineSettings { Lateness = TimeSpan.Zero };
        var live = new QueueStateEngine(Geometry, settings);
        foreach (var input in new[] { stray, nameless, entry })
            live.Offer(input, input.TimeUtc);

        var holding = new QueueStateEngine(Geometry, settings);
        holding.Offer(entry, entry.TimeUtc);
        var state = holding.Capture();
        var crafted = JsonSerializer.Deserialize<QueueEngineState>(JsonSerializer.Serialize(state with
        {
            Sequence = state.Sequence + 2,
            Buffer =
            [
                new QueueBufferedState(new QueueInputState("interval", At(2), false, "A-VIS nowhere", FromUtc: At(1), In: 1, Out: 1), false, state.Sequence + 1),
                new QueueBufferedState(new QueueInputState("interval", At(2), false, null, FromUtc: At(1), In: 1), false, state.Sequence),
                .. state.Buffer
            ]
        }, EventCatalog.Json), EventCatalog.Json)!;
        crafted.Buffer.Should().Contain(b => b.Input.Name == null, "the null line survives the JSON");

        QueueStateEngine restored = null;
        var restore = () => restored = QueueStateEngine.Restore(Geometry, settings, crafted);

        restore.Should().NotThrow();
        restored.Capture().Buffer.Select(b => b.Input.Name).Should().Equal(["A-VIS entry", null, "A-VIS nowhere"], "the entry line's reading first, then by content");
        live.Capture().Buffer.Select(b => b.Input.Name).Should().Equal(restored.Capture().Buffer.Select(b => b.Input.Name));
        var step = restored.Advance(At(30));
        var liveStep = live.Advance(At(30));
        step.Rejections.UnknownGeometry.Should().Be(2, "the null and the unknown line are not the zone's");
        liveStep.Rejections.UnknownGeometry.Should().Be(2);
        step.Movements.Should().ContainSingle().Which.Should().Match<MovementCount>(m => m.MinuteUtc == At(1) && m.Entries == 2 && m.Exits == 0);
        step.Lines.Should().ContainSingle().Which.Should().Match<LineMovement>(l => l.LineName == "A-VIS entry" && l.In == 2);
        static string Of(QueueStep s) => JsonSerializer.Serialize(new { s.WatermarkUtc, s.Movements, s.Lines, s.Waits, s.Resolutions, s.Length }, EventCatalog.Json);
        Of(step).Should().Be(Of(liveStep));
    }

    #endregion

    #region A step that fills inside the instant

    [Fact]
    public void Advance_Should_KeepTheRestOfTheInstantInItsOwnMinute_When_AFullStepStopsBetweenTheReadings()
    {
        // 995 tracked people (in at 17:00, out at 17:01) give 995 waits, and their minutes and lines 4 more records; the entry
        // reading ending at 17:03 adds 2, so a step of 1,000 records stops right before the exit reading. The watermark is
        // then 17:03 less a tick and the exit reading is spread over that tick: still in 17:02, its own minute, never 17:03.
        var people = Enumerable.Range(0, 995).SelectMany(k => new QueueInput[]
        {
            new QueueCrossing("A-VIS entry", CrossingDirection.In, "S-15/" + k, At(k * 0.0001)),
            new QueueCrossing("A-VIS exit", CrossingDirection.Out, "S-15/" + k, At(1 + (k * 0.0001)))
        }).ToList();
        QueueInput[] instant = [new QueueInterval("A-VIS entry", 5, 0, At(2), At(3)), new QueueInterval("A-VIS exit", 0, 3, At(2), At(3))];

        var split = Engine(maxStepRecords: 1_000);
        foreach (var input in people.Concat(instant))
            split.Offer(input, input.TimeUtc);
        var first = split.Advance(At(30));
        first.More.Should().BeTrue();
        first.WatermarkUtc.Should().Be(At(3).AddTicks(-1), "the step stopped inside 17:03");
        split.BufferedEvents.Should().Be(1, "only the exit reading is left");
        var state = JsonSerializer.Deserialize<QueueEngineState>(JsonSerializer.Serialize(split.Capture(), EventCatalog.Json), EventCatalog.Json)!;
        var restored = QueueStateEngine.Restore(Geometry, new QueueEngineSettings { Lateness = TimeSpan.Zero, MaxStepRecords = 1_000 }, state);

        var continued = new List<QueueStep> { first, split.Advance(At(30)) };
        var afterRestore = new List<QueueStep> { first, restored.Advance(At(30)) };
        var whole = Play(Engine(), people.Concat(instant));

        foreach (var steps in new[] { continued, afterRestore, whole })
        {
            LineMinutes(steps).GetValueOrDefault((QueueLineRole.Exit, 2)).Out.Should().Be(3, "the exits of 17:02 stay in 17:02");
            LineMinutes(steps).ContainsKey((QueueLineRole.Exit, 3)).Should().BeFalse();
            ZoneMinutes(steps).GetValueOrDefault(2).Should().Be((5L, 3L));
        }

        static string Of(QueueStep step) => JsonSerializer.Serialize(new { step.WatermarkUtc, step.Movements, step.Lines, step.Waits, step.Resolutions, step.Length }, EventCatalog.Json);
        Of(afterRestore[1]).Should().Be(Of(continued[1]), "a restore in the middle of the split gives what the engine that kept running gives");
        continued[1].Waits.Should().HaveCount(3).And.OnlyContain(w => w.ExitUtc == At(3).AddTicks(-1), "the rest of the instant is spread over its last tick");
    }

    #endregion
}
