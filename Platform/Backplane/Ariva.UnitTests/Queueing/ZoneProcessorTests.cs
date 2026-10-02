using System.Text.Json;
using Ariva.Core.Desks;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Queueing;
using Ariva.Core.Sensing;
using Ariva.Infra.Messaging;
using Ariva.Infra.Sensing;
using Ariva.Simulation.Api.Emulators.Sensors;
using Ariva.Simulation.Api.Scenarios.Engine;
using FluentAssertions;

namespace Ariva.UnitTests.Queueing;

/// <summary>
/// ARV-034: a queue zone's stream processing over the reference evening (the emulator's S-15 traffic through Ingest's
/// mapper as sensing batches), and the snapshots that let a stream worker move a zone to another instance: restoring
/// from JSON at any point and continuing gives exactly the outputs of an uninterrupted run, for the zone, the queue
/// engine and the desk engine.
/// </summary>
public sealed class ZoneProcessorTests
{
    private const string ZoneKey = "DMO/A-VIS";
    private static readonly DateTime Midnight = new(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);
    private static readonly QueueZoneGeometry Geometry = new("A-VIS", new HashSet<string> { "A-VIS entry" }, new HashSet<string> { "A-VIS exit" },
        new HashSet<string>(), new HashSet<string>());
    private static readonly Lazy<IReadOnlyList<SensingBatch>> Evening = new(BuildEvening);

    private static DateTime WallOf(double minute) => Midnight.AddMinutes(minute);

    // One minute of S-15 pushes from 17:00 to 19:30 as Ingest would publish them: a crossing and an occupancy batch per minute.
    private static List<SensingBatch> BuildEvening()
    {
        var day = ScenarioDay.Run(ScenarioConfig.Reference() with { Lite = true });
        var sensor = SensorTraffic.Sensor("S-15");
        var batches = new List<SensingBatch>();
        for (var minute = 1020; minute < 1170; minute++)
        {
            var received = WallOf(minute + 1);
            var push = SensorTraffic.Build(day, sensor, EmulatedDialect.Canonical, minute, minute, WallOf, received);
            using var document = JsonDocument.Parse(push.Json);
            var mapped = CanonicalPushMapper.Map(document.RootElement, 3000);
            T Stamp<T>(T batch) where T : SensingBatch
            {
                batch.DeviceId = Guid.Parse("00000000-0000-0000-0000-000000000015");
                batch.DeviceCode = "S-15";
                batch.SiteCode = "DMO";
                batch.QueueZoneName = "A-VIS";
                batch.Dialect = "canonical";
                batch.Commissioned = true;
                batch.ReceivedUtc = received;
                batch.Clock = new ClockReading(0, true, ClockState.Ok);
                return batch;
            }

            if (mapped.Crossings.Count > 0)
                batches.Add(Stamp(new VendorLineCrossingBatch { Crossings = [.. mapped.Crossings.Select(c => new Sensed<LineCrossing>(c, c.TimeUtc, SensedFlags.None))] }));
            if (mapped.Occupancy.Count > 0)
                batches.Add(Stamp(new ZoneOccupancyBatch { Occupancy = [.. mapped.Occupancy.Select(o => new Sensed<ZoneOccupancy>(o, o.TimeUtc, SensedFlags.None))] }));
        }

        return batches;
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, EventCatalog.Json);

    private static T RoundTrip<T>(T value) => JsonSerializer.Deserialize<T>(Json(value), EventCatalog.Json);

    private static List<ZoneOutputs> Run(IEnumerable<int> cuts)
    {
        var cutSet = cuts.ToHashSet();
        var zone = new ZoneProcessor(ZoneKey, Geometry, 3);
        var outputs = new List<ZoneOutputs>();
        var batches = Evening.Value;
        for (var k = 0; k < batches.Count; k++)
        {
            zone.Offer(batches[k], DateTime.MaxValue);
            if (cutSet.Contains(k))
            {
                outputs.Add(zone.Drain());
                zone = ZoneProcessor.Restore(ZoneKey, Geometry, 3, new ZoneProcessorSettings(), RoundTrip(zone.Capture()), WallOf(1440));
            }
        }

        zone.Tick(WallOf(1200));
        outputs.Add(zone.Drain());
        return outputs;
    }

    private static string Flatten(IEnumerable<ZoneOutputs> outputs)
    {
        var all = outputs.ToList();
        return Json(new
        {
            Minutes = all.SelectMany(o => o.Minutes).ToList(),
            Bins = all.SelectMany(o => o.Bins).ToList(),
            Live = all.SelectMany(o => o.Live).ToList(),
            Recomputations = all.SelectMany(o => o.Recomputations).ToList()
        });
    }

    [Fact]
    public void Zone_Should_GiveTheSameOutputs_When_RestoredFromJsonMidStream()
    {
        var straight = Flatten(Run([]));
        var batches = Evening.Value.Count;

        Flatten(Run([10, 60, 61, batches / 2, batches - 2])).Should().Be(straight);
        Flatten(Run(Enumerable.Range(0, batches))).Should().Be(straight, "a snapshot after every batch changes nothing");
    }

    [Fact]
    public void Zone_Should_PublishTheVisitorsWaveLive_When_TheEveningIsProcessed()
    {
        var day = ScenarioDay.Run(ScenarioConfig.Reference() with { Lite = true });
        var live = Run([]).SelectMany(o => o.Live).ToDictionary(l => l.MinuteUtc);
        var q = ScenarioModel.Q("A-VIS");

        foreach (var minute in new[] { 1085, 1086, 1090 })
            live[WallOf(minute)].NowcastMinutes.Should().BeApproximately(day.State(q, minute).Nowcast!.Value, 2.5, "minute {0}", ScenarioMath.Clock(minute));
        live.Values.Where(l => l.MinuteUtc < WallOf(1080)).Should().OnlyContain(l => l.NowcastMinutes == null || l.NowcastMinutes <= 15);
        live.Keys.Should().BeInAscendingOrder();
        live[WallOf(1100)].LengthMeasured.Should().BeTrue("the queue zone reports its occupancy every minute");
    }

    [Fact]
    public void Zone_Should_FinaliseItsBins_When_TheEveningIsProcessed()
    {
        var outputs = Run([]);
        var finals = outputs.SelectMany(o => o.Bins).Where(b => b.Status == BinStatus.Final).ToList();

        finals.Select(b => b.StartUtc).Should().OnlyHaveUniqueItems();
        finals.Should().Contain(b => b.StartUtc == WallOf(1080) && b.Waits.P90Minutes > 10, "the 18:00 bin carries the Visitors wave");
        finals.Should().OnlyContain(b => b.ZoneProfileVersion == 3);
    }

    [Fact]
    public void Zone_Should_RefuseBatches_When_TheyAreNotForItOrNotCommissioned()
    {
        var zone = new ZoneProcessor(ZoneKey, Geometry, 1);
        var source = (VendorLineCrossingBatch)Evening.Value.First(b => b is VendorLineCrossingBatch);
        VendorLineCrossingBatch Copy(Action<VendorLineCrossingBatch> change)
        {
            var copy = RoundTrip(source);
            change(copy);
            return copy;
        }

        zone.Offer(Copy(b => b.QueueZoneName = "A-CIT"), DateTime.MaxValue);
        zone.Offer(Copy(b => b.Commissioned = false), DateTime.MaxValue);
        zone.Offer(Copy(b => b.DeviceCode = "S/15"), DateTime.MaxValue);
        zone.Offer(Copy(b => b.DeviceCode = new string('S', 17)), DateTime.MaxValue);
        zone.Offer(Copy(b => b.Crossings = [new Sensed<LineCrossing>(new LineCrossing(new string('L', 201), CrossingDirection.In, "1", source.ReceivedUtc), source.ReceivedUtc, SensedFlags.None),
            new Sensed<LineCrossing>(new LineCrossing("A-VIS entry", CrossingDirection.In, new string('t', 65), source.ReceivedUtc), source.ReceivedUtc, SensedFlags.None),
            new Sensed<LineCrossing>(null!, source.ReceivedUtc, SensedFlags.None), null!]), DateTime.MaxValue);

        zone.Counters.Should().Be(new ZoneProcessorCounters(Batches: 5, Uncommissioned: 1, WrongZone: 1, Invalid: 6));
        zone.Drain().Minutes.Should().BeEmpty();
    }

    [Fact]
    public void Zone_Should_CapTheReferenceClock_When_AReceiveTimeLiesAhead()
    {
        var zone = new ZoneProcessor(ZoneKey, Geometry, 1);
        var batch = RoundTrip((VendorLineCrossingBatch)Evening.Value.First(b => b is VendorLineCrossingBatch));
        var cap = batch.ReceivedUtc;
        batch.ReceivedUtc = cap.AddYears(1);

        zone.Offer(batch, cap);

        zone.ReferenceUtc.Should().Be(cap);
    }

    [Fact]
    public void Zone_Should_KeepItsOutputs_When_AWriteIsNotAcknowledged()
    {
        var zone = new ZoneProcessor(ZoneKey, Geometry, 1);
        foreach (var batch in Evening.Value.Take(40))
            zone.Offer(batch, DateTime.MaxValue);

        var first = zone.Peek();
        first.Count.Should().BePositive();
        zone.Peek().Should().BeEquivalentTo(first, "a failed or cancelled write took nothing away");
        foreach (var batch in Evening.Value.Skip(40).Take(10))
            zone.Offer(batch, DateTime.MaxValue);
        var second = zone.Peek();
        zone.Acknowledge(first);

        zone.Peek().Count.Should().Be(second.Count - first.Count, "only what was written is removed");
    }

    [Theory]
    [InlineData("0001-01-01T00:30:00Z")]
    [InlineData("9999-12-31T23:59:59Z")]
    [InlineData("1999-12-31T23:59:59Z")]
    public void Zone_Should_CountHostileEventTimesAsInvalid_When_AnyBatchKindCarriesThem(string time)
    {
        var at = DateTime.Parse(time, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
        var zone = new ZoneProcessor(ZoneKey, Geometry, 1);
        var template = (VendorLineCrossingBatch)Evening.Value.First(b => b is VendorLineCrossingBatch);
        var ok = template.ReceivedUtc;
        T Like<T>(T batch) where T : SensingBatch
        {
            batch.DeviceId = template.DeviceId;
            batch.DeviceCode = template.DeviceCode;
            batch.SiteCode = template.SiteCode;
            batch.QueueZoneName = template.QueueZoneName;
            batch.Commissioned = true;
            batch.ReceivedUtc = ok;
            return batch;
        }

        var batches = new SensingBatch[]
        {
            Like(new VendorLineCrossingBatch { Crossings = [new Sensed<LineCrossing>(new LineCrossing("A-VIS entry", CrossingDirection.In, "1", ok), at, SensedFlags.Corrected)] }),
            Like(new ZoneOccupancyBatch { Occupancy = [new Sensed<ZoneOccupancy>(new ZoneOccupancy("A-VIS", 3, ok), at, SensedFlags.None)] }),
            Like(new IntervalCountBatch { Intervals = [new Sensed<IntervalCount>(new IntervalCount("A-VIS entry", 2, 1, ok.AddHours(-1), ok), at, SensedFlags.Corrected)] }),
            Like(new TrackSampleBatch { Samples = [new Sensed<TrackPosition>(new TrackPosition("7", 1, 1, null, ok), at, SensedFlags.None)] }),
            Like(new IntervalCountBatch { Intervals = [new Sensed<IntervalCount>(new IntervalCount("A-VIS entry", 2, 1, at, at.Year > 2000 ? at : ok), ok, SensedFlags.None)] }),
            Like(new VendorLineCrossingBatch { Crossings = [new Sensed<LineCrossing>(new LineCrossing("A-VIS entry", CrossingDirection.In, "1", at), ok, SensedFlags.None)] })
        };

        foreach (var batch in batches)
            zone.Offer(batch, DateTime.MaxValue);
        var hostileReceive = Like(new ZoneOccupancyBatch { Occupancy = [] });
        hostileReceive.ReceivedUtc = at;
        zone.Offer(hostileReceive, DateTime.MaxValue);

        zone.Counters.Invalid.Should().BeGreaterThanOrEqualTo(5);
        zone.ReferenceUtc.Should().Be(ok);
    }

    [Fact]
    public void Restore_Should_RefuseASnapshot_When_ItsValuesAreNotPlausible()
    {
        var zone = new ZoneProcessor(ZoneKey, Geometry, 3);
        foreach (var batch in Evening.Value.Take(60))
            zone.Offer(batch, DateTime.MaxValue);
        zone.Drain();
        var state = RoundTrip(zone.Capture());
        var far = new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        var bin = state.Bins.Open[0];
        ZoneProcessorState[] hostile =
        [
            state with { ZoneKey = null },
            state with { ZoneKey = "DMO/A-CIT" },
            state with { ProfileVersion = 4 },
            state with { Bins = state.Bins with { QueueZone = "A-CIT" } },
            state with { Bins = state.Bins with { ProfileVersion = 99 } },
            state with { ReferenceUtc = far },
            state with { LastLiveMinuteUtc = far },
            state with { Engine = state.Engine with { WatermarkUtc = far } },
            state with { Engine = state.Engine with { Counters = [-1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0] } },
            state with { Bins = state.Bins with { WatermarkUtc = far } },
            state with { Bins = state.Bins with { FinalHighUtc = far } },
            state with { Bins = state.Bins with { Open = [bin with { StartUtc = bin.StartUtc.AddMinutes(1) }] } },
            state with { Bins = state.Bins with { Open = [bin with { Total = bin.Total with { Entries = -5 } }] } },
            state with { Bins = state.Bins with { Open = [bin with { Total = bin.Total with { WithinTarget = bin.Total.Waits + 1 } }] } },
            state with { Bins = state.Bins with { Open = [bin with { Total = bin.Total with { Histogram = [new HistogramBucketState(-1, 1)] } }] } },
            state with { Bins = state.Bins with { Open = [bin with { Total = bin.Total with { Histogram = [new HistogramBucketState(2, 1), new HistogramBucketState(2, 1)] } }] } },
            state with { Bins = state.Bins with { Marks = [new BinMarkState(WallOf(1100), WallOf(1090), BinQuality.Degraded)] } },
            state with { Exits = [new ExitMinuteState(WallOf(1060), -3, 0)] },
            state with { Engine = null }
        ];

        foreach (var bad in hostile)
        {
            Action restore = () => ZoneProcessor.Restore(ZoneKey, Geometry, 3, new ZoneProcessorSettings(), bad, WallOf(1440));
            restore.Should().Throw<InvalidDataException>();
        }

        ZoneProcessor.Restore(ZoneKey, Geometry, 3, new ZoneProcessorSettings(), state, WallOf(1440)).ZoneKey.Should().Be(ZoneKey);
    }

    [Fact]
    public void Restore_Should_RefuseASnapshot_When_ItIsForAnotherZoneOrBeyondTheBounds()
    {
        var zone = new ZoneProcessor(ZoneKey, Geometry, 1);
        zone.Offer(Evening.Value[0], DateTime.MaxValue);
        zone.Drain();
        var state = RoundTrip(zone.Capture());
        var other = new QueueZoneGeometry("A-CIT", new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), new HashSet<string>());

        var notAfter = WallOf(1440);
        Action wrongZone = () => ZoneProcessor.Restore(ZoneKey, other, 1, new ZoneProcessorSettings(), state, notAfter);
        Action wrongVersion = () => ZoneProcessor.Restore(ZoneKey, Geometry, 1, new ZoneProcessorSettings(), state with { Version = 99 }, notAfter);
        var order = state.Engine.Order + 10;
        Action tooMany = () => ZoneProcessor.Restore(ZoneKey, Geometry, 1, new ZoneProcessorSettings { Engine = new QueueEngineSettings { MaxOpenEntrants = 1 } },
            state with { Engine = state.Engine with { Order = order, Held = [.. Enumerable.Range(0, 2).Select(k => new QueueEntrantState(WallOf(1020), null, "", false, WaitMethod.Fifo, k, null))] } }, notAfter);
        Action badInput = () => ZoneProcessor.Restore(ZoneKey, Geometry, 1, new ZoneProcessorSettings(),
            state with { Engine = state.Engine with { Sequence = state.Engine.Sequence + 5, Buffer = [new QueueBufferedState(new QueueInputState("bogus", WallOf(1020), false), false, 1)] } }, notAfter);
        Action twice = () => ZoneProcessor.Restore(ZoneKey, Geometry, 1, new ZoneProcessorSettings(),
            state with { Engine = state.Engine with { Order = order, Held = [new QueueEntrantState(WallOf(1020), "S-15/1", "S-15", false, WaitMethod.Track, 1, null), new QueueEntrantState(WallOf(1020), "S-15/1", "S-15", false, WaitMethod.Track, 2, null)] } }, notAfter);

        foreach (var bad in new[] { wrongZone, wrongVersion, tooMany, badInput, twice })
            bad.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void QueueEngine_Should_ContinueIdentically_When_RestoredAtRandomPoints(int seed)
    {
        // A seeded mulberry32 stream (the scenario engine's): reproducible, and not used for anything secret.
        var stream = new Mulberry32((uint)seed * 2654435761u);
        int Next(int max) => (int)(stream.Next() * max);
        var geometry = new QueueZoneGeometry("Q", new HashSet<string> { "in" }, new HashSet<string> { "out" },
            new HashSet<string> { "ov-in" }, new HashSet<string> { "OV" });
        var settings = new QueueEngineSettings
        {
            Lateness = TimeSpan.FromSeconds(20), MaxOpenEntrants = 30, MaxBufferedEvents = 60, MaxStepRecords = 1_000, HandoverWindow = TimeSpan.FromSeconds(15),
            CensorAfter = TimeSpan.FromMinutes(6), MaxRememberedTracks = 40, MaxDevicesPerZone = 3
        };
        var straight = new QueueStateEngine(geometry, settings);
        var restored = new QueueStateEngine(geometry, settings);
        var t0 = WallOf(1020);
        string[] lines = ["in", "out", "ov-in", "elsewhere"];
        for (var second = 0; second < 2 * 3600; second += 3)
        {
            var now = t0.AddSeconds(second);
            for (var k = Next(4); k > 0; k--)
            {
                var at = now.AddSeconds(-Next(40));
                var track = Next(4) == 0 ? null : $"D{Next(4)}/{Next(30)}";
                QueueInput input = Next(6) switch
                {
                    0 or 1 => new QueueCrossing(lines[Next(4)], Next(5) == 0 ? CrossingDirection.Out : CrossingDirection.In, track, at, Next(9) == 0),
                    2 => new QueueCrossing("out", CrossingDirection.Out, track, at),
                    3 => new QueueOccupancy(Next(2) == 0 ? "Q" : "OV", Next(8), at),
                    4 => new QueueInterval(lines[Next(3)], Next(4), Next(4), at.AddSeconds(-30 - Next(60)), at),
                    _ => new QueueTrackSeen(track, at)
                };
                straight.Offer(input, now);
                restored.Offer(input, now);
            }

            if (Next(5) == 0)
                restored = QueueStateEngine.Restore(geometry, settings, RoundTrip(restored.Capture()));
            if (second % 15 == 0)
                Json(restored.Advance(now)).Should().Be(Json(straight.Advance(now)), "second {0}", second);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void DeskEngine_Should_ContinueIdentically_When_RestoredAtRandomPoints(int seed)
    {
        var stream = new Mulberry32((uint)seed * 2654435761u);
        int Next(int max) => (int)(stream.Next() * max);
        DeskProfile[] desks = [new("A", "VIS", true, true, true, true), new("B", "VIS", false, true, true, false), new("C", "CIT", true, false, false, false)];
        var settings = new DeskStateSettings { Lateness = TimeSpan.FromSeconds(20) };
        var t0 = WallOf(1020);
        var straight = new DeskStateEngine(desks, t0, settings);
        var restored = new DeskStateEngine(desks, t0, settings);
        for (var second = 0; second < 3600; second += 5)
        {
            var now = t0.AddSeconds(second);
            for (var k = Next(3); k > 0; k--)
            {
                var desk = desks[Next(3)].DeskCode;
                var at = now.AddSeconds(-Next(60));
                DeskSignal signal = Next(6) switch
                {
                    0 => new DeskTransactionStarted(desk, at),
                    1 => new DeskTransactionEnded(desk, at),
                    2 => new DeskSessionChangedSignal(desk, at, (DeskSessionSignal)Next(3)),
                    3 => new DeskZoneReading(desk, at, DeskSource.StaffZone, Next(3)),
                    4 => new DeskTransactionsCompleted(desk, at, Next(3)),
                    _ => new DeskHeartbeat(desk, at, (DeskSource)Next(4))
                };
                straight.Offer(signal, now);
                restored.Offer(signal, now);
            }

            if (Next(4) == 0)
                restored = DeskStateEngine.Restore(desks, settings, RoundTrip(restored.Capture()));
            if (second % 20 == 0)
                Json(restored.Advance(now)).Should().Be(Json(straight.Advance(now)), "second {0}", second);
        }

        Json(restored.Counters).Should().Be(Json(straight.Counters));

        var state = RoundTrip(restored.Capture());
        var first = state.Desks[0];
        foreach (var bad in new[] { first with { Ticks = [TimeSpan.TicksPerMinute + 1, 0, 0, 0, 0] }, first with { Ticks = [-1, 0, 0, 0, 0] },
                     first with { Transactions = -1 }, first with { SensorTicks = long.MaxValue }, first with { Ticks = [1, 2] } })
        {
            Action restore = () => DeskStateEngine.Restore(desks, settings, state with { Desks = [bad] });
            restore.Should().Throw<InvalidDataException>();
        }
    }
}
