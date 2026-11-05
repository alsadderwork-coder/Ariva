using System.Text.Json;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Queueing;
using Ariva.Infra.Messaging;
using Ariva.Simulation.Api.Scenarios.Engine;
using FluentAssertions;

namespace Ariva.UnitTests.Queueing;

/// <summary>
/// ARV-240 (CWE-400): each device holds at most its share of the runs of position samples (ARV-230), as of the buffer (ARV-231):
/// the runs' bound over the devices sharing them, at least two, never below the runs' part of <c>DeviceShareFloor</c>. A device
/// flooding unique tracks into a quiet zone so leaves room for the other devices' tracks, and a fast sensor's samples ahead of the
/// clock stay coalesced instead of taking a place each of its share of the ahead tenth (ARV-239), so its crossings are never refused.
/// The share changes no outcome (the steps are those of the engine that applies every sample) and a restore counts each device's
/// runs as the engine that kept going.
/// </summary>
public sealed class QueueEngineRunShareTests
{
    #region Fields

    private static readonly DateTime Midnight = new(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc);

    private static readonly QueueZoneGeometry Hall = new("Q", new HashSet<string> { "in" }, new HashSet<string> { "out" },
        new HashSet<string> { "ov-in" }, new HashSet<string> { "OV" });

    #endregion

    #region Tests

    [Fact]
    public void RunShare_Should_KeepEveryFastSensorsCrossing_When_ADeviceFloodsUniqueTracksIntoAQuietZone()
    {
        // ARV-231's quiet-zone flood (one device alone for a minute, 3,000 unique tracks a second, each a new run) beside six T3
        // sensors 500 ms fast (ARV-239's tolerance case: 30 people each at 5 Hz, their latest samples and crossings ahead of the
        // clock). Each push is a batch the zone steps after, as the zone processor does, so each device's refusals are its own. With
        // one pool of runs the flood used it up before the sensors pushed, their samples ahead of the clock took a place each and
        // their share of the ahead tenth refused their crossings; with a share the flood holds at most half the runs.
        var engine = new QueueStateEngine(Hall, new QueueEngineSettings());
        var fast = TimeSpan.FromMilliseconds(500);
        long entries = 0, exits = 0, sentEntries = 0, sentExits = 0, sensorsRefused = 0, floodRefused = 0, bufferFull = 0;
        var mostFloodRuns = 0;
        var t0 = Midnight.AddHours(2);
        for (var second = 1; second <= 360; second++)
        {
            var received = t0.AddSeconds(second);
            // The zone processor's liveness: the flooding device alone for the first minute, then the seven.
            engine.ZoneDevices = second > 60 ? 7 : 1;
            for (var k = 0; k < 3_000; k++)
                engine.Offer(new QueueTrackSeen($"S-9/{second}.{k}", received.AddMilliseconds(-k / 3)), received, "S-9");
            floodRefused += Tally(engine.Advance(received));
            if (second <= 60)
                continue;
            for (var sensor = 1; sensor <= 6; sensor++)
            {
                var device = $"S-{sensor}";
                for (var track = 0; track < 30; track++)
                {
                    for (var k = 0; k < 5; k++)
                        engine.Offer(new QueueTrackSeen($"{device}/{track}", received.AddMilliseconds(-1000 + (k * 200)) + fast), received, device);
                }

                engine.Offer(new QueueCrossing("in", CrossingDirection.In, $"{device}/p{second}", received.AddMilliseconds(-100) + fast), received, device);
                sentEntries++;
                if (second > 120)
                {
                    engine.Offer(new QueueCrossing("out", CrossingDirection.Out, $"{device}/p{second - 60}", received.AddMilliseconds(-50) + fast), received, device);
                    sentExits++;
                }

                sensorsRefused += Tally(engine.Advance(received));
            }

            if (second % 30 == 0)
            {
                var runs = engine.Capture().Runs;
                mostFloodRuns = Math.Max(mostFloodRuns, runs.Count(r => r.TrackKey.StartsWith("S-9/", StringComparison.Ordinal)));
                if (second > 90)
                    runs.Count(r => !r.TrackKey.StartsWith("S-9/", StringComparison.Ordinal) && !r.TrackKey.Contains("/p", StringComparison.Ordinal))
                        .Should().Be(6 * 30, "every sensor's track holds a run, second {0}", second);
            }
        }

        sensorsRefused += Tally(engine.Advance(t0.AddMinutes(20)));
        sensorsRefused.Should().Be(0, "nothing of the sensors within the clock tolerance is refused (DeviceShareFull or BufferFull)");
        entries.Should().Be(sentEntries, "every fast sensor's entry is counted");
        exits.Should().Be(sentExits, "and every exit");
        floodRefused.Should().BeGreaterThan(0, "the flooding device is refused past its share of the buffer");
        bufferFull.Should().Be(0);
        mostFloodRuns.Should().Be(10_000, "a device alone in a quiet zone holds half the runs, and no more after the others join");

        long Tally(QueueStep step)
        {
            entries += step.Movements.Sum(m => m.Entries);
            exits += step.Movements.Sum(m => m.Exits);
            bufferFull += step.Rejections.BufferFull;
            return step.Rejections.BufferFull + step.Rejections.DeviceShareFull;
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Steps_Should_BeThoseOfAnEngineApplyingEverySample_When_TheRunSharesBind(int seed)
    {
        // ARV-239's random scenarios (device clocks 600 and 250 ms fast and 100 ms slow, pushes offered within 600 ms, samples,
        // crossings, readings and pauses) with a fourth device flooding unique tracks, and the runs bounded at 14 among four devices:
        // each device's share (three) binds, the bound never does, and the steps stay those of the engine that applies every sample.
        var stream = new Mulberry32((uint)seed * 3266489917u);
        int Next(int max) => (int)(stream.Next() * max);
        var settings = new QueueEngineSettings
        {
            Lateness = TimeSpan.FromSeconds(20), HandoverWindow = TimeSpan.FromSeconds(8), CensorAfter = TimeSpan.FromMinutes(4),
            MaxBufferedEvents = 1_000_000, MaxStepRecords = 1_000_000, MaxRememberedTracks = 200, MaxSightingRuns = 14
        };
        var runs = new QueueStateEngine(Hall, settings) { ZoneDevices = 4 };
        var everySample = new QueueStateEngine(Hall, settings with { CoalescePositions = false }) { ZoneDevices = 4 };
        int[] skew = [600, 250, -100];
        var tracks = Enumerable.Range(0, 12).Select(k => (Device: k % 3, Silent: 0)).ToArray();
        var generation = new int[tracks.Length];
        string[] lines = ["in", "out", "ov-in", "elsewhere"];
        var t0 = Midnight.AddHours(17);
        int mostByDevice = 0, mostRuns = 0, floodAtShare = 0;
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

            // The flooding device: two new tracks a tick, some seen again within the hand-over window.
            for (var k = 0; k < 2; k++)
                batch.Add((new QueueTrackSeen($"D9/{tick - Next(3)}.{k}", now.AddMilliseconds(-Next(300))), "D9"));
            var reader = $"D{Next(3)}";
            var readAt = now.AddMilliseconds(skew[reader[1] - '0']);
            if (Next(25) == 0)
                batch.Add((new QueueOccupancy(Next(3) == 0 ? "OV" : "Q", Next(6), readAt), reader));
            if (Next(60) == 0)
                batch.Add((new QueueInterval(lines[Next(3)], Next(4), Next(3), readAt.AddSeconds(-30 - Next(30)), readAt), reader));
            if (Next(40) == 0)
                batch.Add((new QueueCrossing(lines[Next(4)], Next(4) == 0 ? CrossingDirection.Out : CrossingDirection.In, null, readAt), reader));

            var reference = now.AddMilliseconds(Next(4) * 200);
            foreach (var (input, device) in batch.OrderBy(_ => Next(1000)))
            {
                runs.Offer(input, reference, device);
                everySample.Offer(input, reference, device);
            }

            if (tick % 5 == 0)
            {
                var held = runs.Capture().Runs;
                var byDevice = held.GroupBy(h => h.TrackKey[..h.TrackKey.IndexOf('/', StringComparison.Ordinal)]).ToDictionary(g => g.Key, g => g.Count());
                mostByDevice = Math.Max(mostByDevice, byDevice.Values.DefaultIfEmpty().Max());
                mostRuns = Math.Max(mostRuns, held.Count);
                floodAtShare += byDevice.GetValueOrDefault("D9") == 3 ? 1 : 0;
                Comparable(runs.Advance(now)).Should().Be(Comparable(everySample.Advance(now)), "seed {0}, second {1}", seed, tick / 5);
                runs.BufferedEvents.Should().BeLessThanOrEqualTo(everySample.BufferedEvents);
            }
        }

        var end = t0.AddMinutes(30);
        Comparable(runs.Advance(end)).Should().Be(Comparable(everySample.Advance(end)));
        mostByDevice.Should().Be(3, "no device holds more than its share of the runs (14 over four devices)");
        mostRuns.Should().BeLessThan(settings.MaxSightingRuns, "the shares bind before the runs' bound");
        floodAtShare.Should().BeGreaterThan(100, "the flooding device is held at its share");
        runs.BufferedEvents.Should().Be(0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Engine_Should_ContinueIdentically_When_RestoredAtRandomPointsWithTheRunSharesBinding(int seed)
    {
        // A restore counts each device's runs from their keys, as the engine that kept going counts them: restored at random
        // points, the engine holds the same runs (the same tracks coalesced, the same left to the buffer) and gives the same steps.
        var stream = new Mulberry32((uint)seed * 2654435761u);
        int Next(int max) => (int)(stream.Next() * max);
        var settings = new QueueEngineSettings { Lateness = TimeSpan.FromSeconds(15), HandoverWindow = TimeSpan.FromSeconds(6), MaxSightingRuns = 9 };
        var straight = new QueueStateEngine(Hall, settings) { ZoneDevices = 3 };
        var restored = new QueueStateEngine(Hall, settings) { ZoneDevices = 3 };
        var t0 = Midnight.AddHours(9);
        var restores = 0;
        var atShare = 0;
        for (var tick = 0; tick < 6 * 60 * 5; tick++)
        {
            var now = t0.AddSeconds(tick * 0.2);
            for (var k = 0; k < 12; k++)
            {
                var device = $"D{k % 3}";
                var key = $"{device}/{k}-{tick / 400}";
                QueueInput input = Next(200) switch
                {
                    0 => new QueueCrossing("in", CrossingDirection.In, key, now),
                    1 => new QueueCrossing("out", CrossingDirection.Out, key, now),
                    < 6 => new QueueOccupancy("Q", Next(5), now),
                    // One device also floods new tracks, so its share binds whatever the others do.
                    < 40 when device == "D2" => new QueueTrackSeen($"D2/f{tick}.{k}", now.AddMilliseconds(-Next(900))),
                    _ => new QueueTrackSeen(key, now.AddMilliseconds(-Next(900)))
                };
                if (Next(30) == 0)
                    continue;
                straight.Offer(input, now, device);
                restored.Offer(input, now, device);
            }

            if (Next(40) == 0)
            {
                // Not part of the snapshot: the zone processor sets it before each batch.
                restored = QueueStateEngine.Restore(Hall, settings, RoundTrip(restored.Capture()));
                restored.ZoneDevices = 3;
                restores++;
            }

            if (tick % 10 == 0)
            {
                Json(restored.Advance(now)).Should().Be(Json(straight.Advance(now)), "seed {0}, tick {1}", seed, tick);
                var held = straight.Capture().Runs;
                Json(restored.Capture().Runs).Should().Be(Json(held), "the same runs, seed {0}, tick {1}", seed, tick);
                atShare += held.Count(r => r.TrackKey.StartsWith("D2/", StringComparison.Ordinal)) == 3 ? 1 : 0;
            }
        }

        restores.Should().BeGreaterThan(20);
        atShare.Should().BeGreaterThan(20, "the flooding device holds its share of the runs (9 over three devices) at many restores");
    }

    [Fact]
    public void RunShare_Should_HoldALoneDeviceToHalfTheRuns_And_TakeItsFloorFromTheDeviceShareFloor()
    {
        // A device alone in a quiet zone shares the runs with at least one other (ARV-231's rule).
        Runs(new QueueEngineSettings(), 0, 15_000).Should().Be(10_000, "a lone device holds half the runs");
        // Shared over the zone's devices, never below the runs' part of DeviceShareFloor (20,000 x 10,000 / 100,000 = 2,000).
        Runs(new QueueEngineSettings(), 5, 15_000).Should().Be(4_000, "the runs over the zone's five devices");
        Runs(new QueueEngineSettings(), 20, 15_000).Should().Be(2_000, "the floor, above the runs over twenty devices");
        Runs(new QueueEngineSettings { MaxSightingRuns = 1_000 }, 50, 900).Should().Be(100, "the floor in proportion: 1,000 x 10,000 / 100,000");
        Runs(new QueueEngineSettings { MaxSightingRuns = 1_000, MaxBufferedEvents = 50_000 }, 50, 900).Should().Be(200, "the floor over the buffer's bound");
        Runs(new QueueEngineSettings { MaxSightingRuns = 9 }, 20, 5).Should().Be(1, "never below one run (9 x 10,000 / 100,000 is 0)");
        // A floor at the buffer's bound turns the runs' shares off, as it turns off the buffer's.
        Runs(new QueueEngineSettings { DeviceShareFloor = 100_000 }, 0, 15_000).Should().Be(15_000, "no share: the runs' bound alone");
        // A track of no device (its key names none) is held to the runs' bound alone, and the runs stop there.
        var anonymous = new QueueStateEngine(Hall, new QueueEngineSettings());
        var now = Midnight.AddHours(5);
        for (var k = 0; k < 25_000; k++)
            anonymous.Offer(new QueueTrackSeen($"t{k}", now), now);
        anonymous.Capture().Runs.Should().HaveCount(20_000, "the runs' bound (MaxSightingRuns)");

        static int Runs(QueueEngineSettings settings, int zoneDevices, int tracks)
        {
            var engine = new QueueStateEngine(Hall, settings) { ZoneDevices = zoneDevices };
            var at = Midnight.AddHours(5);
            for (var k = 0; k < tracks; k++)
                engine.Offer(new QueueTrackSeen($"S-1/{k}", at), at, "S-1");
            return engine.Capture().Runs.Count;
        }
    }

    [Fact]
    public void RunShare_Should_DivideTheRunsOverTheDevicesHoldingThem_When_TheyAreMoreThanTheZonesDevices()
    {
        // Two devices hold a run each; a third shares the 900 runs with them (300 each, the floor 90), not with one other.
        var engine = new QueueStateEngine(Hall, new QueueEngineSettings { MaxSightingRuns = 900 });
        var now = Midnight.AddHours(6);
        engine.Offer(new QueueTrackSeen("S-1/a", now), now, "S-1");
        engine.Offer(new QueueTrackSeen("S-2/a", now), now, "S-2");
        for (var k = 0; k < 600; k++)
            engine.Offer(new QueueTrackSeen($"S-3/{k}", now), now, "S-3");
        var runs = engine.Capture().Runs;
        runs.Count(r => r.TrackKey.StartsWith("S-3/", StringComparison.Ordinal)).Should().Be(300);
        runs.Should().HaveCount(302);
    }

    [Fact]
    public void RunShare_Should_FreeADevicesRuns_When_TheyEnd_And_KeepItsCountThroughARestore()
    {
        var settings = new QueueEngineSettings();
        var engine = new QueueStateEngine(Hall, settings);
        var now = Midnight.AddHours(7);
        for (var k = 0; k < 10_000; k++)
            engine.Offer(new QueueTrackSeen($"S-1/{k}", now), now, "S-1");
        engine.Capture().Runs.Should().HaveCount(10_000);

        // A restore counts the device's runs: its further tracks get none.
        var restored = QueueStateEngine.Restore(Hall, settings, RoundTrip(engine.Capture()));
        for (var k = 10_000; k < 15_000; k++)
            restored.Offer(new QueueTrackSeen($"S-1/{k}", now), now, "S-1");
        restored.Capture().Runs.Should().HaveCount(10_000, "the restored engine counts the runs it holds for S-1");

        // Once the watermark passes the runs' windows they end and free the device's share.
        var later = now.AddMinutes(10);
        restored.Advance(later);
        restored.Capture().Runs.Should().BeEmpty();
        for (var k = 0; k < 15_000; k++)
            restored.Offer(new QueueTrackSeen($"S-1/n{k}", later), later, "S-1");
        restored.Capture().Runs.Should().HaveCount(10_000, "the ended runs are no longer counted");
    }

    #endregion

    #region Methods

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, EventCatalog.Json);

    private static string Comparable(QueueStep step) => Json(step with { BufferedEvents = 0 });

    private static QueueEngineState RoundTrip(QueueEngineState state) =>
        JsonSerializer.Deserialize<QueueEngineState>(JsonSerializer.Serialize(state, EventCatalog.Json), EventCatalog.Json);

    #endregion
}
