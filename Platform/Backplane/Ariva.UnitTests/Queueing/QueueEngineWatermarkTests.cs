using System.Text.Json;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Queueing;
using Ariva.Core.Queueing.Replay;
using Ariva.Core.Sensing;
using Ariva.Infra.Messaging;
using Ariva.UnitTests.Replay;
using FluentAssertions;

namespace Ariva.UnitTests.Queueing;

/// <summary>
/// ARV-148: the queue state engine and the zone processor on event time. A zone's watermark is its latest event time less
/// the allowed lateness (each event counted at most up to the receive time it arrived with, and the zone's clock never more
/// than the push interval behind that receive time), so a one-minute push of events up to 61 seconds old loses nothing
/// however Ingest split it into topics and in whatever order those arrive; an event at or behind the watermark is late as
/// before; a device whose clock runs ahead cannot carry the watermark past the receive time (CWE-501); and an outage ends
/// at the start of the minute the device's first push after it covers (F4 of ARV-104j).
/// </summary>
public sealed class QueueEngineWatermarkTests
{
    #region Helpers

    private static readonly DateTime T0 = new(2026, 9, 28, 18, 0, 0, DateTimeKind.Utc);

    private static DateTime At(double seconds) => T0.AddSeconds(seconds);

    private static readonly QueueZoneGeometry Geometry = new(
        "A-VIS",
        new HashSet<string>(StringComparer.Ordinal) { "A-VIS entry" },
        new HashSet<string>(StringComparer.Ordinal) { "A-VIS exit" },
        new HashSet<string>(StringComparer.Ordinal) { "A-OV entry" },
        new HashSet<string>(StringComparer.Ordinal) { "A-OV" });

    private static QueueCrossing In(DateTime at, string track = null) => new("A-VIS entry", CrossingDirection.In, track, at);

    private static QueueCrossing Out(DateTime at, string track = null) => new("A-VIS exit", CrossingDirection.Out, track, at);

    private static T Stamp<T>(T batch, string device, DateTime received) where T : SensingBatch
    {
        batch.Id = Guid.NewGuid();
        batch.DeviceId = Guid.NewGuid();
        batch.DeviceCode = device;
        batch.SiteCode = "DMO";
        batch.QueueZoneName = "A-VIS";
        batch.Dialect = "canonical";
        batch.Commissioned = true;
        batch.ReceivedUtc = received;
        batch.Clock = new ClockReading(0, true, ClockState.Ok);
        return batch;
    }

    private static ZoneOccupancyBatch Reading(string device, string zone, int count, DateTime at, DateTime received) =>
        Stamp(new ZoneOccupancyBatch { Occupancy = [new Sensed<ZoneOccupancy>(new ZoneOccupancy(zone, count, at), at, SensedFlags.None)] }, device, received);

    private static VendorLineCrossingBatch Crossings(string device, DateTime received, params (string Line, CrossingDirection Direction, string Track, DateTime At)[] crossings) =>
        Stamp(new VendorLineCrossingBatch
        {
            Crossings = [.. crossings.Select(c => new Sensed<LineCrossing>(new LineCrossing(c.Line, c.Direction, c.Track, c.At), c.At, SensedFlags.None))]
        }, device, received);

    private static DeviceStatusBatch Heartbeat(string device, DateTime received) => Stamp(new DeviceStatusBatch { Online = true }, device, received);

    private static string Json(object value) => JsonSerializer.Serialize(value, EventCatalog.Json);

    /// <summary>A copy of <paramref name="items"/> in a seeded order (Bogus, not System.Random: CA5394).</summary>
    private static T[] Shuffle<T>(Bogus.Randomizer random, IEnumerable<T> items) => [.. random.Shuffle(items)];

    #endregion

    #region The watermark (formulas F5, ARV-148)

    /// <summary>One case of the watermark rule: an event offered with a reference, then a step at another reference.</summary>
    public sealed record WatermarkCase(string Name, double EventAt, double OfferedAt, double AdvanceAt, double LatenessSeconds, double PushSeconds, double? Expected)
    {
        public override string ToString() => Name;
    }

    public static TheoryData<WatermarkCase> WatermarkCases =>
    [
        // The latest event less the lateness, not the batch's arrival less it.
        new WatermarkCase("the latest event less the lateness", 60, 61, 61, 90, 60, -30),
        new WatermarkCase("a push received a second and a half after its last event", 59.5, 61, 61, 90, 60, -30.5),
        // A zone whose devices send no event moves on with the receive clock less the push interval.
        new WatermarkCase("a silent zone, the receive clock less the push interval", 0, 0, 300, 90, 60, 150),
        new WatermarkCase("a silent zone, still within the push interval", 0, 0, 50, 90, 60, -90),
        // CWE-501: an event ahead of its receive time counts only up to it.
        new WatermarkCase("an event four minutes ahead counts up to its receive time", 240, 0, 0, 90, 60, -90),
        new WatermarkCase("an event ahead, a later step on the receive clock", 240, 0, 120, 90, 60, -30),
        // The clock never runs ahead of the reference the step is given.
        new WatermarkCase("a step at an earlier reference", 60, 61, 30, 90, 60, -60),
        // No push interval: the receive clock less the lateness, the rule before ARV-148.
        new WatermarkCase("no push interval, the receive clock", 0, 40, 40, 30, 0, 10),
        // Nothing before the first event: the zone has nothing to close yet.
        new WatermarkCase("no event yet", double.NaN, 0, 600, 90, 60, null)
    ];

    [Theory]
    [MemberData(nameof(WatermarkCases))]
    public void Advance_Should_MoveTheWatermarkToTheZonesClockLessTheLateness_When_Stepped(WatermarkCase c)
    {
        var engine = new QueueStateEngine(Geometry, new QueueEngineSettings
        {
            Lateness = TimeSpan.FromSeconds(c.LatenessSeconds), PushInterval = TimeSpan.FromSeconds(c.PushSeconds)
        });
        if (!double.IsNaN(c.EventAt))
            engine.Offer(new QueueOccupancy("A-VIS", 3, At(c.EventAt)), At(c.OfferedAt));

        var step = engine.Advance(At(c.AdvanceAt));

        step.WatermarkUtc.Should().Be(c.Expected is { } expected ? At(expected) : DateTime.MinValue, c.Name);
        step.WatermarkUtc.Should().BeOnOrBefore(At(c.AdvanceAt) - TimeSpan.FromSeconds(c.LatenessSeconds), "never past the reference less the lateness");
    }

    public static TheoryData<string, double, bool> BoundaryCases => new()
    {
        { "a tick behind the watermark", -0.0000001, true },
        { "exactly at the watermark, the latest event less the lateness", 0, true },
        { "a tick after the watermark", 0.0000001, false },
        { "a second after the watermark", 1, false }
    };

    [Theory]
    [MemberData(nameof(BoundaryCases))]
    public void Offer_Should_CountAnEventLate_When_ItIsAtOrBehindTheLatestEventLessTheLateness(string because, double afterWatermark, bool late)
    {
        // The latest event 18:02:00, lateness 90 seconds: the watermark is 18:00:30. An event at or before it is late,
        // counted and asked to be recomputed, never applied (as before ARV-148); one after it is applied in order.
        var engine = new QueueStateEngine(Geometry, new QueueEngineSettings { Lateness = TimeSpan.FromSeconds(90) });
        engine.Offer(new QueueOccupancy("A-VIS", 0, At(120)), At(121));
        engine.Advance(At(121)).WatermarkUtc.Should().Be(At(30));

        engine.Offer(In(At(30 + afterWatermark)), At(121));
        var step = engine.Advance(At(200));

        step.Rejections.Late.Should().Be(late ? 1 : 0, because);
        step.Movements.Sum(m => m.Entries).Should().Be(late ? 0 : 1, because);
        if (late)
            step.Rejections.LateByMinute.Should().Equal([(At(0), 1L)], "the late event's minute is reported for a recomputation");
    }

    [Fact]
    public void Offer_Should_KeepEveryCrossingInTime_When_AnotherBatchOfThePushArrivesFirst()
    {
        // F1 of ARV-104j: a push at 18:01:01 carries crossings from 18:00:00 (61 seconds old) and the band's reading of
        // 18:01:00. Ingest splits it into topics; the reading's batch is applied first. On event time the watermark is
        // 18:01:00 less 90 seconds, so the oldest crossing is still in time; on the receive clock with 30 seconds it was
        // late.
        QueueStep Play(QueueEngineSettings settings)
        {
            var engine = new QueueStateEngine(Geometry, settings);
            engine.Offer(new QueueOccupancy("A-OV", 0, At(60)), At(61));
            engine.Advance(At(61));
            foreach (var at in new[] { 0.0, 15, 31, 45, 59.9 })
                engine.Offer(In(At(at)), At(61));
            return engine.Advance(At(300));
        }

        var eventTime = Play(new QueueEngineSettings());
        var receiveClock = Play(new QueueEngineSettings { Lateness = TimeSpan.FromSeconds(30), PushInterval = TimeSpan.Zero });

        eventTime.Rejections.Late.Should().Be(0);
        eventTime.Movements.Sum(m => m.Entries).Should().Be(5);
        receiveClock.Rejections.Late.Should().Be(3, "18:00:00, 18:00:15 and 18:00:31 were behind 18:00:31 on the receive clock");
    }

    [Fact]
    public void Offer_Should_NotLetADeviceAheadMoveTheClock_When_ItsEventsLieInTheFuture()
    {
        // CWE-501: a device whose clock runs 4 minutes ahead (within Ingest's 5) sends readings at 18:05 received at 18:01;
        // they count on the zone's clock only up to 18:01, so the other devices' crossings of 18:00 stay in time.
        var engine = new QueueStateEngine(Geometry, new QueueEngineSettings());
        for (var k = 0; k < 5; k++)
            engine.Offer(new QueueTrackSeen("S-99/x" + k, At(300 + k)), At(61));

        engine.ClockUtc.Should().Be(At(61));
        engine.Advance(At(61)).WatermarkUtc.Should().Be(At(-29));
        engine.Offer(In(At(0.5)), At(61));
        var step = engine.Advance(At(70));

        step.Rejections.Late.Should().Be(0, "the watermark is the receive time less the lateness, not 18:05 less it");
        step.Movements.Sum(m => m.Entries).Should().Be(0, "and 18:00:00.5 is not yet due");
        engine.BufferedEvents.Should().Be(6);
    }

    [Fact]
    public void Advance_Should_ApplyTheEventsOfAnInstantInOneOrder_When_TheyArriveInAnyOrder()
    {
        // ARV-148: events of one instant are applied by their content (interval readings, crossings, readings, track
        // samples; then line or zone, direction, track, interval start, counts and flag), never by arrival, so the batches
        // and topics that carried them cannot change a result. The snapshot lists the held events in that order.
        var at = At(30);
        QueueInput[] canonical =
        [
            new QueueInterval("A-VIS entry", 1, 0, At(0), at),
            new QueueInterval("A-VIS entry", 1, 0, At(10), at),
            new QueueInterval("A-VIS entry", 2, 0, At(10), at),
            new QueueInterval("A-VIS entry", 2, 1, At(10), at),
            new QueueInterval("A-VIS exit", 0, 1, At(0), at),
            new QueueCrossing("A-OV entry", CrossingDirection.In, "S-15/1", at),
            new QueueCrossing("A-VIS entry", CrossingDirection.In, null, at),
            new QueueCrossing("A-VIS entry", CrossingDirection.In, null, at, Degraded: true),
            new QueueCrossing("A-VIS entry", CrossingDirection.In, "S-15/1", at),
            new QueueCrossing("A-VIS entry", CrossingDirection.In, "S-15/2", at),
            new QueueCrossing("A-VIS entry", CrossingDirection.Out, null, at),
            new QueueCrossing("A-VIS exit", CrossingDirection.Out, "S-15/1", at),
            new QueueOccupancy("A-OV", 4, at),
            new QueueOccupancy("A-VIS", 3, at),
            new QueueOccupancy("A-VIS", 7, at),
            new QueueTrackSeen("S-15/1", at),
            new QueueTrackSeen("S-15/2", at)
        ];
        string Held(IEnumerable<QueueInput> arrival)
        {
            var engine = new QueueStateEngine(Geometry, new QueueEngineSettings());
            foreach (var input in arrival)
                engine.Offer(input, at);
            return Json(engine.Capture().Buffer.Select(b => b.Input));
        }

        var expected = Json(canonical.Select(QueueInputStateOf));
        Held(canonical).Should().Be(expected);
        Held(Enumerable.Reverse(canonical)).Should().Be(expected);
        foreach (var seed in new[] { 1, 2, 3 })
            Held(Shuffle(new Bogus.Randomizer(seed), canonical)).Should().Be(expected, "seed {0}", seed);
    }

    private static QueueInputState QueueInputStateOf(QueueInput input)
    {
        var engine = new QueueStateEngine(Geometry, new QueueEngineSettings());
        engine.Offer(input, input.TimeUtc);
        return engine.Capture().Buffer.Single().Input;
    }

    #endregion

    #region The live path: pushes split into topics (ZoneProcessor)

    /// <summary>
    /// The overflow evening of seed 9303 (S-15 and the band's S-25 counting A-VIS, S-17 only reporting its health and out
    /// from 18:20 to 18:30) from 18:00 to 19:00, as Ingest published it, received a second after each minute as the live
    /// emulator's pushes are: every crossing of a push is up to 61 seconds old when it arrives.
    /// </summary>
    private static readonly Lazy<List<SensingBatch>> Evening = new(() =>
    {
        var (batches, health) = ReferenceReplay.OverflowIngested;
        var from = ReferenceReplay.WallOf(1080);
        var to = ReferenceReplay.WallOf(1141);
        return
        [
            .. batches.Where(b => b.ReceivedUtc >= from && b.ReceivedUtc < to).Select(Copy)
                .Concat(health.Where(h => h.ReceivedUtc >= from && h.ReceivedUtc < to).Select(DeviceStatusBatch.From).Select(Copy))
                .Select(b =>
                {
                    b.ReceivedUtc = b.ReceivedUtc.AddSeconds(1);
                    return b;
                })
        ];
    });

    private static SensingBatch Copy(SensingBatch batch) =>
        (SensingBatch)JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(batch, batch.GetType(), EventCatalog.Json), batch.GetType(), EventCatalog.Json)!;

    private static int Rank(SensingBatch b) => b switch
    {
        VendorLineCrossingBatch => 0,
        ZoneOccupancyBatch => 1,
        IntervalCountBatch => 2,
        TrackSampleBatch => 3,
        _ => 4
    };

    /// <summary>As the stream worker merges them: receive time, then topic, then device.</summary>
    private static IEnumerable<SensingBatch> AsTheWorkerMerges(IEnumerable<SensingBatch> batches) =>
        batches.OrderBy(b => b.ReceivedUtc).ThenBy(Rank).ThenBy(b => b.DeviceCode, StringComparer.Ordinal);

    /// <summary>Every event in a batch of its own, each receive time's batches in reverse: health first, readings, then crossings, last device first.</summary>
    private static IEnumerable<SensingBatch> SplitAndReversed(IEnumerable<SensingBatch> batches) =>
        batches.GroupBy(b => b.ReceivedUtc).OrderBy(g => g.Key)
            .SelectMany(g => g.OrderByDescending(Rank).ThenByDescending(b => b.DeviceCode, StringComparer.Ordinal).SelectMany(Split).Reverse());

    /// <summary>Within each receive time, the batches and their events split at random and shuffled.</summary>
    private static IEnumerable<SensingBatch> Shuffled(IEnumerable<SensingBatch> batches, int seed)
    {
        var random = new Bogus.Randomizer(seed);
        return batches.GroupBy(b => b.ReceivedUtc).OrderBy(g => g.Key).ToList()
            .SelectMany(g => Shuffle(random, g.SelectMany(b => random.Bool() ? [b] : Split(b)).ToList()));
    }

    private static IEnumerable<SensingBatch> Split(SensingBatch batch)
    {
        T Like<T>(T part) where T : SensingBatch
        {
            part.Id = Guid.NewGuid();
            part.DeviceId = batch.DeviceId;
            part.DeviceCode = batch.DeviceCode;
            part.SiteCode = batch.SiteCode;
            part.QueueZoneName = batch.QueueZoneName;
            part.Dialect = batch.Dialect;
            part.Commissioned = batch.Commissioned;
            part.ReceivedUtc = batch.ReceivedUtc;
            part.Clock = batch.Clock;
            return part;
        }

        return batch switch
        {
            VendorLineCrossingBatch c => c.Crossings.Reverse().Select(e => (SensingBatch)Like(new VendorLineCrossingBatch { Crossings = [e] })),
            ZoneOccupancyBatch o => o.Occupancy.Reverse().Select(e => (SensingBatch)Like(new ZoneOccupancyBatch { Occupancy = [e] })),
            _ => [batch]
        };
    }

    private static ZoneOutputs Run(IEnumerable<SensingBatch> batches, ZoneProcessorSettings settings = null)
    {
        settings ??= new ZoneProcessorSettings();
        var zone = new ZoneProcessor("DMO/A-VIS", ReferenceReplay.GeometryOf("A-VIS"), ReferenceReplay.ProfileVersion, settings);
        foreach (var batch in batches)
            zone.Offer(batch, DateTime.MaxValue);
        var end = ReferenceReplay.WallOf(1141);
        zone.Finish(end, end + ZoneReplay.Settle(settings));
        return zone.Drain();
    }

    /// <summary>The final result of each bin and minute, each closed line minute, the outages and the late events.</summary>
    private static string Finals(ZoneOutputs outputs) => Json(new
    {
        Bins = outputs.Bins.Where(b => b.Status == BinStatus.Final).GroupBy(b => b.StartUtc).Select(g => g.Last()).OrderBy(b => b.StartUtc).ToList(),
        Minutes = outputs.Minutes.Where(m => m.Status == BinStatus.Final).GroupBy(m => m.StartUtc).Select(g => g.Last()).OrderBy(m => m.StartUtc).ToList(),
        Lines = outputs.Lines.OrderBy(l => l.MinuteUtc).ThenBy(l => l.LineName, StringComparer.Ordinal).ToList(),
        outputs.Outages,
        Late = outputs.Bins.Sum(b => b.LateEvents)
    });

    [Fact]
    public void Zone_Should_GiveTheSameBins_When_ThePushesAreSplitAndReorderedAcrossTopics()
    {
        var merged = Run(AsTheWorkerMerges(Evening.Value));
        var expected = Finals(merged);

        Finals(Run(SplitAndReversed(Evening.Value))).Should().Be(expected, "every event in a batch of its own, the topics in reverse");
        foreach (var seed in new[] { 7, 9303 })
            Finals(Run(Shuffled(Evening.Value, seed))).Should().Be(expected, "split and shuffled within each receive time, seed {0}", seed);

        merged.Bins.Where(b => b.Status == BinStatus.Final).Should().HaveCountGreaterThan(4);
        merged.Bins.Sum(b => b.LateEvents).Should().Be(0);
        merged.Recomputations.Should().BeEmpty();
    }

    [Fact]
    public void Zone_Should_GiveTheSameResults_When_TheEventsOfOneInstantArriveInEitherOrder()
    {
        // Two devices' pushes received together: S-15's entry and S-16's exit at the same instant 18:00:30, and S-50's
        // minute count of 18:01 (an interval reading ending 18:02:00) beside S-15's reading of the queue at 18:02:00. The
        // engine applies an instant's events by their content, so the exit always meets the entry, and the interval is
        // spread over its minute before the reading of its end is applied (in arrival order the reading first would have
        // put all four people at 18:02:00, in the next minute).
        var at = At(30);
        SensingBatch[] Batches() =>
        [
            Crossings("S-15", At(61), ("A-VIS entry", CrossingDirection.In, null, at)),
            Crossings("S-16", At(61), ("A-VIS exit", CrossingDirection.Out, null, at)),
            Stamp(new IntervalCountBatch
            {
                Intervals = [new Sensed<IntervalCount>(new IntervalCount("A-VIS entry", 4, 0, At(60), At(120)), At(120), SensedFlags.None)]
            }, "S-50", At(121)),
            Reading("S-15", "A-VIS", 4, At(120), At(121))
        ];
        ZoneOutputs Play(Func<SensingBatch[], IEnumerable<SensingBatch>> order)
        {
            var zone = new ZoneProcessor("DMO/A-VIS", Geometry, 12);
            foreach (var batch in order(Batches()))
                zone.Offer(batch, DateTime.MaxValue);
            zone.Tick(At(600));
            return zone.Drain();
        }

        // The four counted people are still queueing at the end, so the bin is provisional: compare the latest results.
        static string Latest(ZoneOutputs o) => Json(new
        {
            Bins = o.Bins.GroupBy(b => b.StartUtc).Select(g => g.Last()).OrderBy(b => b.StartUtc).ToList(),
            Minutes = o.Minutes.GroupBy(m => m.StartUtc).Select(g => g.Last()).OrderBy(m => m.StartUtc).ToList(),
            Lines = o.Lines.OrderBy(l => l.MinuteUtc).ThenBy(l => l.LineName, StringComparer.Ordinal).ToList()
        });

        var forward = Play(b => b);
        var backward = Play(b => [b[1], b[0], b[3], b[2]]);

        Latest(backward).Should().Be(Latest(forward));
        // The minutes from 18:00 (the zone's first watermark also publishes the empty minute of the 17:45 bin before it).
        forward.Minutes.Where(m => m.StartUtc >= T0).GroupBy(m => m.StartUtc).Select(g => (g.Key, g.Last().Entries, g.Last().Exits))
            .Should().Equal((T0, 1L, 1L), (T0.AddMinutes(1), 4L, 0L));
        forward.Bins.Last(b => b.StartUtc == T0).Waits.Waits.Should().Be(1, "the exit met the entry of the same instant");
    }

    [Fact]
    public void Zone_Should_LoseNoCrossingAsLate_When_AMinutePushCarriesEventsUpTo61SecondsOld()
    {
        // F1 of ARV-104j: every crossing is counted on its line and as an entry or exit, whatever batch of a push arrives
        // first; on the receive clock with 30 seconds of lateness the same pushes lost crossings as late.
        var crossings = Evening.Value.OfType<VendorLineCrossingBatch>().SelectMany(c => c.Crossings).ToList();
        var entries = crossings.Count(c => c.Event.LineName == "A-VIS entry");
        var exits = crossings.Count(c => c.Event.LineName == "A-VIS exit");
        var readingsFirst = Evening.Value.GroupBy(b => b.ReceivedUtc).OrderBy(g => g.Key).SelectMany(g => g.OrderByDescending(Rank)).ToList();

        var outputs = Run(readingsFirst);
        var receiveClock = Run(readingsFirst, new ZoneProcessorSettings { Engine = new QueueEngineSettings { Lateness = TimeSpan.FromSeconds(30), PushInterval = TimeSpan.Zero } });

        outputs.Bins.Sum(b => b.LateEvents).Should().Be(0);
        outputs.Recomputations.Should().BeEmpty();
        outputs.Lines.Where(l => l.LineName == "A-VIS entry").Sum(l => l.In).Should().Be(entries).And.BePositive();
        outputs.Lines.Where(l => l.LineName == "A-VIS exit").Sum(l => l.Out).Should().Be(exits).And.BePositive();
        receiveClock.Lines.Where(l => l.LineName == "A-VIS entry").Sum(l => l.In).Should().BeLessThan(entries,
            "the receive clock turned the older crossings of a push late (the finding)");
    }

    [Fact]
    public void Zone_Should_KeepItsWatermarkBehindTheReceiveTime_When_ADeviceClockRunsFarAhead()
    {
        // CWE-501: S-99's clock runs 4 minutes ahead; its track samples are within Ingest's 5 minutes, so they reach the
        // engine. They never carry the zone's watermark past the receive time less the lateness, so no other device's
        // events turn late and every bin closes as it would without S-99.
        var hostile = Evening.Value.Where(b => b.DeviceCode == "S-15").Select(b => b.ReceivedUtc).Distinct()
            .Select(received => (SensingBatch)Stamp(new TrackSampleBatch
            {
                Samples = [new Sensed<TrackPosition>(new TrackPosition("S-99/ahead", 1, 1, 1.7, received.AddMinutes(4)), received.AddMinutes(4), SensedFlags.None)]
            }, "S-99", received))
            .ToList();
        var zone = new ZoneProcessor("DMO/A-VIS", ReferenceReplay.GeometryOf("A-VIS"), ReferenceReplay.ProfileVersion);
        foreach (var batch in AsTheWorkerMerges([.. hostile, .. Evening.Value]))
        {
            zone.Offer(batch, DateTime.MaxValue);
            zone.WatermarkUtc.Should().BeOnOrBefore(zone.ReferenceUtc - new QueueEngineSettings().Lateness, "a clock far ahead never moves the watermark");
        }

        var end = ReferenceReplay.WallOf(1141);
        zone.Finish(end, end + ZoneReplay.Settle(new ZoneProcessorSettings()));
        var outputs = zone.Drain();

        outputs.Bins.Sum(b => b.LateEvents).Should().Be(0);
        Finals(outputs).Should().Be(Finals(Run(AsTheWorkerMerges(Evening.Value))), "the bins are those of the zone without S-99");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Zone_Should_CountAnEventAtMostUpToItsOwnBatchsReceiveTime_When_TheHostTickedTheZoneOnBefore(bool ticked)
    {
        // CWE-501: an event moves the zone's clock at most to the receive time of the push that carried it, never to a
        // later time the host ticked the idle zone on to while that batch waited in Kafka. S-16's push received at
        // 18:01:10 says 18:01:50 (its clock runs ahead): the clock goes to 18:01:10 whether the host ticked the zone to
        // 18:02:00 first or not, so the live path gives what a replay of the records gives.
        var zone = new ZoneProcessor("DMO/A-VIS", Geometry, 12);
        zone.Offer(Reading("S-15", "A-VIS", 2, At(60), At(60)), DateTime.MaxValue);
        if (ticked)
            zone.Tick(At(120));
        zone.Offer(Reading("S-16", "A-VIS", 3, At(110), At(70)), DateTime.MaxValue);

        zone.Capture().Engine.ClockUtc.Should().Be(At(70), "the batch's own receive time, not the zone's latest one");
        zone.ReferenceUtc.Should().Be(ticked ? At(120) : At(70), "the zone's receive clock itself never goes back");
    }

    [Fact]
    public void Zone_Should_EndTheOutageAtTheMinuteThePushCovers_When_ADeviceIsHeardAgain()
    {
        // F4 of ARV-104j: S-17 last pushes for 18:19 (received 18:20:01) and next for 18:30 (received 18:31:01): the outage
        // is 18:20 to 18:30, so the 18:15 bin is Degraded and the 18:30 bin is judged (Good).
        var outputs = Run(AsTheWorkerMerges(Evening.Value));

        outputs.Outages.Should().Equal(new DeviceOutage("DMO/A-VIS", "S-17", ReferenceReplay.WallOf(1100), ReferenceReplay.WallOf(1110)));
        var finals = outputs.Bins.Where(b => b.Status == BinStatus.Final).GroupBy(b => b.StartUtc).ToDictionary(g => g.Key, g => g.Last().Quality);
        finals[ReferenceReplay.WallOf(1095)].Should().Be(BinQuality.Degraded);
        finals[ReferenceReplay.WallOf(1110)].Should().Be(BinQuality.Good);
        finals[ReferenceReplay.WallOf(1080)].Should().Be(BinQuality.Good);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Zone_Should_RecordTheSameOutage_When_TheSilentDevicesRecordComesFirstOrLastOfItsReceiveTime(bool silentFirst)
    {
        // A device silent for four minutes and heard again: the zone observes its devices once per receive time, before
        // any record of it, so the silence is an outage whether its record is the first of that receive time or not.
        // S-17 is last heard at 18:02:01; at 18:05:01 its silence is exactly the limit, so only the receive time of 18:06:01,
        // which also carries S-17's next push, finds it out.
        var zone = new ZoneProcessor("DMO/A-VIS", Geometry, 12);
        for (var m = 0; m <= 8; m++)
        {
            var received = At(60 * m + 1);
            var reading = Reading("S-15", "A-VIS", 2, At(60 * m), received);
            var heard = m is <= 2 or >= 6;
            if (heard && silentFirst)
                zone.Offer(Heartbeat("S-17", received), DateTime.MaxValue);
            zone.Offer(reading, DateTime.MaxValue);
            if (heard && !silentFirst)
                zone.Offer(Heartbeat("S-17", received), DateTime.MaxValue);
        }

        zone.Drain().Outages.Should().Equal([new DeviceOutage("DMO/A-VIS", "S-17", At(120), At(300))],
            "last heard 18:02:01, heard again 18:06:01 with its push for 18:05");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Zone_Should_KeepABinProvisional_When_AnOutageMinuteInItIsNotKnownYet(bool deviceFirst)
    {
        // A bin is final only once the receive clock less the push interval has passed its end, so a device heard again
        // later still marks it. With a lateness below the push interval (no lateness here; the stream refuses such
        // settings, where the watermark alone already waits) the watermark passes 18:15 at 18:15:30, before S-17's push of
        // 18:16:10 shows it was out through 18:14: the 18:00 bin waits and takes the mark, whichever record comes first.
        var settings = new ZoneProcessorSettings { Engine = new QueueEngineSettings { Lateness = TimeSpan.Zero } };
        var zone = new ZoneProcessor("DMO/A-VIS", Geometry, 12, settings);
        for (var m = 0; m <= 15; m++)
        {
            if (m <= 11)
                zone.Offer(Heartbeat("S-17", At(60 * m + 10)), DateTime.MaxValue);
            zone.Offer(Reading("S-15", "A-VIS", 2, At(60 * m + 30), At(60 * m + 30)), DateTime.MaxValue);
        }

        zone.WatermarkUtc.Should().Be(At(930), "the latest reading, 18:15:30, with no lateness");
        zone.Drain().Bins.Where(b => b.StartUtc == T0).Should().OnlyContain(b => b.Status == BinStatus.Provisional, "18:15 is not yet known for S-17");

        var reading = Reading("S-15", "A-VIS", 2, At(970), At(970));
        var heard = Heartbeat("S-17", At(970));
        foreach (var batch in deviceFirst ? new SensingBatch[] { heard, reading } : [reading, heard])
            zone.Offer(batch, DateTime.MaxValue);
        var outputs = zone.Drain();

        outputs.Outages.Should().Equal([new DeviceOutage("DMO/A-VIS", "S-17", At(660), At(900))], "silent from 18:11:10, its push of 18:16:10 covers 18:15");
        outputs.Bins.Where(b => b.StartUtc == T0).Should().ContainSingle().Which.Should().Match<BinResult>(b => b.Status == BinStatus.Final && b.Quality == BinQuality.Degraded);
        outputs.Recomputations.Should().BeEmpty("the bin took its mark before it was final");
    }

    [Fact]
    public void Zone_Should_CountALateEventAndAskForARecomputation_When_ItArrivesBehindTheWatermark()
    {
        // An event older than the watermark is late exactly as before ARV-148: counted in its bin, and once the bin is
        // final a recomputation is asked for.
        // Someone enters at 18:05 and is held until 18:30, so the 18:00 bin is provisional when a crossing of 18:05:10 arrives
        // at 18:20:01, far behind the watermark (18:20:00 less 90 seconds): it is counted in the bin, not applied, and the
        // bin asks for a recomputation once it is final.
        var zone = new ZoneProcessor("DMO/A-VIS", Geometry, 12);
        for (var m = 0; m <= 40; m++)
        {
            zone.Offer(Reading("S-15", "A-VIS", 1, At(60 * m), At(60 * m + 1)), DateTime.MaxValue);
            if (m == 6)
                zone.Offer(Crossings("S-15", At(60 * m + 1), ("A-VIS entry", CrossingDirection.In, null, At(300))), DateTime.MaxValue);
            if (m == 20)
                zone.Offer(Crossings("S-16", At(60 * m + 1), ("A-VIS entry", CrossingDirection.In, null, At(310))), DateTime.MaxValue);
            if (m == 31)
                zone.Offer(Crossings("S-15", At(60 * m + 1), ("A-VIS exit", CrossingDirection.Out, null, At(1800))), DateTime.MaxValue);
        }

        zone.Tick(At(3600));
        var outputs = zone.Drain();

        outputs.Bins.Should().Contain(b => b.StartUtc == T0 && b.Status == BinStatus.Final && b.LateEvents == 1 && b.Entries == 1);
        outputs.Recomputations.Should().ContainSingle().Which.Should().Match<RecomputationRequest>(r => r.FromUtc == T0 && r.LateEvents == 1);
    }

    [Fact]
    public void Zone_Should_ContinueIdentically_When_RestoredMidPush()
    {
        // The engine's clock is part of the snapshot (zone version 9): a zone restored between two batches of one push
        // steps exactly as one that was not.
        var batches = AsTheWorkerMerges(Evening.Value).ToList();
        var straight = Run(batches);
        var settings = new ZoneProcessorSettings();
        var zone = new ZoneProcessor("DMO/A-VIS", ReferenceReplay.GeometryOf("A-VIS"), ReferenceReplay.ProfileVersion, settings);
        var outputs = new List<ZoneOutputs>();
        for (var k = 0; k < batches.Count; k++)
        {
            zone.Offer(batches[k], DateTime.MaxValue);
            if (k % 7 != 3)
                continue;
            outputs.Add(zone.Drain());
            var state = JsonSerializer.Deserialize<ZoneProcessorState>(Json(zone.Capture()), EventCatalog.Json)!;
            state.Engine.ClockUtc.Should().Be(zone.Capture().Engine.ClockUtc);
            zone = ZoneProcessor.Restore("DMO/A-VIS", ReferenceReplay.GeometryOf("A-VIS"), ReferenceReplay.ProfileVersion, settings, state, ReferenceReplay.WallOf(2 * 1440));
        }

        var end = ReferenceReplay.WallOf(1141);
        zone.Finish(end, end + ZoneReplay.Settle(settings));
        outputs.Add(zone.Drain());

        Json(outputs.SelectMany(o => o.Bins)).Should().Be(Json(straight.Bins));
        Json(outputs.SelectMany(o => o.Live)).Should().Be(Json(straight.Live));
        Json(outputs.SelectMany(o => o.Lines)).Should().Be(Json(straight.Lines));
    }

    #endregion

    #region Snapshots (CWE-501)

    [Fact]
    public void Restore_Should_RefuseTheClock_When_ItIsAheadOfTheZoneOrBehindTheEventsItHolds()
    {
        var zone = new ZoneProcessor("DMO/A-VIS", Geometry, 12);
        zone.Offer(Reading("S-15", "A-VIS", 2, At(60), At(61)), DateTime.MaxValue);
        zone.Offer(Crossings("S-15", At(62), ("A-VIS entry", CrossingDirection.In, null, At(61.5))), DateTime.MaxValue);
        var state = zone.Capture();
        var notAfter = At(86_400);
        state.Engine.ClockUtc.Should().Be(At(61.5));
        state.Engine.Buffer.Should().NotBeEmpty();

        ZoneProcessor.Restore("DMO/A-VIS", Geometry, 12, new ZoneProcessorSettings(), state, notAfter).Capture().Engine.ClockUtc.Should().Be(At(61.5));

        // A zone that has stepped past everything it held: the watermark moved (18:09:00 less the push interval and the
        // lateness), the buffer is empty, and the clock stays at the latest event.
        zone.Tick(At(600));
        var stepped = zone.Capture();
        stepped.Engine.Buffer.Should().BeEmpty();
        stepped.Engine.WatermarkUtc.Should().Be(At(450));
        stepped.Engine.ClockUtc.Should().Be(At(61.5));
        ZoneProcessor.Restore("DMO/A-VIS", Geometry, 12, new ZoneProcessorSettings(), stepped, notAfter).Capture().Engine.ClockUtc.Should().Be(At(61.5));

        foreach (var (bad, because) in new[]
                 {
                     (state with { Engine = state.Engine with { ClockUtc = At(63) } }, "the clock is ahead of the zone's receive clock"),
                     (state with { Engine = state.Engine with { ClockUtc = At(30) } }, "the clock is behind an event it holds"),
                     (state with { Engine = state.Engine with { ClockUtc = new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc) } }, "the clock is not plausible"),
                     (stepped with { Engine = stepped.Engine with { ClockUtc = DateTime.MinValue } }, "the clock is unset although the watermark moved")
                 })
        {
            var restore = () => ZoneProcessor.Restore("DMO/A-VIS", Geometry, 12, new ZoneProcessorSettings(), bad, notAfter);
            restore.Should().Throw<InvalidDataException>(because);
        }

        // The engine refuses it on its own too, without the zone's snapshot checks in front of it.
        var unset = () => QueueStateEngine.Restore(Geometry, new QueueEngineSettings(), stepped.Engine with { ClockUtc = DateTime.MinValue });
        unset.Should().Throw<InvalidDataException>().WithMessage("*watermark but no clock*");
        QueueStateEngine.Restore(Geometry, new QueueEngineSettings(), stepped.Engine).ClockUtc.Should().Be(At(61.5));
    }

    [Fact]
    public void Restore_Should_StartTheClockAtTheLatestHeldEvent_When_TheSnapshotWasWrittenBeforeTheClock()
    {
        // A zone snapshot of version 8 has no clock: the engine restores it at the latest of its watermark and the events it
        // holds (they moved the clock when they arrived), and continues from there.
        var zone = new ZoneProcessor("DMO/A-VIS", Geometry, 12);
        zone.Offer(Reading("S-15", "A-VIS", 2, At(60), At(61)), DateTime.MaxValue);
        var json = JsonSerializer.SerializeToNode(zone.Capture() with { Version = 8 }, EventCatalog.Json)!.AsObject();
        json["engine"]!.AsObject().Remove("clockUtc").Should().BeTrue("a snapshot written now names the clock");
        json.Remove("recentBatches").Should().BeTrue("so do the remembered batches, which version 8 never carried (ARV-168)");
        var older = json.Deserialize<ZoneProcessorState>(EventCatalog.Json)!;

        var restored = ZoneProcessor.Restore("DMO/A-VIS", Geometry, 12, new ZoneProcessorSettings(), older, At(86_400));

        older.Engine.ClockUtc.Should().BeNull();
        restored.Capture().Engine.ClockUtc.Should().Be(At(60));
        restored.Capture().Version.Should().Be(ZoneProcessorState.CurrentVersion);
    }

    #endregion
}
