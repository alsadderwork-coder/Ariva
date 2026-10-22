using System.Text.Json;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Queueing;
using Ariva.Core.Sensing;
using Ariva.Infra.Messaging;
using Ariva.Infra.Streaming;
using Ariva.Simulation.Api.Scenarios.Engine;
using FluentAssertions;

namespace Ariva.UnitTests.Queueing;

/// <summary>
/// ARV-230 (CWE-400, CWE-120): the queue engine keeps each track's position samples in a run (an anchor and a pending sample)
/// instead of one buffered event a sample. Its steps are those of an engine that applies every sample, out of order, across gaps
/// and restores alike; a hall zone of 20 T3 sensors at 30 people and 5 Hz (3,000 positions a second) keeps every crossing and
/// occupancy reading, is never refused for a full buffer, and its state stays under 1 MB, where the engine before ARV-230
/// refused events of every kind within a minute.
/// </summary>
public sealed class QueueEnginePositionRunTests
{
    #region Fields

    private static readonly DateTime Midnight = new(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc);

    private static readonly QueueZoneGeometry Hall = new("Q", new HashSet<string> { "in" }, new HashSet<string> { "out" },
        new HashSet<string> { "ov-in" }, new HashSet<string> { "OV" });

    #endregion

    #region Tests

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Steps_Should_BeThoseOfAnEngineApplyingEverySample_When_ATrackSendsPositionsAtFiveHertz(int seed)
    {
        // Tracks walk in, are sampled at 5 Hz with pauses past the hand-over window, sometimes leave without an exit; samples and
        // crossings arrive out of order, beside interval readings, occupancy and anonymous crossings; entrants are censored too.
        var stream = new Mulberry32((uint)seed * 2654435761u);
        int Next(int max) => (int)(stream.Next() * max);
        var settings = new QueueEngineSettings
        {
            Lateness = TimeSpan.FromSeconds(20), HandoverWindow = TimeSpan.FromSeconds(8), CensorAfter = TimeSpan.FromMinutes(4),
            MaxBufferedEvents = 1_000_000, MaxStepRecords = 1_000_000, MaxRememberedTracks = 200
        };
        var runs = new QueueStateEngine(Hall, settings);
        var everySample = new QueueStateEngine(Hall, settings with { CoalescePositions = false });
        var tracks = Enumerable.Range(0, 12).Select(k => (Key: $"D{k % 3}/{k}", Silent: 0)).ToArray();
        var generation = new int[tracks.Length];
        string[] lines = ["in", "out", "ov-in", "elsewhere"];
        var t0 = Midnight.AddHours(17);
        var fewest = int.MaxValue;
        for (var tick = 0; tick < 20 * 60 * 5; tick++)
        {
            var now = t0.AddSeconds(tick * 0.2);
            var batch = new List<QueueInput>();
            for (var k = 0; k < tracks.Length; k++)
            {
                var key = $"{tracks[k].Key}-{generation[k]}";
                if (tracks[k].Silent > 0)
                {
                    // A pause, often past the hand-over window (fragmented), then the same person again or a new one.
                    if (--tracks[k].Silent == 0 && Next(2) == 0)
                        generation[k]++;
                    continue;
                }

                var r = Next(1000);
                if (r < 4)
                    batch.Add(new QueueCrossing("in", CrossingDirection.In, key, now));
                else if (r < 7)
                {
                    batch.Add(new QueueCrossing("out", CrossingDirection.Out, key, now));
                    generation[k]++;
                }
                else if (r < 10)
                    tracks[k].Silent = Next(80);
                else
                    batch.Add(new QueueTrackSeen(key, now.AddMilliseconds(-Next(150)), Next(40) == 0));
            }

            if (Next(25) == 0)
                batch.Add(new QueueOccupancy(Next(3) == 0 ? "OV" : "Q", Next(6), now));
            if (Next(60) == 0)
                batch.Add(new QueueInterval(lines[Next(3)], Next(4), Next(3), now.AddSeconds(-30 - Next(30)), now));
            if (Next(40) == 0)
                batch.Add(new QueueCrossing(lines[Next(4)], Next(4) == 0 ? CrossingDirection.Out : CrossingDirection.In, null, now));

            // Late and reordered: a batch is offered up to 6 seconds after its events, in shuffled order.
            var reference = now.AddSeconds(Next(30) * 0.2);
            foreach (var input in batch.OrderBy(_ => Next(1000)))
            {
                runs.Offer(input, reference);
                everySample.Offer(input, reference);
            }

            if (tick % 5 == 0)
            {
                Comparable(runs.Advance(now)).Should().Be(Comparable(everySample.Advance(now)), "seed {0}, second {1}", seed, tick / 5);
                fewest = Math.Min(fewest, everySample.BufferedEvents - runs.BufferedEvents);
                runs.BufferedEvents.Should().BeLessThanOrEqualTo(everySample.BufferedEvents);
            }
        }

        var end = t0.AddMinutes(30);
        Comparable(runs.Advance(end)).Should().Be(Comparable(everySample.Advance(end)));
        fewest.Should().BeGreaterThanOrEqualTo(0);
        everySample.BufferedEvents.Should().Be(0);
        runs.BufferedEvents.Should().Be(0, "every pending sample reaches the buffer as the watermark passes it");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Engine_Should_ContinueIdentically_When_RestoredAtRandomPointsWithRunsHeld(int seed)
    {
        // The runs travel in the snapshot (zone snapshot version 11), so a restored engine coalesces exactly as the one that kept
        // going: the same steps, held events counted alike.
        var stream = new Mulberry32((uint)seed * 2654435761u);
        int Next(int max) => (int)(stream.Next() * max);
        var settings = new QueueEngineSettings { Lateness = TimeSpan.FromSeconds(15), HandoverWindow = TimeSpan.FromSeconds(6) };
        var straight = new QueueStateEngine(Hall, settings);
        var restored = new QueueStateEngine(Hall, settings);
        var t0 = Midnight.AddHours(9);
        for (var tick = 0; tick < 6 * 60 * 5; tick++)
        {
            var now = t0.AddSeconds(tick * 0.2);
            for (var k = 0; k < 8; k++)
            {
                QueueInput input = Next(200) switch
                {
                    0 => new QueueCrossing("in", CrossingDirection.In, $"D/{k}-{tick / 400}", now),
                    1 => new QueueCrossing("out", CrossingDirection.Out, $"D/{k}-{tick / 400}", now),
                    < 6 => new QueueOccupancy("Q", Next(5), now),
                    _ => new QueueTrackSeen($"D/{k}-{tick / 400}", now.AddMilliseconds(-Next(900)))
                };
                if (Next(30) == 0)
                    continue; // a sample lost: gaps of all lengths
                straight.Offer(input, now);
                restored.Offer(input, now);
            }

            if (Next(40) == 0)
                restored = QueueStateEngine.Restore(Hall, settings, RoundTrip(restored.Capture()));
            if (tick % 10 == 0)
                Json(restored.Advance(now)).Should().Be(Json(straight.Advance(now)), "seed {0}, tick {1}", seed, tick);
        }

        restored.Capture().Runs.Should().NotBeEmpty("tracks still being sampled hold runs");
    }

    [Fact]
    public void HallZone_Should_KeepEveryCrossingWithItsStateUnderAMegabyte_When_TwentySensorsSendThreeThousandPositionsASecond()
    {
        // 20 T3 sensors, each seeing 30 people walking the zone in 5 minutes (track ids kept for the walk, a new one for the next
        // person), sampled at 5 Hz: 3,000 positions a second, pushed every second with the second's crossings and an occupancy
        // reading, for 10 minutes. The engine before ARV-230 (every sample buffered for the 90 s lateness) is refused by its
        // 100,000-event buffer within a minute and loses crossings with it.
        const int Sensors = 20, People = 30, Hertz = 5, WalkSeconds = 300, RunSeconds = 600;
        var settings = new QueueEngineSettings();
        var runs = new QueueStateEngine(Hall, settings);
        var everySample = new QueueStateEngine(Hall, settings with { CoalescePositions = false });
        var engines = new[] { runs, everySample };
        var counted = new (long In, long Out, long Waits, long BufferFull, long Forced, long Samples)[2];
        long sentIn = 0, sentOut = 0, positions = 0, largestState = 0;
        var largestParts = "";
        var t0 = Midnight.AddHours(18);
        var zone = new ZoneProcessor("PLE/Q", Hall, 1);
        var entered = new HashSet<string>(StringComparer.Ordinal);

        for (var second = 1; second <= RunSeconds; second++)
        {
            var now = t0.AddSeconds(second);
            var batch = new List<QueueInput>();
            for (var sensor = 0; sensor < Sensors; sensor++)
            {
                for (var person = 0; person < People; person++)
                {
                    // Each person starts the walk at its own offset, so the zone always holds 30 people a sensor; counted in
                    // samples, so each walk ends and the next begins at exactly one sample.
                    var offset = (((person * WalkSeconds / People) + (sensor * 7)) % WalkSeconds) * Hertz;
                    for (var s = 0; s < Hertz; s++)
                    {
                        var sample = ((second - 1) * Hertz) + s;
                        var at = t0.AddMilliseconds(sample * 1000 / Hertz);
                        var walked = sample + offset;
                        var walk = walked / (WalkSeconds * Hertz);
                        var key = $"S{sensor}/{person}-{walk}";
                        if (walked % (WalkSeconds * Hertz) == 0 && walk > 0)
                        {
                            // The person who walked in during the run leaves; the next one enters under a new id.
                            var previous = $"S{sensor}/{person}-{walk - 1}";
                            if (entered.Remove(previous))
                            {
                                batch.Add(new QueueCrossing("out", CrossingDirection.Out, previous, at));
                                sentOut++;
                            }

                            batch.Add(new QueueCrossing("in", CrossingDirection.In, key, at));
                            entered.Add(key);
                            sentIn++;
                        }

                        batch.Add(new QueueTrackSeen(key, at));
                        positions++;
                    }
                }
            }

            batch.Add(new QueueOccupancy("Q", Sensors * People, now));
            foreach (var engine in engines)
            {
                foreach (var input in batch)
                    engine.Offer(input, now);
            }

            for (var e = 0; e < engines.Length; e++)
                Tally(e, engines[e].Advance(now));
            if (second % 30 == 0)
            {
                var state = zone.Capture() with { Engine = runs.Capture(), ReferenceUtc = now };
                var bytes = JsonSerializer.SerializeToUtf8Bytes(state, EventCatalog.Json).LongLength;
                if (bytes > largestState)
                {
                    largestState = bytes;
                    largestParts = $"buffer {state.Engine.Buffer.Count}, runs {state.Engine.Runs.Count}, held {state.Engine.Held.Count}, hand-overs {state.Engine.Handovers.Count}";
                }
            }
        }

        for (var e = 0; e < engines.Length; e++)
            Tally(e, engines[e].Advance(t0.AddSeconds(RunSeconds + 600)));

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"positions {positions}, entries {sentIn}, exits {sentOut}; with runs: largest zone state {largestState} bytes ({largestParts}), buffer full {counted[0].BufferFull}; " +
            $"every sample buffered: buffer full {counted[1].BufferFull}, entries counted {counted[1].In}");
        positions.Should().Be(Sensors * People * Hertz * (long)RunSeconds, "3,000 positions a second");
        counted[0].BufferFull.Should().Be(0, "no event is refused for a full buffer");
        counted[0].Forced.Should().Be(0);
        counted[0].In.Should().Be(sentIn, "every entry crossing is counted");
        counted[0].Out.Should().Be(sentOut, "every exit crossing is counted");
        counted[0].Waits.Should().Be(sentOut, "each exit closes its own track's wait");
        counted[0].Samples.Should().BeGreaterThan(0, "the occupancy readings give samples");
        largestState.Should().BeLessThan(1024 * 1024, "the zone's checkpointed state stays under 1 MB");
        counted[1].BufferFull.Should().BeGreaterThan(0, "an engine that buffers every sample is refused at this rate");
        counted[1].In.Should().BeLessThan(sentIn, "and loses crossings with it");

        void Tally(int e, QueueStep step)
        {
            counted[e].In += step.Movements.Sum(m => m.Entries);
            counted[e].Out += step.Movements.Sum(m => m.Exits);
            counted[e].Waits += step.Waits.Count;
            counted[e].BufferFull += step.Rejections.BufferFull;
            counted[e].Forced += step.Rejections.ForcedAdvances;
            counted[e].Samples += step.Occupancy.Count;
        }
    }

    [Fact]
    public void Restore_Should_RefuseARun_When_ItsPendingSampleIsOutsideItsWindowOrBehindTheWatermark()
    {
        var settings = new QueueEngineSettings { Lateness = TimeSpan.FromSeconds(10), HandoverWindow = TimeSpan.FromSeconds(30) };
        var engine = new QueueStateEngine(Hall, settings);
        var t0 = Midnight.AddHours(8);
        engine.Offer(new QueueCrossing("in", CrossingDirection.In, "D/1", t0), t0);
        for (var k = 1; k <= 20; k++)
            engine.Offer(new QueueTrackSeen("D/1", t0.AddSeconds(k * 0.2)), t0.AddSeconds(k * 0.2));
        engine.Advance(t0.AddSeconds(5));
        var state = RoundTrip(engine.Capture());
        var run = state.Runs.Should().ContainSingle().Which;
        run.PendingUtc.Should().Be(t0.AddSeconds(4), "the latest sample is pending, the first one anchors the run");
        state.Buffer.Count(b => b.Input.Kind == "track-seen").Should().Be(1, "one of 20 samples takes a place in the buffer");
        QueueStateEngine.Restore(Hall, settings, state).BufferedEvents.Should().Be(engine.BufferedEvents);

        foreach (var (bad, because) in new (QueueEngineState, string)[]
                 {
                     (state with { Runs = [run with { PendingUtc = run.AnchorUtc }] }, "a pending sample at its anchor"),
                     (state with { Runs = [run with { PendingUtc = run.AnchorUtc.AddSeconds(31) }] }, "a pending sample past the hand-over window"),
                     (state with { Runs = [run with { AnchorUtc = state.WatermarkUtc.AddSeconds(-10), PendingUtc = state.WatermarkUtc }] }, "a pending sample the watermark reached"),
                     (state with { Runs = [run with { PendingSequence = state.Sequence }] }, "a pending sample arriving after the snapshot"),
                     (state with { Runs = [run, run] }, "a track's run twice"),
                     (state with { Runs = [run with { TrackKey = "" }] }, "a run without a track"),
                     (state with { Runs = [run with { AnchorUtc = DateTime.MinValue }] }, "an anchor that is not a plausible time"),
                     (state with { Runs = [.. Enumerable.Range(0, 100_001).Select(k => run with { TrackKey = "D/" + k })] }, "more pending samples than the buffer holds")
                 })
        {
            var restore = () => QueueStateEngine.Restore(Hall, settings, bad);
            restore.Should().Throw<InvalidDataException>(because);
        }
    }

    [Fact]
    public void Zone_Should_CoalesceATracksSamples_When_ADeviceSendsEachPushNewestFirst()
    {
        // Security review (CWE-400, CWE-501): a device that pushes once a minute and lists a push's samples newest first must not
        // take a place a sample (each is behind the anchor its newest sample set), or a few such sensors fill a zone's buffer and
        // its crossings are refused with it. The zone offers each push in time order, so it holds what an in-order push holds.
        const int Sensors = 3, People = 10, Hertz = 5, PushSeconds = 60, Pushes = 6;
        var inOrder = new ZoneProcessor("PLE/Q", Hall, 1);
        var newestFirst = new ZoneProcessor("PLE/Q", Hall, 1);
        var direct = new QueueStateEngine(Hall);
        var t0 = Midnight.AddHours(18);
        for (var push = 1; push <= Pushes; push++)
        {
            var received = t0.AddSeconds(push * PushSeconds);
            for (var sensor = 0; sensor < Sensors; sensor++)
            {
                var device = $"S-{sensor + 1}";
                var samples = new List<Sensed<TrackPosition>>();
                for (var person = 0; person < People; person++)
                {
                    for (var k = 0; k < PushSeconds * Hertz; k++)
                    {
                        var at = received.AddSeconds(-PushSeconds).AddMilliseconds((k + 1) * 1000 / Hertz);
                        samples.Add(new Sensed<TrackPosition>(new TrackPosition($"{person}", 1, 1, 1.7, at), at, SensedFlags.None));
                    }
                }

                var reversed = samples.OrderByDescending(e => e.TimeUtc).ToList();
                if (push == 1)
                {
                    // Each person entered just before the first push's samples.
                    var entered = received.AddSeconds(-PushSeconds);
                    List<Sensed<LineCrossing>> Entries() =>
                        [.. Enumerable.Range(0, People).Select(person => new Sensed<LineCrossing>(new LineCrossing("in", CrossingDirection.In, $"{person}", entered), entered, SensedFlags.None))];
                    inOrder.Offer(Stamp(new VendorLineCrossingBatch { Crossings = Entries() }, device, received), received);
                    newestFirst.Offer(Stamp(new VendorLineCrossingBatch { Crossings = Entries() }, device, received), received);
                }

                inOrder.Offer(Stamp(new TrackSampleBatch { Samples = samples }, device, received), received);
                newestFirst.Offer(Stamp(new TrackSampleBatch { Samples = reversed }, device, received), received);
                foreach (var e in reversed)
                    direct.Offer(new QueueTrackSeen($"{device}/{e.Event.TrackId}", e.TimeUtc), received);
            }

            direct.Advance(received);
        }

        var ordered = inOrder.Capture().Engine;
        var sent = newestFirst.Capture().Engine;
        var held = sent.Buffer.Count + sent.Runs.Count(r => r.PendingUtc is not null);
        TestContext.Current.TestOutputHelper?.WriteLine($"held by the zone {held}, by the engine offered newest first {direct.BufferedEvents}");
        Json(sent).Should().Be(Json(ordered), "the zone offers a push's samples in time order, whichever order the device sent");
        sent.Runs.Should().HaveCount(Sensors * People, "each track keeps its run");
        held.Should().BeLessThan(300, "a run takes a place every hand-over window (30 s), for each track's latest 150 s at most");
        direct.BufferedEvents.Should().BeGreaterThan(9_000, "the engine offered the samples newest first buffers nearly each one");
        Json(newestFirst.Drain()).Should().Be(Json(inOrder.Drain()));
    }

    [Fact]
    public void Runs_Should_StayWithinTheirBoundAndTheStateWithinItsBudget_When_ADeviceFloodsUniqueTracks()
    {
        // Security review (CWE-400, CWE-120): one device sends Ingest's largest push (3,000 samples) every second, each sample a
        // track never seen again, with keys of 81 characters, for a minute at the default bounds. The runs stop at
        // MaxSightingRuns, beyond which samples are buffered as before, so the zone's state is at most the runs' share above the
        // engine that buffers every sample: both refuse the device past its share of the buffer (half of it, ARV-231) alike, and
        // their steps are the same.
        var settings = new QueueEngineSettings();
        var runs = new QueueStateEngine(Hall, settings);
        var everySample = new QueueStateEngine(Hall, settings with { CoalescePositions = false });
        var t0 = Midnight.AddHours(19);
        long runsBytes = 0, everyBytes = 0, refused = 0;
        var mostRuns = 0;
        for (var second = 1; second <= 60; second++)
        {
            var now = t0.AddSeconds(second);
            for (var k = 0; k < 3_000; k++)
            {
                var seen = new QueueTrackSeen("S-FLOOD/" + ((second * 3_000) + k).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(73, '0'),
                    now.AddMilliseconds(-k / 3));
                runs.Offer(seen, now);
                everySample.Offer(seen, now);
            }

            var step = runs.Advance(now);
            refused += step.Rejections.BufferFull + step.Rejections.DeviceShareFull;
            Comparable(step).Should().Be(Comparable(everySample.Advance(now)), "second {0}", second);
            if (second % 10 == 0)
            {
                var state = runs.Capture();
                mostRuns = Math.Max(mostRuns, state.Runs.Count);
                runsBytes = Math.Max(runsBytes, JsonSerializer.SerializeToUtf8Bytes(state, EventCatalog.Json).LongLength);
                everyBytes = Math.Max(everyBytes, JsonSerializer.SerializeToUtf8Bytes(everySample.Capture(), EventCatalog.Json).LongLength);
            }
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"runs at most {mostRuns}; largest engine state with runs {runsBytes} bytes, every sample buffered {everyBytes} bytes; refused {refused}");
        mostRuns.Should().Be(settings.MaxSightingRuns, "the runs stop at their bound");
        refused.Should().BeGreaterThan(0, "the flood is refused past the device's share of the buffer (ARV-231)");
        runsBytes.Should().BeLessThan(everyBytes + (settings.MaxSightingRuns * 200L), "the runs add at most their bound's share");
        runsBytes.Should().BeLessThan(StreamStore.MaxStateBytes / 2, "far within what a zone may save");
    }

    [Fact]
    public void Bounds_Should_ProcessAPendingSampleToMakeRoom_When_ItIsTheOldestEventHeld()
    {
        // Security review: the buffer is full and the oldest event held is a run's pending sample, outside the buffer. The forced
        // advance reaches it as it reaches a buffered one: the same steps as the engine that buffers every sample.
        var settings = new QueueEngineSettings { Lateness = TimeSpan.FromSeconds(30), MaxBufferedEvents = 10 };
        var runs = new QueueStateEngine(Hall, settings);
        var everySample = new QueueStateEngine(Hall, settings with { CoalescePositions = false });
        var t0 = Midnight.AddHours(7);
        var steps = new List<(QueueStep Runs, QueueStep Every)>();
        foreach (var engine in new[] { runs, everySample })
        {
            engine.Offer(new QueueCrossing("in", CrossingDirection.In, "D/1", t0), t0);
            engine.Offer(new QueueTrackSeen("D/1", t0), t0);
            engine.Offer(new QueueCrossing("in", CrossingDirection.In, "D/2", t0.AddSeconds(31)), t0.AddSeconds(31));
        }

        steps.Add((runs.Advance(t0.AddSeconds(31)), everySample.Advance(t0.AddSeconds(31))));
        foreach (var engine in new[] { runs, everySample })
        {
            // Within the hand-over window of the run's anchor (applied), so the runs hold it pending.
            engine.Offer(new QueueTrackSeen("D/1", t0.AddSeconds(1.5)), t0.AddSeconds(32));
            for (var k = 0; k < 8; k++)
                engine.Offer(new QueueCrossing("in", CrossingDirection.In, $"D/{k + 3}", t0.AddSeconds(32 + k)), t0.AddSeconds(32 + k));
            engine.BufferedEvents.Should().Be(10);
            engine.Offer(new QueueCrossing("in", CrossingDirection.In, "D/11", t0.AddSeconds(40)), t0.AddSeconds(40));
        }

        runs.Capture().Runs.Should().ContainSingle().Which.PendingUtc.Should().BeNull("the forced advance buffered and applied the pending sample");
        steps.Add((runs.Advance(t0.AddSeconds(40)), everySample.Advance(t0.AddSeconds(40))));
        steps[1].Runs.Rejections.ForcedAdvances.Should().Be(1);
        steps[1].Runs.Rejections.BufferFull.Should().Be(0);
        steps[1].Runs.WatermarkUtc.Should().Be(t0.AddSeconds(10), "the clock (18:00:40) less the lateness");
        foreach (var (withRuns, every) in steps)
            Comparable(withRuns).Should().Be(Comparable(every));
        // D/1 was seen at 01.5, so it is fragmented at 31.5, not at 30.
        var end = runs.Advance(t0.AddMinutes(10));
        Comparable(end).Should().Be(Comparable(everySample.Advance(t0.AddMinutes(10))));
    }

    [Fact]
    public void ZoneRestore_Should_RefuseRuns_When_TheSnapshotIsBeforeVersion11OrARunIsAfterTheZonesClocks()
    {
        var settings = new ZoneProcessorSettings { Engine = new QueueEngineSettings { Lateness = TimeSpan.FromSeconds(10), HandoverWindow = TimeSpan.FromSeconds(30) } };
        var engine = new QueueStateEngine(Hall, settings.Engine);
        var t0 = Midnight.AddHours(8);
        for (var k = 0; k <= 20; k++)
            engine.Offer(new QueueTrackSeen("S-1/1", t0.AddSeconds(k * 0.2)), t0.AddSeconds(k * 0.2));
        engine.Advance(t0.AddSeconds(5));
        var reference = t0.AddSeconds(5);
        var notAfter = t0.AddHours(1);
        var state = new ZoneProcessor("PLE/Q", Hall, 1, settings).Capture() with { Engine = RoundTrip(engine.Capture()), ReferenceUtc = reference };
        var run = state.Engine.Runs.Should().ContainSingle().Which;
        SnapshotChecks.Of(state, "PLE/Q", Hall.QueueZone, 1, notAfter).Should().BeEmpty();
        ZoneProcessor.Restore("PLE/Q", Hall, 1, settings, state, notAfter).Capture().Engine.Runs.Should().ContainSingle();

        foreach (var (bad, because) in new (ZoneProcessorState, string)[]
                 {
                     (state with { Version = ZoneProcessorState.SightingRunsSinceVersion - 1 }, "a snapshot before version 11 carries no runs"),
                     (state with { Engine = state.Engine with { Runs = [run with { PendingUtc = reference.AddSeconds(1) }] } }, "a pending sample after the zone's reference clock"),
                     (state with { Engine = state.Engine with { Runs = [run with { AnchorUtc = notAfter.AddDays(2) }] } }, "an anchor after the host's clock"),
                     (state with { Engine = state.Engine with { Runs = [run with { AnchorUtc = new DateTime(1999, 12, 31, 0, 0, 0, DateTimeKind.Utc) }] } }, "an anchor before 2000")
                 })
        {
            SnapshotChecks.Of(bad, "PLE/Q", Hall.QueueZone, 1, notAfter).Should().NotBeEmpty(because);
            var restore = () => ZoneProcessor.Restore("PLE/Q", Hall, 1, settings, bad, notAfter);
            restore.Should().Throw<InvalidDataException>(because);
        }
    }

    [Fact]
    public void Restore_Should_RefuseARun_When_ItsAnchorIsAfterTheClockOrNotHeld_Or_TheRunsExceedTheirBound()
    {
        var settings = new QueueEngineSettings { Lateness = TimeSpan.FromSeconds(10), HandoverWindow = TimeSpan.FromSeconds(30) };
        var engine = new QueueStateEngine(Hall, settings);
        var t0 = Midnight.AddHours(8);
        engine.Offer(new QueueCrossing("in", CrossingDirection.In, "D/1", t0), t0);
        for (var k = 0; k <= 20; k++)
        {
            engine.Offer(new QueueTrackSeen("D/1", t0.AddSeconds(k * 0.2)), t0.AddSeconds(k * 0.2));
            engine.Offer(new QueueTrackSeen("D/2", t0.AddSeconds(k * 0.2)), t0.AddSeconds(k * 0.2));
        }

        engine.Advance(t0.AddSeconds(4));
        var state = RoundTrip(engine.Capture());
        state.Runs.Should().HaveCount(2);
        state.Runs.Should().OnlyContain(r => r.AnchorUtc == t0 && r.PendingUtc == t0.AddSeconds(4), "each track's first sample anchors its run, its latest is pending");
        QueueStateEngine.Restore(Hall, settings with { MaxSightingRuns = 2 }, state).Capture().Runs.Should().HaveCount(2);
        var run = state.Runs[0];
        var withoutPending = run with { PendingUtc = null, PendingSequence = 0 };

        foreach (var (bad, because, bounds) in new (QueueEngineState, string, QueueEngineSettings)[]
                 {
                     (state, "more runs than MaxSightingRuns", settings with { MaxSightingRuns = 1 }),
                     (state with { Runs = [state.Runs[0] with { PendingUtc = null, PendingSequence = 0 }, state.Runs[1] with { PendingUtc = null, PendingSequence = 0 }] },
                         "more runs than MaxSightingRuns, without pending samples", settings with { MaxSightingRuns = 1 }),
                     (state with { Runs = [withoutPending with { AnchorUtc = t0.AddSeconds(2) }] }, "an anchor after the watermark the buffer does not hold", settings),
                     (state with { Runs = [withoutPending with { AnchorUtc = state.WatermarkUtc.AddTicks(1) }] }, "an anchor one tick after the watermark where no step stopped", settings),
                     (state with { Runs = [withoutPending with { AnchorUtc = state.ClockUtc.Value.AddSeconds(1) }] }, "an anchor after the clock", settings)
                 })
        {
            var restore = () => QueueStateEngine.Restore(Hall, bounds, bad);
            restore.Should().Throw<InvalidDataException>(because);
        }

        // An anchor the watermark has passed needs no place in the buffer: its sample was applied.
        QueueStateEngine.Restore(Hall, settings, state with { Runs = [withoutPending with { AnchorUtc = state.WatermarkUtc }] }).Capture().Runs.Should().ContainSingle();
    }

    [Fact]
    public void Restore_Should_AcceptAnAppliedAnchorAtTheInstantAFullStepStoppedInside_And_ContinueIdentically()
    {
        // Security review, round 2: a step that fills MaxStepRecords inside an instant leaves the watermark one tick before it with
        // part of it applied, so a run anchored at that instant may have its sample applied and still be after the watermark. At
        // 08:00:10 two tracks are sampled; applying the first fragments 1,000 entrants last seen at 08:00:00 (the step's 1,000
        // records), and the step stops before the second.
        var settings = new QueueEngineSettings { Lateness = TimeSpan.Zero, HandoverWindow = TimeSpan.FromSeconds(1), MaxStepRecords = 1_000 };
        var engine = new QueueStateEngine(Hall, settings);
        var t1 = Midnight.AddHours(8);
        var at = t1.AddSeconds(10);
        for (var k = 0; k < 1_000; k++)
        {
            engine.Offer(new QueueCrossing("in", CrossingDirection.In, $"D/{k}", t1), t1);
            engine.Offer(new QueueTrackSeen($"D/{k}", t1), t1);
        }

        engine.Advance(t1);
        engine.Offer(new QueueTrackSeen("E/a", at), at);
        engine.Offer(new QueueTrackSeen("E/b", at), at);
        engine.Advance(at).More.Should().BeTrue("the step is full inside 08:00:10");
        var state = RoundTrip(engine.Capture());
        state.WatermarkUtc.Should().Be(at.AddTicks(-1));
        var applied = state.Runs.Where(r => r.AnchorUtc == at && !state.Buffer.Any(b => b.Input.TrackKey == r.TrackKey)).Should().ContainSingle(
            "one of the two samples was applied before the step stopped").Which;
        var restored = QueueStateEngine.Restore(Hall, settings, state);
        for (var k = 1; k <= 20; k++)
        {
            var next = at.AddSeconds(k * 0.2);
            foreach (var e in new[] { engine, restored })
            {
                e.Offer(new QueueTrackSeen("E/a", next), next);
                e.Offer(new QueueTrackSeen("E/b", next), next);
            }

            Json(restored.Advance(next)).Should().Be(Json(engine.Advance(next)));
        }

        // A run anchored at that instant without its sample in the buffer is accepted; one tick later is after the clock (08:00:10)
        // and refused. An unheld anchor one tick after a watermark where no step stopped is refused in the anchor test above.
        var tracked = applied with { TrackKey = "E/z" };
        QueueStateEngine.Restore(Hall, settings, state with { Runs = [.. state.Runs, tracked] }).Capture().Runs.Should().Contain(r => r.TrackKey == "E/z");
        var later = () => QueueStateEngine.Restore(Hall, settings, state with { Runs = [.. state.Runs, tracked with { AnchorUtc = at.AddTicks(1) }] });
        later.Should().Throw<InvalidDataException>("an anchor after the engine's clock");
    }

    [Fact]
    public void Settings_Should_RefuseRunBoundsOutOfRange_And_CoalesceByDefault()
    {
        new QueueEngineSettings().CoalescePositions.Should().BeTrue("only tests turn coalescing off");
        new QueueEngineSettings { MaxSightingRuns = 0 }.Problems().Should().BeEmpty("0 runs coalesces nothing, as before ARV-230");
        new QueueEngineSettings { MaxSightingRuns = 1_000_000 }.Problems().Should().BeEmpty();
        new QueueEngineSettings { MaxSightingRuns = -1 }.Problems().Should().ContainSingle().Which.Should().StartWith("MaxSightingRuns");
        new QueueEngineSettings { MaxSightingRuns = 1_000_001 }.Problems().Should().ContainSingle().Which.Should().StartWith("MaxSightingRuns");
    }

    #endregion

    #region Helpers

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, EventCatalog.Json);

    /// <summary>A step without its count of held events, which is what coalescing changes.</summary>
    private static string Comparable(QueueStep step) => Json(step with { BufferedEvents = 0 });

    private static QueueEngineState RoundTrip(QueueEngineState state) =>
        JsonSerializer.Deserialize<QueueEngineState>(JsonSerializer.Serialize(state, EventCatalog.Json), EventCatalog.Json);

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
