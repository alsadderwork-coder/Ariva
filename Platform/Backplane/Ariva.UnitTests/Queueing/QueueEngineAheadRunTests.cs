using System.Text.Json;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Queueing;
using Ariva.Core.Sensing;
using Ariva.Infra.Messaging;
using Ariva.Simulation.Api.Scenarios.Engine;
using FluentAssertions;

namespace Ariva.UnitTests.Queueing;

/// <summary>
/// ARV-239 (CWE-400, CWE-501): position samples a device stamps after Ariva's receive clock (a clock up to Ingest's 500 ms
/// tolerance fast, or one running further ahead) are coalesced in their track's run like the others, so a hall zone of fast
/// sensors keeps every crossing and reading; each device holds at most its share of the tenth of the buffer kept for events ahead
/// of the clock, so a device minutes ahead cannot take that tenth from the others; the runs' samples ahead of the clock travel in
/// the snapshot (zone snapshot version 13) and are checked against the clock on restore.
/// </summary>
public sealed class QueueEngineAheadRunTests
{
    #region Fields

    private static readonly DateTime Midnight = new(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc);

    private static readonly QueueZoneGeometry Hall = new("Q", new HashSet<string> { "in" }, new HashSet<string> { "out" },
        new HashSet<string> { "ov-in" }, new HashSet<string> { "OV" });

    #endregion

    #region Tests

    [Fact]
    public void HallZone_Should_KeepEveryCrossingAndReading_When_TwentySensorsRunHalfASecondFast()
    {
        // ARV-230's hall zone (20 T3 sensors, 30 people each, 5 Hz, 10 minutes), every sensor's clock 500 ms fast: the last
        // two or three samples of each push, and some crossings and readings, are stamped after the push's receive time. Before
        // ARV-239 each of those samples took a place of the tenth kept for events ahead of the clock for the lateness (about
        // 1,200 a second against 10,000 places), and the zone refused events ahead of the clock, crossings included.
        const int Sensors = 20, People = 30, Hertz = 5, WalkSeconds = 300, RunSeconds = 600;
        var fast = TimeSpan.FromMilliseconds(500);
        var engine = new QueueStateEngine(Hall, new QueueEngineSettings()) { ZoneDevices = Sensors };
        long bufferFull = 0, shareFull = 0, late = 0, forced = 0, otherRefusals = 0;
        long sentIn = 0, sentOut = 0, ahead = 0, countedIn = 0, countedOut = 0, waits = 0, samples = 0, largestHeld = 0;
        var t0 = Midnight.AddHours(18);
        var entered = new HashSet<string>(StringComparer.Ordinal);

        for (var second = 1; second <= RunSeconds; second++)
        {
            var now = t0.AddSeconds(second);
            for (var sensor = 0; sensor < Sensors; sensor++)
            {
                var device = $"S{sensor}";
                var push = new List<QueueInput>();
                for (var person = 0; person < People; person++)
                {
                    // Walks start on each of the five samples of a second, so some crossings are ahead of the clock too.
                    var offset = ((((person * WalkSeconds / People) + (sensor * 7)) % WalkSeconds) * Hertz) + (person % Hertz);
                    for (var s = 0; s < Hertz; s++)
                    {
                        var sample = ((second - 1) * Hertz) + s;
                        var at = t0.AddMilliseconds(sample * 1000 / Hertz) + fast;
                        var walked = sample + offset;
                        var walk = walked / (WalkSeconds * Hertz);
                        var key = $"{device}/{person}-{walk}";
                        if (walked % (WalkSeconds * Hertz) == 0 && walk > 0)
                        {
                            var previous = $"{device}/{person}-{walk - 1}";
                            if (entered.Remove(previous))
                            {
                                push.Add(new QueueCrossing("out", CrossingDirection.Out, previous, at));
                                sentOut++;
                            }

                            push.Add(new QueueCrossing("in", CrossingDirection.In, key, at));
                            entered.Add(key);
                            sentIn++;
                        }

                        push.Add(new QueueTrackSeen(key, at));
                    }
                }

                if (sensor == 0)
                    push.Add(new QueueOccupancy("Q", Sensors * People, now + fast));

                // The zone processor offers a push's samples in time order (ARV-230).
                foreach (var input in push.OrderBy(i => i.TimeUtc))
                {
                    if (input.TimeUtc > now)
                        ahead++;
                    engine.Offer(input, now, device);
                }
            }

            Tally(engine.Advance(now));
            largestHeld = Math.Max(largestHeld, engine.BufferedEvents);
        }

        Tally(engine.Advance(t0.AddSeconds(RunSeconds + 600)));
        var summary = $"entries {sentIn}, exits {sentOut}, events ahead of the clock {ahead}; counted {countedIn} and {countedOut}; " +
            $"refused {bufferFull} for a full buffer, {shareFull} past a share; late {late}; forced {forced}; other {otherRefusals}; largest held {largestHeld}";
        TestContext.Current.TestOutputHelper?.WriteLine(summary);
        ahead.Should().BeGreaterThan(600 * 1_000, "the latest samples of each push are ahead of the receive clock");
        (bufferFull, shareFull, late, forced, otherRefusals).Should().Be((0L, 0L, 0L, 0L, 0L), "no event is refused, late, applied early or invalid ({0})", summary);
        countedIn.Should().Be(sentIn, "every entry crossing is counted");
        countedOut.Should().Be(sentOut, "every exit crossing is counted");
        waits.Should().Be(sentOut, "each exit closes its own track's wait");
        samples.Should().BeGreaterThan(0, "the occupancy readings give samples");
        largestHeld.Should().BeLessThan(10_000, "the runs hold a few places a track, ahead of the clock or not");

        void Tally(QueueStep step)
        {
            var r = step.Rejections;
            (bufferFull, shareFull, late, forced) = (bufferFull + r.BufferFull, shareFull + r.DeviceShareFull, late + r.Late, forced + r.ForcedAdvances);
            otherRefusals += r.Future + r.Invalid + r.UnknownGeometry + r.BeyondHorizon + r.Duplicates + r.Reverse + r.UnmatchedExits + r.NegativeWaits + r.TooManyDevices;
            countedIn += step.Movements.Sum(m => m.Entries);
            countedOut += step.Movements.Sum(m => m.Exits);
            waits += step.Waits.Count;
            samples += step.Occupancy.Count;
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Steps_Should_BeThoseOfAnEngineApplyingEverySample_When_DeviceClocksRunFastOrSlow(int seed)
    {
        // ARV-230's random scenarios, with each device's clock off by its own amount (600 and 250 ms fast, 100 ms slow) and each
        // push offered within 600 ms of its events, so samples, crossings and readings arrive ahead of the clock as well as late.
        var stream = new Mulberry32((uint)seed * 2246822519u);
        int Next(int max) => (int)(stream.Next() * max);
        var settings = new QueueEngineSettings
        {
            Lateness = TimeSpan.FromSeconds(20), HandoverWindow = TimeSpan.FromSeconds(8), CensorAfter = TimeSpan.FromMinutes(4),
            MaxBufferedEvents = 1_000_000, MaxStepRecords = 1_000_000, MaxRememberedTracks = 200
        };
        var runs = new QueueStateEngine(Hall, settings);
        var everySample = new QueueStateEngine(Hall, settings with { CoalescePositions = false });
        int[] skew = [600, 250, -100];
        var tracks = Enumerable.Range(0, 12).Select(k => (Device: k % 3, Silent: 0)).ToArray();
        var generation = new int[tracks.Length];
        string[] lines = ["in", "out", "ov-in", "elsewhere"];
        var t0 = Midnight.AddHours(17);
        long aheadOffered = 0;
        var pendingAhead = false;
        for (var tick = 0; tick < 20 * 60 * 5; tick++)
        {
            var now = t0.AddSeconds(tick * 0.2);
            var batch = new List<(QueueInput Input, string Device)>();
            for (var k = 0; k < tracks.Length; k++)
            {
                var device = $"D{tracks[k].Device}";
                var key = $"{device}/{k}-{generation[k]}";
                var at = now.AddMilliseconds(skew[tracks[k].Device]);
                if (tracks[k].Silent > 0)
                {
                    if (--tracks[k].Silent == 0 && Next(2) == 0)
                        generation[k]++;
                    continue;
                }

                var r = Next(1000);
                if (r < 4)
                    batch.Add((new QueueCrossing("in", CrossingDirection.In, key, at), device));
                else if (r < 7)
                {
                    batch.Add((new QueueCrossing("out", CrossingDirection.Out, key, at), device));
                    generation[k]++;
                }
                else if (r < 10)
                    tracks[k].Silent = Next(80);
                else
                    batch.Add((new QueueTrackSeen(key, at.AddMilliseconds(-Next(150)), Next(40) == 0), device));
            }

            var reader = $"D{Next(3)}";
            var readAt = now.AddMilliseconds(skew[reader[1] - '0']);
            if (Next(25) == 0)
                batch.Add((new QueueOccupancy(Next(3) == 0 ? "OV" : "Q", Next(6), readAt), reader));
            if (Next(60) == 0)
                batch.Add((new QueueInterval(lines[Next(3)], Next(4), Next(3), readAt.AddSeconds(-30 - Next(30)), readAt), reader));
            if (Next(40) == 0)
                batch.Add((new QueueCrossing(lines[Next(4)], Next(4) == 0 ? CrossingDirection.Out : CrossingDirection.In, null, readAt), reader));

            // Offered from at once to 600 ms after the events, in shuffled order.
            var reference = now.AddMilliseconds(Next(4) * 200);
            foreach (var (input, device) in batch.OrderBy(_ => Next(1000)))
            {
                if (input.TimeUtc > reference)
                    aheadOffered++;
                runs.Offer(input, reference, device);
                everySample.Offer(input, reference, device);
            }

            if (tick % 5 == 0)
            {
                pendingAhead |= runs.Capture().Runs.Any(r => r.PendingAhead);
                Comparable(runs.Advance(now)).Should().Be(Comparable(everySample.Advance(now)), "seed {0}, second {1}", seed, tick / 5);
                runs.BufferedEvents.Should().BeLessThanOrEqualTo(everySample.BufferedEvents);
            }
        }

        var end = t0.AddMinutes(30);
        Comparable(runs.Advance(end)).Should().Be(Comparable(everySample.Advance(end)));
        aheadOffered.Should().BeGreaterThan(10_000, "the fast devices' events arrive ahead of the clock");
        pendingAhead.Should().BeTrue("runs held samples ahead of the clock as their pending samples");
        runs.BufferedEvents.Should().Be(0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Engine_Should_ContinueIdentically_When_RestoredAtRandomPointsWithSamplesAheadOfTheClock(int seed)
    {
        // Runs whose anchor or pending sample is ahead of the clock travel in the snapshot (zone snapshot version 13): a restored
        // engine coalesces and counts its devices' places ahead of the clock exactly as the one that kept going.
        var stream = new Mulberry32((uint)seed * 3266489917u);
        int Next(int max) => (int)(stream.Next() * max);
        var settings = new QueueEngineSettings { Lateness = TimeSpan.FromSeconds(15), HandoverWindow = TimeSpan.FromSeconds(6) };
        var straight = new QueueStateEngine(Hall, settings);
        var restored = new QueueStateEngine(Hall, settings);
        var t0 = Midnight.AddHours(9);
        var heldAhead = 0;
        var appliedAfterClock = 0;
        for (var tick = 0; tick < 6 * 60 * 5; tick++)
        {
            var now = t0.AddSeconds(tick * 0.2);
            // Security review M1: the third and fourth minutes are quiet, so the watermark follows the receive clock past runs
            // anchored ahead of the event clock; a restore 80 seconds in finds such runs alive.
            var quiet = tick is >= 600 and < 1200;
            for (var k = 0; k < 8 && !quiet; k++)
            {
                var device = $"D{k % 2}";
                var key = $"{device}/{k}-{tick / 400}";
                QueueInput input = Next(200) switch
                {
                    0 => new QueueCrossing("in", CrossingDirection.In, key, now.AddMilliseconds(400)),
                    1 => new QueueCrossing("out", CrossingDirection.Out, key, now.AddMilliseconds(400)),
                    < 6 => new QueueOccupancy("Q", Next(5), now.AddMilliseconds(Next(800) - 300)),
                    _ => new QueueTrackSeen(key, now.AddMilliseconds(Next(1300) - 800))
                };
                if (Next(30) == 0)
                    continue;
                straight.Offer(input, now, device);
                restored.Offer(input, now, device);
            }

            if (Next(40) == 0 || tick == 1000)
            {
                var state = restored.Capture();
                if (state.Runs.Any(r => r.PendingAhead) || state.Runs.Any(r => r.AnchorUtc > state.ClockUtc))
                    heldAhead++;
                if (state.Runs.Any(r => r.AnchorUtc > state.ClockUtc && r.AnchorUtc <= state.WatermarkUtc))
                    appliedAfterClock++;
                restored = QueueStateEngine.Restore(Hall, settings, RoundTrip(state));
            }

            if (tick % 10 == 0)
                Json(restored.Advance(now)).Should().Be(Json(straight.Advance(now)), "seed {0}, tick {1}", seed, tick);
        }

        heldAhead.Should().BeGreaterThan(0, "some restores held runs ahead of the clock");
        appliedAfterClock.Should().BeGreaterThan(0, "and some, in the quiet minutes, runs whose applied anchor is after the clock");
    }

    [Fact]
    public void AheadShare_Should_KeepEveryOtherDevicesCrossings_When_ADeviceMinutesAheadFloodsTheZone()
    {
        // Six sensors 500 ms fast count people in and out with their positions; a seventh, four minutes ahead (within MaxAhead),
        // sends 3,000 positions a second of tracks never seen again. It is refused past its share of the ahead tenth alone; with
        // the floor raised to the whole buffer (no share), it fills the tenth and the zone refuses the fast sensors' crossings.
        var shared = Run(new QueueEngineSettings());
        var unshared = Run(new QueueEngineSettings { DeviceShareFloor = new QueueEngineSettings().MaxBufferedEvents });

        shared.Rejections.BufferFull.Should().Be(0);
        shared.Rejections.DeviceShareFull.Should().BeGreaterThan(500_000, "the device minutes ahead is refused past its share");
        shared.Entries.Should().Be(shared.SentEntries, "every fast sensor's entry is counted");
        shared.Exits.Should().Be(shared.SentExits, "and every exit");
        shared.FlooderAhead.Should().BeLessThanOrEqualTo(10_000 / 7, "a device holds at most its share of the ahead tenth");
        shared.WatermarkPastClock.Should().BeFalse("no device carries the watermark past the receive clock (ARV-148)");
        unshared.Rejections.BufferFull.Should().BeGreaterThan(0);
        unshared.Entries.Should().BeLessThan(unshared.SentEntries, "without shares the device ahead costs the zone its crossings");

        static (QueueRejections Rejections, long Entries, long Exits, long SentEntries, long SentExits, int FlooderAhead, bool WatermarkPastClock) Run(
            QueueEngineSettings settings)
        {
            var engine = new QueueStateEngine(Hall, settings) { ZoneDevices = 7 };
            var fast = TimeSpan.FromMilliseconds(500);
            long entries = 0, exits = 0, sentEntries = 0, sentExits = 0, bufferFull = 0, shareFull = 0;
            var flooderAhead = 0;
            var pastClock = false;
            var t0 = Midnight.AddHours(18);
            for (var second = 1; second <= 300; second++)
            {
                var now = t0.AddSeconds(second);
                for (var sensor = 1; sensor <= 6; sensor++)
                {
                    // One person enters a second and leaves a minute later, each sampled at 5 Hz while inside.
                    var device = $"S-{sensor}";
                    engine.Offer(new QueueCrossing("in", CrossingDirection.In, $"{device}/{second}", now + fast), now, device);
                    for (var inside = Math.Max(1, second - 59); inside <= second; inside++)
                    {
                        for (var k = 0; k < 5; k++)
                            engine.Offer(new QueueTrackSeen($"{device}/{inside}", now.AddMilliseconds(-800 + (k * 200)) + fast), now, device);
                    }

                    sentEntries++;
                    if (second > 60)
                    {
                        engine.Offer(new QueueCrossing("out", CrossingDirection.Out, $"{device}/{second - 60}", now + fast), now, device);
                        sentExits++;
                    }
                }

                for (var k = 0; k < 3_000; k++)
                    engine.Offer(new QueueTrackSeen($"S-9/{second}.{k}", now.AddMinutes(4).AddMilliseconds(-k / 3)), now, "S-9");
                var step = engine.Advance(now);
                pastClock |= step.WatermarkUtc > now;
                (entries, exits) = (entries + step.Movements.Sum(m => m.Entries), exits + step.Movements.Sum(m => m.Exits));
                (bufferFull, shareFull) = (bufferFull + step.Rejections.BufferFull, shareFull + step.Rejections.DeviceShareFull);
                if (second % 30 == 0)
                {
                    var state = engine.Capture();
                    flooderAhead = Math.Max(flooderAhead,
                        state.Buffer.Count(b => b.Ahead && b.Input.TrackKey?.StartsWith("S-9/", StringComparison.Ordinal) == true) +
                        state.Runs.Count(r => r.PendingAhead && r.TrackKey.StartsWith("S-9/", StringComparison.Ordinal)));
                }
            }

            var end = engine.Advance(t0.AddMinutes(20));
            return (new QueueRejections { BufferFull = bufferFull + end.Rejections.BufferFull, DeviceShareFull = shareFull + end.Rejections.DeviceShareFull },
                entries + end.Movements.Sum(m => m.Entries), exits + end.Movements.Sum(m => m.Exits), sentEntries, sentExits, flooderAhead, pastClock);
        }
    }

    [Fact]
    public void AheadShare_Should_HoldALoneDeviceToHalfTheTenth_And_TakeItsFloorFromTheDeviceShareFloor()
    {
        // A device alone in a quiet zone shares the tenth with at least one other (ARV-231's rule); the share never falls below a
        // tenth of DeviceShareFloor, and at least one place.
        Held(new QueueEngineSettings(), zoneDevices: 0).Should().Be(5_000, "half the tenth of 100,000");
        Held(new QueueEngineSettings(), zoneDevices: 40).Should().Be(1_000, "a tenth of the 10,000 floor, above 10,000 over 40 devices");
        Held(new QueueEngineSettings { DeviceShareFloor = 5 }, zoneDevices: 40).Should().Be(250, "10,000 over 40 devices");
        Held(new QueueEngineSettings { DeviceShareFloor = 5 }, zoneDevices: 100_000).Should().Be(1, "never below one place");

        static int Held(QueueEngineSettings settings, int zoneDevices)
        {
            var engine = new QueueStateEngine(Hall, settings) { ZoneDevices = zoneDevices };
            var now = Midnight.AddHours(3);
            for (var k = 0; k < 20_000; k++)
                engine.Offer(new QueueTrackSeen($"S-9/{k}", now.AddSeconds(30)), now, "S-9");
            return engine.Capture().Buffer.Count(b => b.Ahead);
        }
    }

    [Fact]
    public void Restore_Should_RefuseARunAheadOfTheClock_When_ItIsNotHeldAsSuchOrBeyondMaxAhead()
    {
        // A run whose anchor and pending sample are ahead of the clock (CWE-501): the anchor must be held in the buffer as that
        // track's sample ahead of the clock, the pending sample marked ahead and at most MaxAhead past the clock.
        var settings = new QueueEngineSettings { MaxAhead = TimeSpan.FromSeconds(1), HandoverWindow = TimeSpan.FromSeconds(8) };
        var engine = new QueueStateEngine(Hall, settings);
        var now = Midnight.AddHours(10);
        engine.Offer(new QueueTrackSeen("S/1", now.AddMilliseconds(200)), now, "S");
        engine.Offer(new QueueTrackSeen("S/1", now.AddMilliseconds(400)), now, "S");
        var state = RoundTrip(engine.Capture());
        var run = state.Runs.Should().ContainSingle().Subject;
        run.PendingAhead.Should().BeTrue();
        run.AnchorUtc.Should().Be(now.AddMilliseconds(200));
        state.Buffer.Should().ContainSingle().Which.Ahead.Should().BeTrue();
        var restored = QueueStateEngine.Restore(Hall, settings, state);
        Json(restored.Advance(now.AddMinutes(5))).Should().Be(Json(engine.Advance(now.AddMinutes(5))));

        foreach (var (bad, why) in new[]
                 {
                     (state with { Runs = [run with { PendingUtc = now.AddSeconds(1).AddTicks(1) }] }, "a pending sample past MaxAhead"),
                     (state with { Runs = [run with { PendingAhead = false }] }, "a pending sample after the clock not marked ahead"),
                     (state with { Buffer = [state.Buffer[0] with { Ahead = false }] }, "an event after the clock not held as ahead"),
                     (state with { Buffer = [] }, "an anchor after the clock not held at all")
                 })
        {
            var act = () => QueueStateEngine.Restore(Hall, settings, RoundTrip(bad));
            act.Should().Throw<InvalidDataException>(why);
        }
    }

    [Fact]
    public void AheadTenth_Should_CountAPendingSampleOnce_When_ASampleAheadOfTheClockReplacesIt_And_AfterARestore()
    {
        // Shares off (a floor at the bound), so only the tenth binds: 10 places of a 100-event buffer. A sample ahead of the clock
        // that replaces a pending sample which was not ahead takes a place of the tenth; one that replaces a pending sample ahead
        // of it takes none. A restored engine counts the pending samples ahead of the clock as the one that kept going.
        var settings = new QueueEngineSettings { Lateness = TimeSpan.FromSeconds(30), MaxBufferedEvents = 100, DeviceShareFloor = 100 };
        var engine = new QueueStateEngine(Hall, settings);
        var now = Midnight.AddHours(14);
        engine.Offer(new QueueTrackSeen("S-1/a", now.AddMilliseconds(-200)), now, "S-1");
        engine.Offer(new QueueTrackSeen("S-1/a", now.AddMilliseconds(-100)), now, "S-1");
        engine.Offer(new QueueTrackSeen("S-1/b", now.AddMilliseconds(100)), now, "S-1");
        engine.Offer(new QueueTrackSeen("S-1/b", now.AddMilliseconds(200)), now, "S-1");
        for (var k = 0; k < 8; k++)
            engine.Offer(new QueueTrackSeen($"S-1/c{k}", now.AddMilliseconds(300)), now, "S-1");
        var full = engine.Capture();
        (full.Buffer.Count(b => b.Ahead) + full.Runs.Count(r => r.PendingAhead)).Should().Be(10, "the tenth is full");

        engine.Offer(new QueueTrackSeen("S-1/a", now.AddMilliseconds(400)), now, "S-1");
        engine.Offer(new QueueTrackSeen("S-1/b", now.AddMilliseconds(500)), now, "S-1");
        var restored = QueueStateEngine.Restore(Hall, settings, RoundTrip(engine.Capture()));
        foreach (var e in new[] { engine, restored })
            e.Offer(new QueueTrackSeen("S-1/d", now.AddMilliseconds(600)), now, "S-1");

        var step = engine.Advance(now);
        step.Rejections.BufferFull.Should().Be(2, "a's sample would take a place of the full tenth, and so would d's; b's replaces one");
        var held = engine.Capture();
        held.Runs.Single(r => r.TrackKey == "S-1/a").PendingUtc.Should().Be(now.AddMilliseconds(-100), "a's pending sample stays");
        held.Runs.Single(r => r.TrackKey == "S-1/b").PendingUtc.Should().Be(now.AddMilliseconds(500));
        Json(restored.Advance(now)).Should().Be(Json(step), "the restored engine refused d alike");
        // An engine whose cursor never moved writes it without a kind, a restored one as UTC (as before ARV-239).
        Json(restored.Capture() with { CursorUtc = held.CursorUtc }).Should().Be(Json(held), "and holds the same events");
    }

    [Fact]
    public void Restore_Should_AcceptAnAppliedAnchorAfterTheClock_When_AQuietZonesWatermarkPassedIt_And_ContinueIdentically()
    {
        // Security review M1: in a quiet zone the watermark follows the receive clock (the push interval behind it, less the
        // lateness) while the clock moves only with events, so a run anchored by a sample ahead of the clock can have that sample
        // applied and still be after the clock until the run ends, a hand-over window later. The engine writes that state.
        var settings = new QueueEngineSettings();
        var engine = new QueueStateEngine(Hall, settings);
        var now = Midnight.AddHours(15);
        engine.Offer(new QueueTrackSeen("S/1", now.AddMilliseconds(400)), now, "S");
        engine.Advance(now.AddSeconds(151));
        var state = RoundTrip(engine.Capture());
        var run = state.Runs.Should().ContainSingle().Subject;
        run.AnchorUtc.Should().BeAfter(state.ClockUtc.Value);
        state.WatermarkUtc.Should().BeOnOrAfter(run.AnchorUtc);
        state.Buffer.Should().BeEmpty("the anchor's sample was applied");

        var restored = QueueStateEngine.Restore(Hall, settings, state);
        for (var k = 1; k <= 20; k++)
        {
            var next = now.AddSeconds(151 + k);
            foreach (var e in new[] { engine, restored })
                e.Offer(new QueueTrackSeen("S/1", next.AddMilliseconds(400)), next, "S");
            Json(restored.Advance(next)).Should().Be(Json(engine.Advance(next)));
        }
    }

    [Fact]
    public void Restore_Should_RefuseEventsAheadOfTheClock_When_BeyondMaxAheadOrMoreThanTheTenth()
    {
        // CWE-501 (security review M1 and L1): whatever the watermark says, a run's anchor is at most MaxAhead after the clock, and
        // so is each buffered event ahead of it; one not yet applied is held in the buffer; and no more events are ahead of the
        // clock than the tenth.
        var settings = new QueueEngineSettings { MaxAhead = TimeSpan.FromSeconds(1) };
        var now = Midnight.AddHours(16);
        var quiet = new QueueStateEngine(Hall, settings);
        quiet.Offer(new QueueTrackSeen("S/1", now.AddMilliseconds(400)), now, "S");
        quiet.Advance(now.AddSeconds(151));
        var applied = RoundTrip(quiet.Capture());
        var run = applied.Runs.Should().ContainSingle().Subject;
        var beyond = applied.ClockUtc.Value.AddSeconds(1).AddTicks(1);

        var busy = new QueueStateEngine(Hall, settings);
        busy.Offer(new QueueTrackSeen("S/1", now.AddMilliseconds(400)), now, "S");
        busy.Offer(new QueueCrossing("in", CrossingDirection.In, null, now.AddMilliseconds(500)), now, "S");
        var held = RoundTrip(busy.Capture());
        var crossing = held.Buffer.Single(b => b.Input.TrackKey is null);

        foreach (var (bad, why, message) in new[]
                 {
                     (applied with { Runs = [run with { AnchorUtc = beyond }], WatermarkUtc = beyond.AddSeconds(1) },
                         "an applied anchor further after the clock than MaxAhead", "*further after its clock than MaxAhead*"),
                     (held with { Buffer = [crossing] }, "an anchor after the clock and the watermark, not held", "*after its watermark and not held*"),
                     (held with { Buffer = [.. held.Buffer.Select(b => b == crossing ? b with { Input = b.Input with { TimeUtc = beyond } } : b)] },
                         "a buffered event further ahead of the clock than MaxAhead", "*further ahead of its clock than MaxAhead*")
                 })
        {
            var act = () => QueueStateEngine.Restore(Hall, settings, bad);
            act.Should().Throw<InvalidDataException>(why).WithMessage(message);
        }

        // Eleven events ahead of the clock restore within a 1,000-event buffer, not within a 100-event one (a tenth is 10).
        var many = new QueueStateEngine(Hall, new QueueEngineSettings());
        for (var k = 0; k < 11; k++)
            many.Offer(new QueueCrossing("in", CrossingDirection.In, null, now.AddSeconds(1 + k)), now, "S");
        var eleven = RoundTrip(many.Capture());
        QueueStateEngine.Restore(Hall, new QueueEngineSettings { MaxBufferedEvents = 1_000 }, eleven).BufferedEvents.Should().Be(11);
        var tight = () => QueueStateEngine.Restore(Hall, new QueueEngineSettings { MaxBufferedEvents = 100 }, eleven);
        tight.Should().Throw<InvalidDataException>().WithMessage("*more events ahead of its clock than a tenth*");
    }

    [Fact]
    public void ZoneRestore_Should_RestoreAQuietZonesRunAnchoredAfterTheClock_When_TicksMovedTheWatermarkPastIt()
    {
        // Security review M1 through the zone processor: a T3 sensor 500 ms fast pushes, the zone goes quiet, its idle ticks move
        // the watermark past the run's anchor, and a checkpoint taken then restores and continues as the zone that kept going.
        var settings = new ZoneProcessorSettings();
        var zone = new ZoneProcessor("PLE/Q", Hall, 1, settings);
        var received = Midnight.AddHours(16);
        zone.Offer(Stamp(new TrackSampleBatch { Samples = [Sample("7", received.AddMilliseconds(-200)), Sample("7", received.AddMilliseconds(400))] }, "S-1", received), received);
        zone.Tick(received.AddSeconds(151));
        zone.Drain();
        var state = JsonSerializer.Deserialize<ZoneProcessorState>(JsonSerializer.Serialize(zone.Capture(), EventCatalog.Json), EventCatalog.Json);
        var run = state.Engine.Runs.Should().ContainSingle().Subject;
        run.AnchorUtc.Should().Be(received.AddMilliseconds(400)).And.BeAfter(state.Engine.ClockUtc.Value);
        state.Engine.WatermarkUtc.Should().BeOnOrAfter(run.AnchorUtc);
        var notAfter = received.AddHours(1);
        SnapshotChecks.Of(state, "PLE/Q", Hall.QueueZone, 1, notAfter).Should().BeEmpty();

        var restored = ZoneProcessor.Restore("PLE/Q", Hall, 1, settings, state, notAfter);
        var next = received.AddSeconds(160);
        var push = Stamp(new TrackSampleBatch { Samples = [Sample("7", next.AddMilliseconds(400))] }, "S-1", next);
        zone.Offer(push, next);
        restored.Offer(push, next);
        Json(restored.Drain()).Should().Be(Json(zone.Drain()));
        var kept = zone.Capture();
        var again = restored.Capture();
        // An engine whose cursor never moved writes it without a kind, a restored one as UTC (as before ARV-239).
        Json(again with { Engine = again.Engine with { CursorUtc = kept.Engine.CursorUtc } }).Should().Be(Json(kept));
    }

    [Fact]
    public void ZoneRestore_Should_RefuseRunsAheadOfTheClock_When_TheSnapshotIsBeforeVersion13()
    {
        // A pending sample ahead of the clock may be after the zone's reference clock (by at most MaxAhead, which the engine checks);
        // a snapshot before version 13 never carries one, nor a run anchored after the engine's clock.
        var settings = new ZoneProcessorSettings { Engine = new QueueEngineSettings { Lateness = TimeSpan.FromSeconds(10), HandoverWindow = TimeSpan.FromSeconds(30) } };
        var engine = new QueueStateEngine(Hall, settings.Engine);
        var t0 = Midnight.AddHours(8);
        engine.Offer(new QueueTrackSeen("S-1/1", t0.AddMilliseconds(200)), t0, "S-1");
        engine.Offer(new QueueTrackSeen("S-1/1", t0.AddMilliseconds(400)), t0, "S-1");
        var notAfter = t0.AddHours(1);
        var state = new ZoneProcessor("PLE/Q", Hall, 1, settings).Capture() with { Engine = RoundTrip(engine.Capture()), ReferenceUtc = t0 };
        var run = state.Engine.Runs.Should().ContainSingle().Which;
        run.PendingAhead.Should().BeTrue();
        state.Version.Should().Be(ZoneProcessorState.AheadRunsSinceVersion).And.Be(ZoneProcessorState.CurrentVersion);
        SnapshotChecks.Of(state, "PLE/Q", Hall.QueueZone, 1, notAfter).Should().BeEmpty();
        ZoneProcessor.Restore("PLE/Q", Hall, 1, settings, state, notAfter).Capture().Engine.Runs.Should().ContainSingle().Which.PendingAhead.Should().BeTrue();

        var anchoredAhead = run with { PendingUtc = null, PendingAhead = false, PendingSequence = 0 };
        foreach (var (bad, because) in new (ZoneProcessorState, string)[]
                 {
                     (state with { Version = ZoneProcessorState.AheadRunsSinceVersion - 1 }, "a snapshot before version 13 holds no pending sample ahead of the clock"),
                     (state with { Version = ZoneProcessorState.AheadRunsSinceVersion - 1, Engine = state.Engine with { Runs = [anchoredAhead] } },
                         "nor a run anchored after the engine's clock"),
                     (state with { Engine = state.Engine with { Runs = [run with { PendingAhead = false }] } }, "a pending sample after the zone's reference clock not marked ahead")
                 })
        {
            SnapshotChecks.Of(bad, "PLE/Q", Hall.QueueZone, 1, notAfter).Should().NotBeEmpty(because);
            var restore = () => ZoneProcessor.Restore("PLE/Q", Hall, 1, settings, bad, notAfter);
            restore.Should().Throw<InvalidDataException>(because);
        }
    }

    #endregion

    #region Methods

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, EventCatalog.Json);

    private static string Comparable(QueueStep step) => Json(step with { BufferedEvents = 0 });

    private static QueueEngineState RoundTrip(QueueEngineState state) =>
        JsonSerializer.Deserialize<QueueEngineState>(JsonSerializer.Serialize(state, EventCatalog.Json), EventCatalog.Json);

    private static Sensed<TrackPosition> Sample(string track, DateTime at) => new(new TrackPosition(track, 1, 1, 1.7, at), at, SensedFlags.None);

    private static T Stamp<T>(T batch, string device, DateTime received) where T : SensingBatch
    {
        batch.Id = Guid.NewGuid();
        batch.DeviceId = Guid.NewGuid();
        batch.DeviceCode = device;
        batch.SiteCode = "PLE";
        batch.QueueZoneName = "Q";
        batch.Dialect = "canonical";
        batch.Commissioned = true;
        batch.ReceivedUtc = received;
        batch.Clock = new ClockReading(0, true, ClockState.Ok);
        return batch;
    }

    #endregion
}
