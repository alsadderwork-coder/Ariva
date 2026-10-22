using System.Text.Json;
using System.Text.Json.Nodes;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Queueing;
using Ariva.Core.Sensing;
using Ariva.Infra.Messaging;
using FluentAssertions;

namespace Ariva.UnitTests.Queueing;

/// <summary>
/// ARV-168: a queue zone applies a sensing batch once. A device that sends a push again whose request it dropped sends
/// the same body, which Ingest gives the same batch id; the zone drops a batch of the same device with the same id
/// within its window (until the watermark is a lateness past the batch's latest event), so a resent interval-count
/// batch no longer tallies its In and Out twice. A resend is then exactly a health report at its receive time: the
/// device was heard, nothing else changes. The key is the device and the id within the zone; the memory is bounded per
/// zone and per device and travels in the zone's snapshot (version 10).
/// </summary>
public sealed class RepeatedBatchTests
{
    private const string Zone = "Q-T1";
    private const string ZoneKey = "DMO/" + Zone;
    private const string Entry = Zone + " entry";
    private const string Exit = Zone + " exit";
    private static readonly QueueZoneGeometry Geometry = new(Zone, new HashSet<string> { Entry }, new HashSet<string> { Exit }, new HashSet<string>(), new HashSet<string>());
    private static readonly DateTime T0 = new(2026, 10, 13, 17, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Far = T0.AddDays(1);

    #region Helpers

    private static DateTime At(double seconds) => T0.AddSeconds(seconds);

    /// <summary>The end of minute <paramref name="m"/> in seconds after 17:00: its push is received a second later.</summary>
    private static double End(int m) => (m + 1) * 60;

    private static Guid Id(int n) => Guid.Parse($"0199e0a0-0000-8000-8000-{n:D12}");

    private static T Stamp<T>(T batch, string device, Guid id, DateTime received) where T : SensingBatch
    {
        batch.Id = id;
        batch.OccurredOn = received;
        batch.DeviceId = Guid.Parse($"0199e0a0-0000-7000-8000-0000000000{device[^2..]}");
        batch.DeviceCode = device;
        batch.SiteCode = "DMO";
        batch.QueueZoneName = Zone;
        batch.Dialect = "canonical";
        batch.Commissioned = true;
        batch.ReceivedUtc = received;
        batch.Clock = new ClockReading(0, true, ClockState.Ok);
        return batch;
    }

    /// <summary>A T1 counter's push of minute <paramref name="m"/>: its entry and exit lines' counts over the minute, received a second after it.</summary>
    private static IntervalCountBatch Counts(int m, string device = "S-81", Guid? id = null, int @in = 5, int @out = 3, double? received = null)
    {
        var from = T0.AddMinutes(m);
        var to = from.AddMinutes(1);
        return Stamp(new IntervalCountBatch
        {
            Intervals =
            [
                new(new IntervalCount(Entry, @in, 0, from, to), to, SensedFlags.None),
                new(new IntervalCount(Exit, 0, @out, from, to), to, SensedFlags.None)
            ]
        }, device, id ?? Id(m), At(received ?? End(m) + 1));
    }

    /// <summary>A vendor's anonymous crossings of minute <paramref name="m"/> (no track ids): five in, three out, received a second after it.</summary>
    private static VendorLineCrossingBatch Anonymous(int m)
    {
        var from = T0.AddMinutes(m);
        var crossings = Enumerable.Range(0, 5).Select(k => (Line: Entry, Direction: CrossingDirection.In, At: from.AddSeconds(5 + (k * 10))))
            .Concat(Enumerable.Range(0, 3).Select(k => (Line: Exit, Direction: CrossingDirection.Out, At: from.AddSeconds(8 + (k * 15)))))
            .OrderBy(c => c.At).Select(c => new Sensed<LineCrossing>(new LineCrossing(c.Line, c.Direction, null, c.At), c.At, SensedFlags.None)).ToList();
        return Stamp(new VendorLineCrossingBatch { Crossings = crossings }, "S-81", Id(m), At(End(m) + 1));
    }

    /// <summary>The same batch again, as a resent body or a redelivered record arrives: read back from its JSON, with its own receive time.</summary>
    private static SensingBatch Again(SensingBatch batch, double receivedSeconds)
    {
        var copy = (SensingBatch)JsonSerializer.Deserialize(JsonSerializer.Serialize(batch, batch.GetType(), EventCatalog.Json), batch.GetType(), EventCatalog.Json)!;
        copy.ReceivedUtc = At(receivedSeconds);
        copy.OccurredOn = copy.ReceivedUtc;
        return copy;
    }

    /// <summary>What a resend amounts to once dropped: the device was heard at its receive time.</summary>
    private static DeviceStatusBatch Heard(SensingBatch batch, double receivedSeconds) => new()
    {
        Id = Guid.Parse("0199e0a0-0000-7000-8000-00000000beef"),
        OccurredOn = At(receivedSeconds),
        DeviceId = batch.DeviceId,
        DeviceCode = batch.DeviceCode,
        SiteCode = batch.SiteCode,
        QueueZoneName = batch.QueueZoneName,
        Dialect = "health",
        Commissioned = true,
        ReceivedUtc = At(receivedSeconds),
        Clock = batch.Clock,
        Online = true
    };

    /// <summary>Twelve minutes of pushes with the given resends (minute and receive time), in receive order; each resend as itself or as a health report.</summary>
    private static List<SensingBatch> Evening(Func<int, SensingBatch> push, IEnumerable<(int Minute, double At)> resends, bool asHealth)
    {
        var pushes = Enumerable.Range(0, 12).Select(push).ToList();
        var extra = resends.Select(r => asHealth ? Heard(pushes[r.Minute], r.At) : Again(pushes[r.Minute], r.At));
        // Stable: a resend received with its original comes right after it, as a redelivered record does.
        return [.. pushes.Concat(extra).OrderBy(b => b.ReceivedUtc)];
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, EventCatalog.Json);

    private static T RoundTrip<T>(T value) => JsonSerializer.Deserialize<T>(Json(value), EventCatalog.Json)!;

    /// <summary>The zone's outputs and counters for <paramref name="batches"/>, restored from its JSON snapshot after the batches whose indexes are in <paramref name="cuts"/>.</summary>
    private static (string Outputs, List<ZoneOutputs> Raw, ZoneProcessor Zone) Run(IReadOnlyList<SensingBatch> batches, ZoneProcessorSettings settings = null,
        IEnumerable<int> cuts = null)
    {
        settings ??= new ZoneProcessorSettings();
        var cutSet = (cuts ?? []).ToHashSet();
        var zone = new ZoneProcessor(ZoneKey, Geometry, 3, settings);
        var outputs = new List<ZoneOutputs>();
        for (var k = 0; k < batches.Count; k++)
        {
            zone.Offer(batches[k], DateTime.MaxValue);
            if (cutSet.Contains(k))
            {
                outputs.Add(zone.Drain());
                zone = ZoneProcessor.Restore(ZoneKey, Geometry, 3, settings, RoundTrip(zone.Capture()), Far);
            }
        }

        zone.Tick(T0.AddHours(3));
        outputs.Add(zone.Drain());
        var all = outputs;
        return (Json(new
        {
            Minutes = all.SelectMany(o => o.Minutes).ToList(),
            Bins = all.SelectMany(o => o.Bins).ToList(),
            Live = all.SelectMany(o => o.Live).ToList(),
            Recomputations = all.SelectMany(o => o.Recomputations).ToList(),
            Lines = all.SelectMany(o => o.Lines).ToList(),
            Health = all.SelectMany(o => o.Health).ToList(),
            Outages = all.SelectMany(o => o.Outages).ToList()
        }), all, zone);
    }

    /// <summary>The ins and outs counted on a line in minute <paramref name="m"/>.</summary>
    private static (long In, long Out) Line(IEnumerable<ZoneOutputs> outputs, string line, int m) =>
        outputs.SelectMany(o => o.Lines).Where(l => l.LineName == line && l.MinuteUtc == T0.AddMinutes(m))
            .Aggregate((In: 0L, Out: 0L), (t, l) => (t.In + l.In, t.Out + l.Out));

    /// <summary>The ins and outs counted on a line over the evening.</summary>
    private static (long In, long Out) Total(IEnumerable<ZoneOutputs> outputs, string line) =>
        outputs.SelectMany(o => o.Lines).Where(l => l.LineName == line).Aggregate((In: 0L, Out: 0L), (t, l) => (t.In + l.In, t.Out + l.Out));

    /// <summary>The late events of the final bins.</summary>
    private static long Late(IEnumerable<ZoneOutputs> outputs) =>
        outputs.SelectMany(o => o.Bins).GroupBy(b => b.StartUtc).Select(g => g.Last()).Sum(b => b.LateEvents);

    #endregion



    #region The same batch twice counts once

    public static TheoryData<string, bool, int[], double[]> Resends => new()
    {
        // Minute 3's push is received at 17:04:01 (End(3) + 1); the device's next push at 17:05:01. Without the rule each of
        // these resends would be applied again: its events (17:04:00) are still ahead of the watermark, which is the
        // zone's clock (its latest event) less the 90-second lateness, until the push received at 17:06:01. Columns: the
        // minutes resent and when each resend arrives.
        { "a redelivered record, right after the first", false, [3], [End(3) + 1] },
        { "a resend 20 seconds later, before the next push", false, [3], [End(3) + 20] },
        { "a resend after the device's next push", false, [3], [End(4) + 5] },
        { "two resends of the same push", false, [3, 3], [End(3) + 20, End(4) + 5] },
        { "resends of two pushes", false, [3, 4], [End(3) + 20, End(4) + 30] },
        { "anonymous vendor crossings resent", true, [3, 3], [End(3) + 20, End(4) + 5] }
    };

    [Theory]
    [MemberData(nameof(Resends))]
    public void Offer_Should_ApplyABatchOnce_When_TheSameDeviceSendsItAgainWithinItsWindow(string because, bool crossings, int[] minutes, double[] at)
    {
        Func<int, SensingBatch> push = crossings ? Anonymous : m => Counts(m);
        var resends = minutes.Zip(at).ToList();

        var (resent, raw, zone) = Run(Evening(push, resends, asHealth: false));
        var (heard, _, _) = Run(Evening(push, resends, asHealth: true));

        resent.Should().Be(heard, "a dropped resend is only a sign of life at its receive time ({0})", because);
        zone.Counters.RepeatedBatches.Should().Be(resends.Count, because);
        zone.Counters.ForgottenBatches.Should().Be(0);
        foreach (var m in minutes.Distinct())
        {
            Line(raw, Entry, m).Should().Be((5L, 0L), "minute {0}'s entries count once ({1})", m, because);
            // ARV-173: the exit line's reading ends with the entry line's and is spread over its own minute too.
            Line(raw, Exit, m).Should().Be((0L, 3L), "minute {0}'s exits count once, in their minute ({1})", m, because);
        }

        Total(raw, Entry).Should().Be((60L, 0L), "twelve pushes of 5 entries ({0})", because);
        Total(raw, Exit).Should().Be((0L, 36L), "twelve pushes of 3 exits ({0})", because);

        Late(raw).Should().Be(0, "a dropped resend is not a late event either");
    }

    [Fact]
    public void Offer_Should_CountTheBatchAgain_When_ItIsANewBatchWithTheSameCounts()
    {
        // What the rule must not hide: the same counts in a new batch (another body, so another id) are new data.
        var batches = Evening(m => Counts(m), [], asHealth: false);
        var resent = Again(batches[3], End(3) + 20);
        resent.Id = Id(99);
        batches.Insert(4, resent);

        var (_, raw, zone) = Run(batches);

        zone.Counters.RepeatedBatches.Should().Be(0);
        Total(raw, Entry).Should().Be((65L, 0L));
        Total(raw, Exit).Should().Be((0L, 39L));
    }

    #endregion

    #region The key is the device and the id

    public static TheoryData<string, string, string, int, long> Devices => new()
    {
        // The second batch of minute 3: its device, its id (the first's or another), the entries counted over the evening,
        // the batches dropped.
        { "two devices with the same id both count", "S-82", "same", 65, 0 },
        { "two devices with their own ids both count", "S-82", "other", 65, 0 },
        { "the same device with the same id counts once", "S-81", "same", 60, 1 },
        { "the same device with another id counts twice", "S-81", "other", 65, 0 }
    };

    [Theory]
    [MemberData(nameof(Devices))]
    public void Offer_Should_KeyTheBatchByDeviceAndId_When_TwoBatchesShareAnId(string because, string second, string id, int entries, long repeated)
    {
        // A device cannot suppress another device's batch: S-81 and S-82 count the same line in the same minute.
        var batches = Evening(m => Counts(m), [], asHealth: false);
        batches.Insert(4, Counts(3, second, id == "same" ? Id(3) : Id(77), received: End(3) + 2));

        var (_, raw, zone) = Run(batches);

        Total(raw, Entry).In.Should().Be(entries, because);
        zone.Counters.RepeatedBatches.Should().Be(repeated, because);
    }

    #endregion

    #region The window

    public static TheoryData<string, double, long, long, long> Windows => new()
    {
        // The resend of minute 3 (events at 17:04:00, a lateness of 90 s): remembered until the watermark reaches 17:05:30,
        // which the push received at 17:07:01 (the zone's clock at 17:07:00) brings. Columns: when it arrives, the batches
        // dropped, the late events counted, the entries counted in minute 3.
        { "within the window, its events still ahead of the watermark", End(4) + 5, 1, 0, 5 },
        { "within the window, its events already behind the watermark", End(5) + 5, 1, 0, 5 },
        { "after the window: forgotten, its events late and not applied", End(6) + 5, 0, 2, 5 },
        { "long after the window", End(10) + 5, 0, 2, 5 }
    };

    [Theory]
    [MemberData(nameof(Windows))]
    public void Offer_Should_DropTheBatchWithinItsWindowOnly_When_ItIsResent(string because, double at, long repeated, long late, long entries)
    {
        var (_, raw, zone) = Run(Evening(m => Counts(m), [(3, at)], asHealth: false));

        zone.Counters.RepeatedBatches.Should().Be(repeated, because);
        Late(raw).Should().Be(late, because);
        Line(raw, Entry, 3).In.Should().Be(entries, "{0}: never applied twice", because);
        Total(raw, Entry).In.Should().Be(60, because);
    }

    public static TheoryData<string, int, long, long> Reuses => new()
    {
        // The device sends minute m's push under minute 3's id, at its usual time. Columns: m, the batches dropped, the
        // entries counted in minute m.
        { "after the window (a counter that wrapped): counted again", 9, 0, 5 },
        { "within the window: the same batch, dropped", 4, 1, 0 }
    };

    [Theory]
    [MemberData(nameof(Reuses))]
    public void Offer_Should_CountAReusedIdAgain_When_ItsWindowHasEnded(string because, int minute, long repeated, long entries)
    {
        var batches = Evening(m => Counts(m), [], asHealth: false);
        batches[minute] = Counts(minute, id: Id(3));

        var (_, raw, zone) = Run(batches);

        zone.Counters.RepeatedBatches.Should().Be(repeated, because);
        Line(raw, Entry, minute).In.Should().Be(entries, because);
        Line(raw, Entry, 3).In.Should().Be(5);
    }

    [Fact]
    public void Offer_Should_ForgetABatch_When_TheWatermarkIsALatenessPastItsLatestEvent()
    {
        var zone = new ZoneProcessor(ZoneKey, Geometry, 3);
        zone.Offer(Counts(3), DateTime.MaxValue);

        zone.Capture().RecentBatches.Should().Equal(new RecentBatchState("S-81", Id(3), At(End(3) + 90)));
        foreach (var m in new[] { 4, 5 })
        {
            zone.Offer(Counts(m), DateTime.MaxValue);
            zone.Capture().RecentBatches.Should().Contain(b => b.BatchId == Id(3), "the watermark is {0}", zone.WatermarkUtc);
        }

        zone.Offer(Counts(6), DateTime.MaxValue);
        zone.WatermarkUtc.Should().Be(At(End(3) + 90));
        zone.Capture().RecentBatches.Select(b => b.BatchId).Should().Equal(Id(4), Id(5), Id(6));
    }

    [Fact]
    public void Offer_Should_NotLengthenTheWindow_When_AnEventIsRefusedAsTooFarAhead()
    {
        // An event more than the engine's MaxAhead (5 minutes) ahead of its receive time is refused, so it never keeps its
        // batch remembered longer; a batch with nothing else is not remembered at all.
        var ahead = Counts(3);
        ahead.Intervals = [.. ahead.Intervals, new Sensed<IntervalCount>(new IntervalCount(Entry, 1, 0, At(End(3) + 600), At(End(3) + 660)), At(End(3) + 660), SensedFlags.None)];
        var only = Counts(4, id: Id(40));
        only.Intervals = [new Sensed<IntervalCount>(new IntervalCount(Entry, 1, 0, At(End(4) + 600), At(End(4) + 660)), At(End(4) + 660), SensedFlags.None)];
        var zone = new ZoneProcessor(ZoneKey, Geometry, 3);

        zone.Offer(ahead, DateTime.MaxValue);
        zone.Offer(only, DateTime.MaxValue);

        zone.Capture().RecentBatches.Should().Equal(new RecentBatchState("S-81", Id(3), At(End(3) + 90)));
    }

    [Fact]
    public void Offer_Should_NeverRememberABatch_When_ItHasNoIdOrIsAHealthReport()
    {
        var zone = new ZoneProcessor(ZoneKey, Geometry, 3);
        var unnamed = Counts(3, id: Guid.Empty);

        zone.Offer(unnamed, DateTime.MaxValue);
        zone.Offer(Again(unnamed, End(3) + 20), DateTime.MaxValue);
        zone.Offer(Heard(unnamed, End(3) + 30), DateTime.MaxValue);
        zone.Offer(Heard(unnamed, End(3) + 40), DateTime.MaxValue);

        zone.RememberedBatches.Should().Be(0);
        zone.Counters.RepeatedBatches.Should().Be(0, "an id that is empty names no batch, and health reports have no events to apply twice");
    }

    #endregion

    #region The bound

    [Fact]
    public void Offer_Should_ForgetTheDevicesEarliestWindowFirst_When_ADeviceExceedsItsShare()
    {
        // S-81 counts minute 3 in three batches whose latest events are 17:03:30, :40 and :50; with two per device, the
        // first is forgotten when the third arrives, so its resend is applied again (the bound's cost), the others' are not.
        var settings = new ZoneProcessorSettings { MaxRememberedBatches = 6, MaxRememberedBatchesPerDevice = 2 };
        IntervalCountBatch Part(int k) => Stamp(new IntervalCountBatch
        {
            Intervals = [new(new IntervalCount(Entry, 1, 0, At(180 + (k * 10)), At(210 + (k * 10))), At(210 + (k * 10)), SensedFlags.None)]
        }, "S-81", Id(30 + k), At(End(3) + 1));
        var batches = new List<SensingBatch> { Part(0), Part(1), Part(2), Again(Part(0), End(3) + 5), Again(Part(2), End(3) + 6) };

        var (_, raw, zone) = Run(batches, settings);

        zone.Counters.ForgottenBatches.Should().Be(2, "the first part when the third arrived, and the first part again when its resend arrived");
        zone.Counters.RepeatedBatches.Should().Be(1, "the third part's resend");
        Line(raw, Entry, 3).In.Should().Be(4, "three parts, the first applied twice");
    }

    [Fact]
    public void Offer_Should_ForgetTheZonesEarliestWindowFirst_When_TheZoneIsFull()
    {
        var settings = new ZoneProcessorSettings { MaxRememberedBatches = 6, MaxRememberedBatchesPerDevice = 2 };
        var zone = new ZoneProcessor(ZoneKey, Geometry, 3, settings);
        // Four devices, two batches each, every window ending at 17:05:30: ties go by device code, then id, so S-80's two
        // are forgotten when the seventh and eighth arrive.
        for (var k = 0; k < 8; k++)
            zone.Offer(Counts(3, $"S-8{k / 2}", Id(k), @in: 1, @out: 0, received: End(3) + 1 + (k * 0.001)), DateTime.MaxValue);

        var remembered = zone.Capture().RecentBatches;
        remembered.Should().HaveCount(6);
        zone.Counters.ForgottenBatches.Should().Be(2);
        remembered.Should().NotContain(b => b.DeviceCode == "S-80");
        remembered.GroupBy(b => b.DeviceCode).Should().OnlyContain(g => g.Count() == 2);
    }

    [Fact]
    public void Offer_Should_KeepTheOtherDevicesBatches_When_ADeviceFloodsTheZone()
    {
        // CWE-120: a device that sends thousands of batches holds at most its share (1,000 by default) and forgets only its
        // own; S-82's batch, sent first, is still remembered, so its resend is dropped.
        var zone = new ZoneProcessor(ZoneKey, Geometry, 3);
        var other = Counts(3, "S-82", Id(1));
        zone.Offer(other, DateTime.MaxValue);
        for (var k = 0; k < 3_000; k++)
        {
            var reading = At(End(3) + (k * 0.01));
            zone.Offer(Stamp(new ZoneOccupancyBatch { Occupancy = [new(new ZoneOccupancy(Zone, 1, reading), reading, SensedFlags.None)] }, "S-81", Id(1_000 + k), At(End(3) + 31)),
                DateTime.MaxValue);
        }

        zone.RememberedBatches.Should().Be(1_001);
        zone.Capture().RecentBatches.Count(b => b.DeviceCode == "S-81").Should().Be(1_000);
        zone.Counters.ForgottenBatches.Should().Be(2_000);
        zone.Offer(Again(other, End(3) + 40), DateTime.MaxValue);
        zone.Counters.RepeatedBatches.Should().Be(1);
    }

    #endregion

    #region Snapshot and restore

    [Fact]
    public void Restore_Should_DropTheResend_When_TheZoneWasRestoredBetweenTheBatchAndItsResend()
    {
        var batches = Evening(m => Counts(m), [(3, End(3) + 20), (3, End(4) + 5), (6, End(6) + 30)], asHealth: false);
        var (straight, raw, uninterrupted) = Run(batches);
        Total(raw, Entry).Should().Be((60L, 0L), "each push counts once");

        foreach (var cuts in new int[][] { [3], [4, 5], [.. Enumerable.Range(0, batches.Count)] })
        {
            var (restored, _, zone) = Run(batches, cuts: cuts);
            restored.Should().Be(straight, "a zone restored after batches {0} drops the same resends", string.Join(", ", cuts));
            zone.Counters.Should().Be(uninterrupted.Counters);
        }

        uninterrupted.Counters.RepeatedBatches.Should().Be(3);
    }

    [Fact]
    public void Capture_Should_CarryTheRememberedBatches_When_TheZoneIsSaved()
    {
        var zone = new ZoneProcessor(ZoneKey, Geometry, 3);
        zone.Offer(Counts(3), DateTime.MaxValue);
        zone.Offer(Counts(3, "S-82", Id(3)), DateTime.MaxValue);

        var json = JsonSerializer.SerializeToNode(zone.Capture(), EventCatalog.Json)!.AsObject();
        var state = json.Deserialize<ZoneProcessorState>(EventCatalog.Json)!;

        state.Version.Should().Be(11).And.Be(ZoneProcessorState.CurrentVersion);
        json["recentBatches"]!.AsArray().Should().HaveCount(2);
        state.RecentBatches.Should().Equal(new RecentBatchState("S-81", Id(3), At(End(3) + 90)), new RecentBatchState("S-82", Id(3), At(End(3) + 90)));
        ZoneProcessor.Restore(ZoneKey, Geometry, 3, new ZoneProcessorSettings(), state, Far).Capture().RecentBatches.Should().Equal(state.RecentBatches);
    }

    [Fact]
    public void Restore_Should_RememberNothing_When_TheSnapshotWasWrittenBeforeVersion10()
    {
        // A version 9 snapshot has no remembered batches: it restores with none, so a batch applied before the upgrade and
        // resent just after it is applied again (once, within one window of the upgrade).
        var zone = new ZoneProcessor(ZoneKey, Geometry, 3);
        zone.Offer(Counts(3), DateTime.MaxValue);
        var json = JsonSerializer.SerializeToNode(zone.Capture() with { Version = 9 }, EventCatalog.Json)!.AsObject();
        json.Remove("recentBatches").Should().BeTrue("a snapshot written now names them");
        var older = json.Deserialize<ZoneProcessorState>(EventCatalog.Json)!;

        var restored = ZoneProcessor.Restore(ZoneKey, Geometry, 3, new ZoneProcessorSettings(), older, Far);
        restored.RememberedBatches.Should().Be(0);
        restored.Capture().Version.Should().Be(ZoneProcessorState.CurrentVersion);
        restored.Offer(Again(Counts(3), End(3) + 20), DateTime.MaxValue);
        restored.Counters.RepeatedBatches.Should().Be(0);
    }

    public static TheoryData<string> BadSnapshots =>
        ["a version 9 snapshot that remembers batches", "an invalid device code", "an empty id", "a batch twice", "a window the watermark has reached",
         "a window beyond the receive clock plus MaxAhead and the lateness", "an implausible window", "more of one device than its share",
         "more than the zone keeps", "more than any zone keeps", "an empty entry"];

    [Theory]
    [MemberData(nameof(BadSnapshots))]
    public void Restore_Should_RefuseTheSnapshot_When_ItsRememberedBatchesAreNotPlausible(string bad)
    {
        // CWE-501: a snapshot comes back from storage and is checked before it is used.
        var settings = new ZoneProcessorSettings { MaxRememberedBatches = 6, MaxRememberedBatchesPerDevice = 3 };
        var zone = new ZoneProcessor(ZoneKey, Geometry, 3, settings);
        foreach (var m in new[] { 0, 1, 2 })
            zone.Offer(Counts(m), DateTime.MaxValue);
        var state = RoundTrip(zone.Capture());
        state.RecentBatches.Should().HaveCount(3);
        ZoneProcessor.Restore(ZoneKey, Geometry, 3, settings, state, Far).RememberedBatches.Should().Be(3, "the snapshot as written restores");
        var first = state.RecentBatches[0];
        RecentBatchState Other(int k, string device = "S-81") => new(device, Id(500 + k), first.UntilUtc.AddSeconds(k));
        var changed = bad switch
        {
            "a version 9 snapshot that remembers batches" => state with { Version = 9 },
            "an invalid device code" => state with { RecentBatches = [first with { DeviceCode = "s-81;drop" }] },
            "an empty id" => state with { RecentBatches = [first with { BatchId = Guid.Empty }] },
            "a batch twice" => state with { RecentBatches = [first, first with { UntilUtc = first.UntilUtc.AddSeconds(1) }] },
            "a window the watermark has reached" => state with { RecentBatches = [first with { UntilUtc = zone.WatermarkUtc }] },
            "a window beyond the receive clock plus MaxAhead and the lateness" => state with { RecentBatches = [first with { UntilUtc = state.ReferenceUtc.AddMinutes(6.5).AddTicks(1) }] },
            "an implausible window" => state with { RecentBatches = [first with { UntilUtc = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc) }] },
            "more of one device than its share" => state with { RecentBatches = [.. Enumerable.Range(0, 4).Select(k => Other(k))] },
            "more than the zone keeps" => state with { RecentBatches = [.. Enumerable.Range(0, 7).Select(k => Other(k, $"S-{80 + k}"))] },
            "more than any zone keeps" => state with { RecentBatches = [.. Enumerable.Range(0, ZoneProcessorSettings.MaxRememberedBatchesLimit + 1).Select(k => Other(k % 100, $"S-{k / 100}"))] },
            _ => state with { RecentBatches = [first, null] }
        };

        var restore = () => ZoneProcessor.Restore(ZoneKey, Geometry, 3, settings, changed, Far);

        restore.Should().Throw<InvalidDataException>(bad);
    }

    #endregion

    #region Settings

    public static TheoryData<int, int, bool> BoundSettings => new()
    {
        { 20_000, 1_000, true },
        { 1, 1, true },
        { 200_000, 200_000, true },
        { 0, 1, false },
        { 200_001, 1_000, false },
        { 1_000, 0, false },
        { 1_000, 1_001, false }
    };

    [Theory]
    [MemberData(nameof(BoundSettings))]
    public void Settings_Should_BoundTheRememberedBatches_When_Validated(int max, int perDevice, bool valid)
    {
        var settings = new ZoneProcessorSettings { MaxRememberedBatches = max, MaxRememberedBatchesPerDevice = perDevice };

        settings.Problems().Any(p => p.Contains("RememberedBatches", StringComparison.Ordinal)).Should().Be(!valid);
    }

    #endregion
}
