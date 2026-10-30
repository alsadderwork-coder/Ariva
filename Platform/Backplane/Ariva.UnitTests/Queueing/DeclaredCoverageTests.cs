using System.Text.Json;
using Ariva.Core.Queueing;
using Ariva.Core.Sensing;
using Ariva.Infra.Messaging;
using FluentAssertions;

namespace Ariva.UnitTests.Queueing;

/// <summary>
/// ARV-259e5 (formulas F11, ADR-0027): a device covers the entry and exit lines its latest health report declares it counts (Ingest
/// stamps the registry's declaration on the report), every line until a report says otherwise, and none when it declares none. A line
/// whose only declared covering devices are out makes the zone Unknown while other devices are live; a declared name that is not an
/// entry or exit line of the zone is ignored and counted; a declaration applies from its report's receive time; a declaration out of
/// the registry's shape makes its report invalid; and the declarations survive a snapshot (version 15), an earlier snapshot restoring
/// every device as counting every line.
/// </summary>
public sealed class DeclaredCoverageTests
{
    private const string ZoneKey = "DMO/A-VIS";
    private const string Entry = "A-VIS entry";
    private const string Exit = "A-VIS exit";
    private static readonly DateTime Start = new(2026, 9, 28, 18, 0, 0, DateTimeKind.Utc);
    private static readonly QueueZoneGeometry Geometry = new("A-VIS", new HashSet<string> { Entry }, new HashSet<string> { Exit },
        new HashSet<string>(), new HashSet<string>());

    private static string Json(object value) => JsonSerializer.Serialize(value, EventCatalog.Json);

    private static T Stamp<T>(T batch, string device, DateTime received) where T : SensingBatch
    {
        batch.Id = Guid.NewGuid();
        batch.DeviceId = Guid.NewGuid();
        batch.DeviceCode = device;
        batch.SiteCode = "DMO";
        batch.QueueZoneName = "A-VIS";
        batch.Commissioned = true;
        batch.ReceivedUtc = received;
        return batch;
    }

    private static ZoneOccupancyBatch Occupancy(string device, DateTime received) =>
        Stamp(new ZoneOccupancyBatch { Occupancy = [new Sensed<ZoneOccupancy>(new ZoneOccupancy("A-VIS", 3, received), received, SensedFlags.None)] }, device, received);

    private static DeviceStatusBatch Status(string device, DateTime received, IReadOnlyList<string> lines, bool online = true) =>
        Stamp(new DeviceStatusBatch { Online = online, CoveredLines = lines }, device, received);

    /// <summary>A device of the zone: the minutes it is silent, and the lines its health reports declare at each minute (null for every line).</summary>
    private sealed record Sensor(string Code, Func<int, bool> Silent, Func<int, string[]> Lines);

    private static readonly Func<int, bool> Never = _ => false;
    private static readonly Func<int, string[]> Every = _ => null;

    private static Func<int, string[]> Only(params string[] lines) => _ => lines;

    /// <summary>
    /// Minute by minute: each device not silent sends a reading and a health report with its declaration (as Ingest does, at least
    /// every 10 seconds), and the zone is ticked at the minute as the stream host ticks an idle zone.
    /// </summary>
    private static ZoneProcessor Play(IReadOnlyList<Sensor> sensors, int minutes = 40, Action<ZoneProcessor, int> each = null)
    {
        var zone = new ZoneProcessor(ZoneKey, Geometry, 12);
        for (var m = 0; m < minutes; m++)
        {
            var at = Start.AddMinutes(m);
            Minute(zone, sensors, m);
            each?.Invoke(zone, m);
            zone.Tick(at);
        }

        zone.Finish(Start.AddMinutes(minutes), Start.AddMinutes(minutes + 200));
        return zone;
    }

    private static void Minute(ZoneProcessor zone, IReadOnlyList<Sensor> sensors, int m)
    {
        var at = Start.AddMinutes(m);
        foreach (var sensor in sensors.Where(s => !s.Silent(m)))
        {
            zone.Offer(Occupancy(sensor.Code, at), DateTime.MaxValue);
            zone.Offer(Status(sensor.Code, at, sensor.Lines(m)), DateTime.MaxValue);
        }
    }

    private static List<int> UnknownMinutes(ZoneOutputs outputs) =>
        [.. outputs.Live.Where(l => l.Unknown).Select(l => (int)(l.MinuteUtc - Start).TotalMinutes)];

    private static BinQuality FinalBin(ZoneOutputs outputs, int minute) =>
        outputs.Bins.Where(b => b.Status == BinStatus.Final && b.StartUtc == Start.AddMinutes(minute)).Select(b => b.Quality).Last();

    private static long Ignored(ZoneOutputs outputs) =>
        outputs.Refusals.Where(r => r.Kind == ZoneProcessor.DeclaredLineNotInZone).Sum(r => r.Count);

    #region F11 with declared lines

    private static readonly Func<int, bool> OutFrom1011To1020 = m => m is > 10 and < 21;

    public static TheoryData<string, Func<int, string[]>, Func<int, string[]>, Func<int, bool>, int?, int?, int?, BinQuality, BinQuality, BinQuality> Cases => new()
    {
        // Formulas F11's declared cases. Name; what S-15 (always heard) and S-17 declare; when S-17 is silent; the spell [from, to) in
        // minutes and a minute it must hold (null: never Unknown); the 18:00, 18:15 and 18:30 bins.
        { "every device counts every line, one of two out", Every, Every, OutFrom1011To1020, null, null, null, BinQuality.Degraded, BinQuality.Degraded, BinQuality.Good },
        { "S-17 alone counts the exit and is out while S-15 is live", Only(Entry), Only(Exit), OutFrom1011To1020, 10, 20, 19, BinQuality.Unknown, BinQuality.Unknown, BinQuality.Good },
        { "a name the zone does not hold is ignored", Only(Entry), Only(Exit, "A-VIS count"), OutFrom1011To1020, 10, 20, 19, BinQuality.Unknown, BinQuality.Unknown, BinQuality.Good },
        { "S-15 counts both lines, S-17 none and is out", Only(Entry, Exit), Only(), OutFrom1011To1020, null, null, null, BinQuality.Degraded, BinQuality.Degraded, BinQuality.Good },
        { "S-15 counts none and is live, S-17 every line and is out", Only(), Every, OutFrom1011To1020, 10, 20, 19, BinQuality.Unknown, BinQuality.Unknown, BinQuality.Good },
        { "each counts one line, both live", Only(Entry), Only(Exit), Never, null, null, null, BinQuality.Good, BinQuality.Good, BinQuality.Good },
        { "S-15 counts both lines, S-17 the exit and is out", Only(Entry, Exit), Only(Exit), OutFrom1011To1020, null, null, null, BinQuality.Degraded, BinQuality.Degraded, BinQuality.Good },
        { "both count the entry only: no device counts the exit", Only(Entry), Only(Entry), Never, 0, 40, 30, BinQuality.Unknown, BinQuality.Unknown, BinQuality.Unknown }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Zone_Should_BeUnknownWhileALinesDeclaredDevicesAreOut_When_DevicesDeclareTheirLines(string name, Func<int, string[]> lines15,
        Func<int, string[]> lines17, Func<int, bool> silent17, int? from, int? to, int? holds, BinQuality bin1800, BinQuality bin1815, BinQuality bin1830)
    {
        var outputs = Play([new Sensor("S-15", Never, lines15), new Sensor("S-17", silent17, lines17)]).Drain();

        var unknown = UnknownMinutes(outputs);
        if (from is null)
            unknown.Should().BeEmpty(name);
        else
        {
            // Live minutes turn Unknown once the line's last covering device is out; those published before stay as they were.
            unknown.Should().NotBeEmpty(name).And.OnlyContain(m => m >= from && m < to, name).And.Contain(holds!.Value, name);
            outputs.Live.Where(l => l.Unknown).Should().OnlyContain(l => l.LengthDegraded, name);
        }

        FinalBin(outputs, 0).Should().Be(bin1800, name);
        FinalBin(outputs, 15).Should().Be(bin1815, name);
        FinalBin(outputs, 30).Should().Be(bin1830, name);
    }

    [Fact]
    public void Declaration_Should_ApplyFromItsReceiveTime_When_ADeviceStopsAndThenStartsCountingALine()
    {
        // Both devices heard throughout. S-15 counts the entry; S-17 every line, but its reports from 18:10 to 18:19 say the entry only:
        // the exit has no covering device from the minute of the 18:10 report, until the 18:20 report, whose push covers 18:19 (ARV-148).
        Func<int, string[]> s17 = m => m is >= 10 and < 20 ? [Entry] : null;

        var zone = Play([new Sensor("S-15", Never, Only(Entry)), new Sensor("S-17", Never, s17)]);
        var outputs = zone.Drain();

        outputs.Outages.Should().BeEmpty("no device was out");
        UnknownMinutes(outputs).Should().NotBeEmpty().And.OnlyContain(m => m >= 10 && m < 19).And.Contain(18);
        FinalBin(outputs, 0).Should().Be(BinQuality.Unknown, "18:10 to 18:14");
        FinalBin(outputs, 15).Should().Be(BinQuality.Unknown, "18:15 to 18:18");
        FinalBin(outputs, 30).Should().Be(BinQuality.Good);
        zone.Capture().Devices.Should().Contain(d => d.DeviceCode == "S-17" && d.CoveredLines == null, "its latest report counts every line again");
    }

    [Fact]
    public void Declaration_Should_LoseItsLineAtTheReportsMinute_When_TheLinesOtherDevicesWentOutEarlier()
    {
        // S-15 counts the exit and falls silent after 18:05 (out from 18:05); S-17 counts every line until its 18:12 report says the
        // entry only. The exit was covered by S-17 until then, so it is without coverage from 18:12, not from S-15's outage at 18:05.
        Func<int, string[]> s17 = m => m >= 12 ? [Entry] : null;

        var outputs = Play([new Sensor("S-15", m => m > 5, Only(Exit)), new Sensor("S-17", Never, s17)]).Drain();

        UnknownMinutes(outputs).Should().NotBeEmpty().And.OnlyContain(m => m >= 12).And.Contain(12);
        FinalBin(outputs, 0).Should().Be(BinQuality.Unknown, "18:12 to 18:14");
        FinalBin(outputs, 15).Should().Be(BinQuality.Unknown, "S-15 never came back");
    }

    [Fact]
    public void Zone_Should_CountTheNamesItDoesNotHold_When_AReportDeclaresThem()
    {
        // ARV-259e5: a declared name that is not an entry or exit line of the zone (a publish renamed the line, or it is another zone's)
        // is ignored, and counted with the engine's refusals, once per name and report.
        var zone = new ZoneProcessor(ZoneKey, Geometry, 12);
        for (var m = 0; m < 5; m++)
            zone.Offer(Status("S-17", Start.AddMinutes(m), [Exit, "A-VIS count", "B-VIS exit"]), DateTime.MaxValue);

        Ignored(zone.Peek()).Should().Be(10);
        zone.Capture().Devices.Single().CoveredLines.Should().Equal(Exit);
        zone.Counters.Invalid.Should().Be(0);
        zone.Acknowledge(zone.Peek());
        Ignored(zone.Peek()).Should().Be(0, "counted once");
    }

    public static TheoryData<string, string[]> Malformed => new()
    {
        { "17 names", [.. Enumerable.Range(0, 17).Select(i => $"line {i}")] },
        { "a name twice", [Exit, Exit] },
        { "an empty name", [Exit, ""] },
        { "a missing name", [Exit, null] },
        { "a name longer than a profile keeps", [new string('x', 201)] },
        // The registry's name rule (ARV-159): no control or invisible character, not even at a name's ends, so the health archive's line
        // feed join keeps each name whole and a replay reads back what the stream saw (ARV-259e5 review).
        { "a name with a line feed", ["Exit A\nEntry A"] },
        { "a name ending in a line feed", ["Exit A\n"] },
        { "a name of a line feed", ["\n"] },
        { "a name with a NUL", ["Exit\0A"] },
        { "a name of spaces", ["   "] }
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void Offer_Should_TakeTheReportAsInvalid_When_ItsDeclarationIsOutOfTheRegistrysShape(string name, string[] lines)
    {
        // CWE-501: Kafka is a trust boundary. Ingest stamps the registry's declaration (at most 16 distinct names of at most 200
        // characters); a report with any other shape did not come from it, so it is not applied at all, not even as a sign of life.
        var zone = new ZoneProcessor(ZoneKey, Geometry, 12);

        zone.Offer(Status("S-17", Start, lines), DateTime.MaxValue);

        zone.Counters.Invalid.Should().Be(1, name);
        zone.Capture().Devices.Should().BeEmpty(name);
        Ignored(zone.Peek()).Should().Be(0, name);
    }

    [Fact]
    public void Offer_Should_TakeSixteenNamesTheZoneDoesNotHold_When_TheyHaveTheRegistrysShape()
    {
        var zone = new ZoneProcessor(ZoneKey, Geometry, 12);

        zone.Offer(Status("S-17", Start, [.. Enumerable.Range(0, 16).Select(i => $"line {i}")]), DateTime.MaxValue);

        zone.Counters.Invalid.Should().Be(0);
        zone.Capture().Devices.Single().CoveredLines.Should().BeEmpty("none of them is a line of the zone: it counts none");
        Ignored(zone.Peek()).Should().Be(16);
        zone.UnknownNow.Should().BeTrue("no device counts the zone's lines");
    }

    [Fact]
    public void Zone_Should_GiveTheSameOutputs_When_ARecordsDeclarationsOfOneReceiveTimeComeInAnotherOrder()
    {
        // At 18:10 S-15 stops counting the exit and S-17 starts: in one order the exit is without a covering device between the two
        // reports, in the other never; a spell of no length is dropped, so the outputs are the same.
        var s15 = new Sensor("S-15", Never, m => m < 10 ? null : [Entry]);
        var s17 = new Sensor("S-17", Never, m => m < 10 ? [Entry] : null);

        var first = Play([s15, s17]).Drain();
        var second = Play([s17, s15]).Drain();

        UnknownMinutes(first).Should().BeEmpty();
        Json(second.Live).Should().Be(Json(first.Live));
        Json(second.Bins).Should().Be(Json(first.Bins));
        Json(second.Recomputations).Should().Be(Json(first.Recomputations));
    }

    [Fact]
    public void Covering_Should_MergeTheDevicesCountingEveryLineIntoEachLine_When_OthersDeclareTheirs()
    {
        // CWE-400: the devices counting every line fold into one cover merged into each line once; the result is what each line's own
        // devices give: live if any is, else out since the latest of their outages.
        var liveness = new DeviceLiveness(TimeSpan.FromSeconds(180), TimeSpan.FromHours(24), 64, TimeSpan.FromSeconds(60));
        string[] lines = ["A", "B", "C"];
        liveness.Heard(ZoneKey, "S-1", Start, online: false);
        liveness.Heard(ZoneKey, "S-2", Start.AddMinutes(5), online: false);
        liveness.Declare("S-2", ["B"]);
        liveness.Heard(ZoneKey, "S-3", Start.AddMinutes(2), online: true);
        liveness.Declare("S-3", ["C", "Z"]);
        liveness.Heard(ZoneKey, "S-4", Start.AddMinutes(7), online: false);
        liveness.Declare("S-4", []);

        var covering = liveness.Covering(lines);

        covering[0].Should().Be((false, Start), "A: only S-1, every line, out since 18:00");
        covering[1].Should().Be((false, Start.AddMinutes(5)), "B: S-1 and S-2, out, the later outage");
        covering[2].Should().Be((true, Start), "C: S-3 live; S-1 out since 18:00 counts it too");
        liveness.Covering([]).Should().BeEmpty();
    }

    [Fact]
    public void Trim_Should_CutADeclarationToOneMoreThanTheRegistrysBound_When_AHealthReportWaitsInTheMerge()
    {
        // CWE-400: a hostile report's declared lines are not kept whole while the record waits; one more than the bound stays, so the
        // zone still refuses it as out of shape.
        var batch = Status("S-17", Start, [.. Enumerable.Range(0, 5_000).Select(i => $"line {i}")]);
        var fits = Status("S-17", Start, [Entry, Exit]);

        Ariva.Infra.Streaming.QueueStreamWorker.Trim(batch);
        Ariva.Infra.Streaming.QueueStreamWorker.Trim(fits);

        batch.CoveredLines.Should().HaveCount(Ariva.Core.Domain.Entities.Device.MaxCoveredLines + 1);
        LineDeclarations.IsWellFormed(batch.CoveredLines).Should().BeFalse();
        fits.CoveredLines.Should().Equal(Entry, Exit);
        var zone = new ZoneProcessor(ZoneKey, Geometry, 12);
        zone.Offer(batch, DateTime.MaxValue);
        zone.Counters.Invalid.Should().Be(1);
    }

    #endregion

    #region Snapshots

    private static readonly Sensor EntryCounter = new("S-15", Never, Only(Entry));
    private static readonly Sensor ExitCounter = new("S-17", OutFrom1011To1020, Only(Exit));

    private static ZoneProcessorState Saved(ZoneProcessor zone) => JsonSerializer.Deserialize<ZoneProcessorState>(Json(zone.Capture()), EventCatalog.Json)!;

    /// <summary>S-15 counts the entry, S-17 the exit and is silent from 18:11: captured and saved at 18:15, during the exit's spell.</summary>
    private static (ZoneProcessor Zone, ZoneProcessorState State) DuringTheExitsSpell()
    {
        var zone = new ZoneProcessor(ZoneKey, Geometry, 12);
        for (var m = 0; m <= 15; m++)
        {
            Minute(zone, [EntryCounter, ExitCounter], m);
            zone.Tick(Start.AddMinutes(m));
        }

        zone.Drain();
        return (zone, Saved(zone));
    }

    [Fact]
    public void Coverage_Should_ContinueIdentically_When_RestoredFromASnapshotWithDeclarations()
    {
        var straight = Play([EntryCounter, ExitCounter]).Drain();

        var zone = new ZoneProcessor(ZoneKey, Geometry, 12);
        var outputs = new List<ZoneOutputs>();
        for (var m = 0; m < 40; m++)
        {
            Minute(zone, [EntryCounter, ExitCounter], m);
            zone.Tick(Start.AddMinutes(m));
            if (m is 15 or 21)
            {
                outputs.Add(zone.Drain());
                var state = Saved(zone);
                state.Version.Should().Be(15).And.Be(ZoneProcessorState.CurrentVersion);
                state.Devices.Should().Contain(d => d.DeviceCode == "S-15" && d.CoveredLines.SequenceEqual(new[] { Entry }));
                if (m == 15)
                    state.Coverage.Should().ContainSingle("only the exit is without a live covering device").Which.LineName.Should().Be(Exit);
                zone = ZoneProcessor.Restore(ZoneKey, Geometry, 12, new ZoneProcessorSettings(), state, Start.AddDays(1));
            }
        }

        zone.Finish(Start.AddMinutes(40), Start.AddMinutes(240));
        outputs.Add(zone.Drain());

        UnknownMinutes(straight).Should().Contain(19);
        Json(outputs.SelectMany(o => o.Live)).Should().Be(Json(straight.Live));
        Json(outputs.SelectMany(o => o.Bins)).Should().Be(Json(straight.Bins));
        Json(outputs.SelectMany(o => o.Recomputations)).Should().Be(Json(straight.Recomputations));
    }

    [Fact]
    public void Restore_Should_TakeEveryDeviceAsCountingEveryLine_When_TheSnapshotWasWrittenBeforeVersion15()
    {
        // Both devices heard to 18:05 with their declarations; S-17 then falls silent. Restored from version 15 the exit loses its only
        // covering device; restored from a build before it, every device counts every line, as it did then, and S-15 covers the exit.
        var zone = new ZoneProcessor(ZoneKey, Geometry, 12);
        for (var m = 0; m <= 5; m++)
        {
            Minute(zone, [EntryCounter, new Sensor("S-17", Never, Only(Exit))], m);
            zone.Tick(Start.AddMinutes(m));
        }

        zone.Drain();
        var state = Saved(zone);
        var before = state.WrittenBefore15() with { Version = 14 };
        var notAfter = Start.AddDays(1);
        SnapshotChecks.Of(before, ZoneKey, "A-VIS", 12, notAfter).Should().BeEmpty();

        var current = ZoneProcessor.Restore(ZoneKey, Geometry, 12, new ZoneProcessorSettings(), state, notAfter);
        var earlier = ZoneProcessor.Restore(ZoneKey, Geometry, 12, new ZoneProcessorSettings(), before, notAfter);
        earlier.Capture().Devices.Should().HaveCount(2).And.OnlyContain(d => d.CoveredLines == null);
        foreach (var restored in new[] { current, earlier })
            restored.Offer(Occupancy("S-15", Start.AddMinutes(9)), DateTime.MaxValue);

        current.UnknownNow.Should().BeTrue("S-17 alone counted the exit");
        earlier.UnknownNow.Should().BeFalse("S-15 counts every line until its next report says otherwise");
        earlier.Offer(Status("S-15", Start.AddMinutes(9), [Entry]), DateTime.MaxValue);
        earlier.UnknownNow.Should().BeTrue("its report declares the entry only");
    }

    [Fact]
    public void Restore_Should_RefuseTheSnapshot_When_ItsDeclarationsAreOfAnEarlierVersionOutOfShapeOrNotTheZones()
    {
        var (_, state) = DuringTheExitsSpell();
        var notAfter = Start.AddDays(1);
        SnapshotChecks.Of(state, ZoneKey, "A-VIS", 12, notAfter).Should().BeEmpty();
        var s15 = state.Devices.Single(d => d.DeviceCode == "S-15");
        var s17 = state.Devices.Single(d => d.DeviceCode == "S-17");
        const string Implausible = "1 values of the snapshot are not plausible (negative counts, implausible times or empty items).";

        foreach (var (bad, problem, because) in new (ZoneProcessorState, string, string)[]
                 {
                     (state with { Version = 14 }, "A snapshot before version 15 carries no lines a device declared it counts.", "declarations before version 15"),
                     (state with { Devices = [s15 with { CoveredLines = [.. Enumerable.Range(0, 17).Select(i => $"line {i}")] }, s17] }, Implausible, "17 names"),
                     (state with { Devices = [s15 with { CoveredLines = [Entry, Entry] }, s17] }, Implausible, "a name twice"),
                     (state with { Devices = [s15 with { CoveredLines = [Entry, ""] }, s17] }, Implausible, "an empty name"),
                     (state with { Devices = [s15 with { CoveredLines = [new string('x', 201)] }, s17] }, Implausible, "a name no profile keeps"),
                     (state with { Devices = [s15 with { CoveredLines = ["A-VIS entry\n"] }, s17] }, Implausible, "a name with a line feed")
                 })
        {
            SnapshotChecks.Of(bad, ZoneKey, "A-VIS", 12, notAfter).Should().Equal([problem], because);
            var restore = () => ZoneProcessor.Restore(ZoneKey, Geometry, 12, new ZoneProcessorSettings(), bad, notAfter);
            restore.Should().Throw<InvalidDataException>(because).WithMessage("*" + problem, because);
        }

        // A name in the registry's shape that is not one of the zone's entry or exit lines: the engine keeps only the zone's lines, so a
        // snapshot naming another is not its own.
        var foreign = state with { Devices = [s15 with { CoveredLines = [Entry, "A-VIS count"] }, s17] };
        SnapshotChecks.Of(foreign, ZoneKey, "A-VIS", 12, notAfter).Should().BeEmpty();
        var restoreForeign = () => ZoneProcessor.Restore(ZoneKey, Geometry, 12, new ZoneProcessorSettings(), foreign, notAfter);
        restoreForeign.Should().Throw<InvalidDataException>().WithMessage("*counts a line that is not one of the zone's entry or exit lines*");

        // Declarations that disagree with the snapshot's lines without coverage: S-15 counting none would leave the entry without a
        // covering device, which the snapshot does not say.
        var disagree = state with { Devices = [s15 with { CoveredLines = [] }, s17] };
        var restoreDisagree = () => ZoneProcessor.Restore(ZoneKey, Geometry, 12, new ZoneProcessorSettings(), disagree, notAfter);
        restoreDisagree.Should().Throw<InvalidDataException>().WithMessage("*do not agree with its devices*");
    }

    #endregion
}
