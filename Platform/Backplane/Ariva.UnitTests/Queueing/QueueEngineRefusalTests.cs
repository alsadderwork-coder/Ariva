using System.Text.Json;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Queueing;
using Ariva.Core.Sensing;
using Ariva.Infra.Messaging;
using Ariva.Infra.Streaming;
using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.UnitTests.Queueing;

/// <summary>
/// ARV-231 (CWE-400, CWE-778): what the queue engine refuses is visible and bounded per device. Each device holds at most its
/// share of the reorder buffer, so one flooding device cannot make the zone refuse the other devices' crossings; the minutes a
/// full buffer touched are named, and the zone marks its live minutes degraded for them; the zone's outputs carry the refusals by
/// kind for the stream's counter and its warnings, at most one a minute per zone and kind; and the shares survive a snapshot.
/// </summary>
public sealed class QueueEngineRefusalTests
{
    #region Fields

    private static readonly DateTime Midnight = new(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc);

    private static readonly QueueZoneGeometry Hall = new("Q", new HashSet<string> { "in" }, new HashSet<string> { "out" },
        new HashSet<string>(), new HashSet<string>());

    #endregion

    #region Tests

    [Fact]
    public void Share_Should_KeepEveryOtherDevicesCrossings_When_OneDeviceOfTheZoneFloodsIt()
    {
        // Six sensors each count two people in and two out a second; a seventh sends 2,000 positions a second of tracks never seen
        // again, for five minutes. With the shares the flood is refused alone; with a share as large as the buffer (no share) the
        // flood fills the buffer and the zone refuses the others' crossings too.
        var settings = new QueueEngineSettings { MaxBufferedEvents = 20_000, DeviceShareFloor = 1_000 };
        var shared = Run(settings);
        var unshared = Run(settings with { DeviceShareFloor = settings.MaxBufferedEvents });

        shared.Rejections.BufferFull.Should().Be(0);
        shared.Rejections.DeviceShareFull.Should().BeGreaterThan(500_000, "the flooding device is refused past its share");
        shared.Entries.Should().Be(shared.SentEntries, "every other device's entry is counted");
        shared.Exits.Should().Be(shared.SentExits, "and every exit");
        unshared.Rejections.DeviceShareFull.Should().Be(0);
        unshared.Rejections.BufferFull.Should().BeGreaterThan(0);
        unshared.Entries.Should().BeLessThan(unshared.SentEntries, "without shares the flood costs the zone its crossings");

        static (QueueRejections Rejections, long Entries, long Exits, long SentEntries, long SentExits) Run(QueueEngineSettings settings)
        {
            var engine = new QueueStateEngine(Hall, settings);
            long entries = 0, exits = 0, sentEntries = 0, sentExits = 0;
            long bufferFull = 0, shareFull = 0;
            var t0 = Midnight.AddHours(18);
            for (var second = 1; second <= 300; second++)
            {
                var now = t0.AddSeconds(second);
                for (var sensor = 1; sensor <= 6; sensor++)
                {
                    var device = $"S-{sensor}";
                    for (var person = 0; person < 2; person++)
                    {
                        engine.Offer(new QueueCrossing("in", CrossingDirection.In, $"{device}/{second}-{person}", now), now, device);
                        sentEntries++;
                        if (second > 60)
                        {
                            engine.Offer(new QueueCrossing("out", CrossingDirection.Out, $"{device}/{second - 60}-{person}", now), now, device);
                            sentExits++;
                        }
                    }
                }

                for (var k = 0; k < 2_000; k++)
                    engine.Offer(new QueueTrackSeen($"S-9/{second}.{k}", now.AddMilliseconds(-k / 2)), now, "S-9");
                var step = engine.Advance(now);
                (entries, exits) = (entries + step.Movements.Sum(m => m.Entries), exits + step.Movements.Sum(m => m.Exits));
                (bufferFull, shareFull) = (bufferFull + step.Rejections.BufferFull, shareFull + step.Rejections.DeviceShareFull);
            }

            var end = engine.Advance(t0.AddMinutes(20));
            return (new QueueRejections { BufferFull = bufferFull + end.Rejections.BufferFull, DeviceShareFull = shareFull + end.Rejections.DeviceShareFull },
                entries + end.Movements.Sum(m => m.Entries), exits + end.Movements.Sum(m => m.Exits), sentEntries, sentExits);
        }
    }

    [Fact]
    public void Share_Should_LeaveRoomForTheOtherDevices_When_ADeviceFloodsTheZoneBeforeTheyHoldAnything()
    {
        // Security review M1: the flooding device starts alone, a minute before the six sensors push anything (a quiet zone at
        // night, after an outage, at start). It never holds more than half the buffer, so the sensors that join find room.
        var engine = new QueueStateEngine(Hall, new QueueEngineSettings());
        long entries = 0, exits = 0, sentEntries = 0, sentExits = 0, bufferFull = 0, shareFull = 0;
        var t0 = Midnight.AddHours(2);
        for (var second = 1; second <= 360; second++)
        {
            var now = t0.AddSeconds(second);
            for (var k = 0; k < 3_000; k++)
                engine.Offer(new QueueTrackSeen($"S-9/{second}.{k}", now.AddMilliseconds(-k / 3)), now, "S-9");
            if (second > 60)
            {
                for (var sensor = 1; sensor <= 6; sensor++)
                {
                    var device = $"S-{sensor}";
                    engine.Offer(new QueueCrossing("in", CrossingDirection.In, $"{device}/{second}", now), now, device);
                    sentEntries++;
                    if (second > 120)
                    {
                        engine.Offer(new QueueCrossing("out", CrossingDirection.Out, $"{device}/{second - 60}", now), now, device);
                        sentExits++;
                    }
                }
            }

            Tally(engine.Advance(now));
        }

        Tally(engine.Advance(t0.AddMinutes(20)));
        bufferFull.Should().Be(0, "no device is refused for a full buffer");
        shareFull.Should().BeGreaterThan(0, "the flooding device is refused past its share");
        entries.Should().Be(sentEntries, "every entry of the sensors that joined is counted");
        exits.Should().Be(sentExits);

        void Tally(QueueStep step)
        {
            entries += step.Movements.Sum(m => m.Entries);
            exits += step.Movements.Sum(m => m.Exits);
            bufferFull += step.Rejections.BufferFull;
            shareFull += step.Rejections.DeviceShareFull;
        }
    }

    [Fact]
    public void Engine_Should_KeepEachDevicesShare_When_RestoredWithPendingSamples()
    {
        // Security review M2: a device whose tracks hold pending samples, with its share binding. Restored at random points, the
        // engine refuses exactly what the one that kept going refuses.
        var settings = new QueueEngineSettings { MaxBufferedEvents = 2_000, DeviceShareFloor = 100, Lateness = TimeSpan.FromSeconds(60) };
        var straight = new QueueStateEngine(Hall, settings);
        var restored = new QueueStateEngine(Hall, settings);
        var t0 = Midnight.AddHours(14);
        long refusedShare = 0;
        var restores = 0;
        for (var tick = 1; tick <= 5 * 300; tick++)
        {
            var now = t0.AddSeconds(tick * 0.2);
            foreach (var engine in new[] { straight, restored })
            {
                for (var track = 0; track < 300; track++)
                    engine.Offer(new QueueTrackSeen($"S-1/{track}", now), now, "S-1");
                for (var track = 0; track < 20; track++)
                    engine.Offer(new QueueTrackSeen($"S-2/{track}", now), now, "S-2");
                for (var k = 0; k < 10; k++)
                    engine.Offer(new QueueTrackSeen($"S-9/{tick}.{k}", now), now, "S-9");
            }

            if (tick % 53 == 0)
            {
                restored = QueueStateEngine.Restore(Hall, settings, RoundTrip(restored.Capture()));
                restores++;
            }

            if (tick % 5 == 0)
            {
                var step = straight.Advance(now);
                refusedShare += step.Rejections.DeviceShareFull;
                Json(restored.Advance(now)).Should().Be(Json(step), "tick {0}", tick);
            }
        }

        restores.Should().BeGreaterThan(20);
        refusedShare.Should().BeGreaterThan(0, "the shares bind in this run");
        straight.Capture().Buffer.Count(b => b.Input.TrackKey!.StartsWith("S-1/", StringComparison.Ordinal)).Should().BeGreaterThan(100, "S-1 holds its share");
        straight.Capture().Runs.Should().Contain(r => r.PendingUtc != null, "runs hold pending samples");
    }

    [Fact]
    public void Shares_Should_KeepEveryCrossingOfASensorWithinTheClockTolerance_When_ItsLatestSamplesAreAheadOfTheClock()
    {
        // Security review M3: Ingest leaves a clock within 500 ms uncorrected, so a T3 sensor 500 ms fast stamps the latest samples
        // of each push after its receive time: they are ahead of the clock, held one place each. In a zone of seven devices it
        // keeps every crossing (the tenth for events ahead of the clock is the zone's, not a share per device).
        var engine = new QueueStateEngine(Hall, new QueueEngineSettings()) { ZoneDevices = 7 };
        var fast = TimeSpan.FromMilliseconds(500);
        long entries = 0, exits = 0, sentEntries = 0, sentExits = 0, refused = 0;
        var t0 = Midnight.AddHours(16);
        for (var second = 1; second <= 300; second++)
        {
            var received = t0.AddSeconds(second);
            for (var track = 0; track < 30; track++)
            {
                for (var k = 0; k < 5; k++)
                    engine.Offer(new QueueTrackSeen($"S-1/{track}", received.AddMilliseconds(-1000 + (k * 200)) + fast), received, "S-1");
            }

            engine.Offer(new QueueCrossing("in", CrossingDirection.In, $"S-1/p{second}", received.AddMilliseconds(-100) + fast), received, "S-1");
            sentEntries++;
            if (second > 60)
            {
                engine.Offer(new QueueCrossing("out", CrossingDirection.Out, $"S-1/p{second - 60}", received.AddMilliseconds(-50) + fast), received, "S-1");
                sentExits++;
            }

            Tally(engine.Advance(received));
        }

        Tally(engine.Advance(t0.AddMinutes(20)));
        refused.Should().Be(0, "nothing of a sensor within the clock tolerance is refused");
        entries.Should().Be(sentEntries);
        exits.Should().Be(sentExits);

        void Tally(QueueStep step)
        {
            entries += step.Movements.Sum(m => m.Entries);
            exits += step.Movements.Sum(m => m.Exits);
            refused += step.Rejections.BufferFull + step.Rejections.DeviceShareFull;
        }
    }

    [Fact]
    public void Rejections_Should_NameTheMinutesAFullBufferShortened_When_EventsAreRefusedOrAppliedEarly()
    {
        var engine = new QueueStateEngine(Hall, new QueueEngineSettings { Lateness = TimeSpan.FromSeconds(30), MaxBufferedEvents = 10 });
        var t0 = Midnight.AddHours(9);
        for (var k = 0; k < 10; k++)
            engine.Offer(new QueueCrossing("in", CrossingDirection.In, null, t0.AddSeconds(k)), t0.AddSeconds(k));

        // Full: the one at 09:00:00 was due, so a forced advance applies it early and makes room.
        var later = t0.AddMinutes(5);
        engine.Offer(new QueueCrossing("in", CrossingDirection.In, null, later), later);
        // Ahead of the clock: full again, so the next due event (09:00:01) is applied early too; a tenth of the buffer (one event)
        // holds events ahead of the clock, so the second is refused.
        engine.Offer(new QueueCrossing("in", CrossingDirection.In, null, later.AddMinutes(3)), later);
        engine.Offer(new QueueCrossing("in", CrossingDirection.In, null, later.AddMinutes(3.5)), later);
        var step = engine.Advance(later);

        step.Rejections.ForcedAdvances.Should().Be(2);
        step.Rejections.BufferFull.Should().Be(1);
        step.Rejections.FullBufferMinutes.Should().Equal([t0, later.AddMinutes(3)],
            "09:00 was applied before its lateness was up, and 09:08 lost an event");
        step.Rejections.Counts().Should().BeEquivalentTo([("BufferFull", 1L), ("ForcedAdvances", 2L)]);
        engine.Advance(later).Rejections.FullBufferMinutes.Should().BeEmpty("each step names the minutes since the previous one");
    }

    [Fact]
    public void Zone_Should_MarkTheLiveMinuteDegradedAndReportTheRefusals_When_AFullBufferRefusedItsEvents()
    {
        // The same pushes twice; one zone also gets two crossings 2 minutes ahead of the clock, the second refused (a tenth of a
        // buffer of 10 holds one). Only that zone marks the minute the refused crossing belongs to, and it alone has refusals.
        var settings = new ZoneProcessorSettings { Engine = new QueueEngineSettings { Lateness = TimeSpan.FromSeconds(30), MaxBufferedEvents = 10 } };
        var plain = new ZoneProcessor("PLE/Q", Hall, 1, settings);
        var refused = new ZoneProcessor("PLE/Q", Hall, 1, settings);
        var t0 = Midnight.AddHours(10);
        var ahead = t0.AddMinutes(2).AddSeconds(30);
        for (var second = 0; second <= 300; second += 5)
        {
            var now = t0.AddSeconds(second);
            foreach (var zone in new[] { plain, refused })
                zone.Offer(Push(now, ("in", now)), now);
            if (second == 10)
                refused.Offer(Push(now, ("in", ahead), ("in", ahead.AddSeconds(1))), now);
        }

        var minute = new DateTime(ahead.Ticks - ahead.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        var outputs = refused.Peek();
        var twin = plain.Peek().Live.ToDictionary(l => l.MinuteUtc);
        outputs.Refusals.Should().ContainSingle().Which.Should().Be(new QueueRefusalCount("BufferFull", 1));
        outputs.Live.Should().Contain(l => l.MinuteUtc == minute && l.LengthDegraded && l.NowcastDegraded, "the minute the refused crossing belongs to");
        twin[minute].LengthDegraded.Should().BeFalse("the zone without the refusal does not mark it");
        twin[minute].NowcastDegraded.Should().BeFalse();
        outputs.Live.Where(l => l.MinuteUtc != minute).Select(l => (l.MinuteUtc, l.LengthDegraded, l.NowcastDegraded))
            .Should().Equal(twin.Values.Where(l => l.MinuteUtc != minute).Select(l => (l.MinuteUtc, l.LengthDegraded, l.NowcastDegraded)), "every other minute as without it");
        plain.Peek().Refusals.Should().BeEmpty();

        refused.Acknowledge(outputs);
        refused.Peek().Refusals.Should().BeEmpty("acknowledged once the checkpoint wrote them");
        outputs.Count.Should().Be(outputs.Minutes.Count + outputs.Bins.Count + outputs.Live.Count + outputs.Recomputations.Count + outputs.Outages.Count +
            outputs.Lines.Count + outputs.Health.Count + outputs.Overflow.Count + outputs.OverflowChanges.Count + outputs.DeskReadings.Count,
            "refusals are not rows to write");
    }

    [Fact]
    public void Engine_Should_ContinueIdenticallyWithItsShares_When_RestoredDuringAFlood()
    {
        // The shares travel in the snapshot: an anonymous event carries its device, a tracked one is named by its key.
        var settings = new QueueEngineSettings { MaxBufferedEvents = 2_000, DeviceShareFloor = 100 };
        var straight = new QueueStateEngine(Hall, settings);
        var restored = new QueueStateEngine(Hall, settings);
        var t0 = Midnight.AddHours(11);
        QueueEngineState captured = null;
        for (var second = 1; second <= 240; second++)
        {
            var now = t0.AddSeconds(second);
            foreach (var engine in new[] { straight, restored })
            {
                engine.Offer(new QueueCrossing("in", CrossingDirection.In, null, now), now, "S-1");
                engine.Offer(new QueueCrossing("in", CrossingDirection.In, $"S-2/{second}", now), now, "S-2");
                for (var k = 0; k < 50; k++)
                    engine.Offer(new QueueTrackSeen($"S-9/{second}.{k}", now), now, "S-9");
            }

            if (second % 37 == 0)
            {
                captured = RoundTrip(restored.Capture());
                restored = QueueStateEngine.Restore(Hall, settings, captured);
            }

            Json(restored.Advance(now)).Should().Be(Json(straight.Advance(now)), "second {0}", second);
        }

        captured.Should().NotBeNull();
        captured.Buffer.Where(b => b.Input.TrackKey is null).Should().OnlyContain(b => b.Device == "S-1", "an anonymous event carries its device");
        captured.Buffer.Where(b => b.Input.TrackKey is not null).Should().OnlyContain(b => b.Device == null, "a tracked event's key names its device");
        captured.Counters.Should().HaveCount(12);
        JsonSerializer.Serialize(captured.Buffer.First(b => b.Input.TrackKey is not null), EventCatalog.Json).Should().NotContain("device",
            "nothing is written for a device the key names");
    }

    [Fact]
    public void Restore_Should_RefuseShares_When_TheSnapshotIsBeforeVersion12OrNamesNoDeviceCode()
    {
        var settings = new ZoneProcessorSettings { Engine = new QueueEngineSettings { Lateness = TimeSpan.FromSeconds(30), MaxBufferedEvents = 10 } };
        var zone = new ZoneProcessor("PLE/Q", Hall, 1, settings);
        var t0 = Midnight.AddHours(12);
        var ahead = t0.AddMinutes(3);
        zone.Offer(Push(t0, ("in", t0), ("in", ahead), ("in", ahead.AddSeconds(1))), t0);
        var state = RoundTrip(zone.Capture());
        var notAfter = t0.AddHours(1);
        state.Version.Should().Be(12);
        state.Engine.Buffer.Should().Contain(b => b.Device == "S-1");
        ZoneProcessor.Restore("PLE/Q", Hall, 1, settings, state, notAfter).Capture().Engine.Buffer.Should().HaveCount(state.Engine.Buffer.Count);
        var minute = state.FullBufferMinutes.Should().ContainSingle().Subject;

        foreach (var (bad, because) in new (ZoneProcessorState, string)[]
                 {
                     (state with { Version = 11 }, "a version 11 snapshot carries no devices or full-buffer minutes"),
                     (state.WrittenBefore12() with { Version = 11, FullBufferMinutes = [minute] }, "nor the zone's full-buffer minutes"),
                     (state.WrittenBefore12() with { Version = 11, Engine = state.WrittenBefore12().Engine with { Counters = [.. Enumerable.Repeat(0L, 12)] } },
                         "nor a twelfth counter"),
                     (state with { FullBufferMinutes = [minute.AddSeconds(1)] }, "a full-buffer minute that is not a whole minute"),
                     (state with { FullBufferMinutes = [.. Enumerable.Range(0, 2 * 24 * 60 + 1).Select(k => minute.AddMinutes(-k))] }, "more minutes than a zone keeps"),
                     (state with { Engine = state.Engine with { Buffer = [.. state.Engine.Buffer.Select(b => b with { Device = b.Device is null ? null : "s 1" })] } },
                         "a device that is not a device code")
                 })
        {
            var restore = () => ZoneProcessor.Restore("PLE/Q", Hall, 1, settings, bad, notAfter);
            restore.Should().Throw<InvalidDataException>(because);
        }

        // As an earlier build wrote it, the snapshot restores: its tracked events counted for their devices, its anonymous ones for none.
        ZoneProcessor.Restore("PLE/Q", Hall, 1, settings, state.WrittenBefore12() with { Version = 11 }, notAfter).ZoneKey.Should().Be("PLE/Q");
    }

    [Fact]
    public void Settings_Should_RefuseAShareFloorOutOfRange()
    {
        new QueueEngineSettings().DeviceShareFloor.Should().Be(10_000);
        new QueueEngineSettings { DeviceShareFloor = 1 }.Problems().Should().BeEmpty();
        new QueueEngineSettings { DeviceShareFloor = 0 }.Problems().Should().ContainSingle().Which.Should().StartWith("DeviceShareFloor");
        new QueueEngineSettings { DeviceShareFloor = 1_000_001 }.Problems().Should().ContainSingle().Which.Should().StartWith("DeviceShareFloor");
    }

    [Fact]
    public void Warnings_Should_ComeAtMostOnceAMinutePerZoneAndKind_When_RefusalsKeepComing()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 22, 13, 0, 0, TimeSpan.Zero));
        var warnings = new RefusalWarnings(clock);
        warnings.Add("PLE/Q", [new QueueRefusalCount("BufferFull", 5), new QueueRefusalCount("Late", 0)]);
        warnings.Due().Should().Equal([("PLE/Q", "BufferFull", 5L)], "the first warning comes at once, a kind with none is not one");

        warnings.Add("PLE/Q", [new QueueRefusalCount("BufferFull", 3)]);
        warnings.Add("PLE/R", [new QueueRefusalCount("BufferFull", 2)]);
        warnings.Due().Should().Equal([("PLE/R", "BufferFull", 2L)], "another zone warns on its own; PLE/Q waits for its minute");

        clock.Advance(TimeSpan.FromSeconds(59));
        warnings.Due().Should().BeEmpty();
        clock.Advance(TimeSpan.FromSeconds(1));
        warnings.Due().Should().Equal([("PLE/Q", "BufferFull", 3L)], "a quiet minute later, the refusals since the last warning");

        clock.Advance(TimeSpan.FromMinutes(2));
        warnings.Due().Should().BeEmpty("nothing new");
        warnings.Add("PLE/Q", [new QueueRefusalCount("BufferFull", 1), new QueueRefusalCount("DeviceShareFull", 4)]);
        warnings.Due().Should().Equal([("PLE/Q", "BufferFull", 1L), ("PLE/Q", "DeviceShareFull", 4L)], "each kind on its own");
    }

    [Fact]
    public void Counter_Should_AddTheRefusalsByKindAndSite_When_TheWorkerReportsThem()
    {
        using var metrics = new StreamMetrics();
        using var reader = new InstrumentReader(metrics.Meter);
        metrics.Refused("PLE", "BufferFull", 7);
        metrics.Refused("PLE", "BufferFull", 2);
        metrics.Refused("AUH-TA", "DeviceShareFull", 3);
        metrics.Refused("PLE", "Late", 0);

        reader.Sum(StreamMetrics.QueueRefusedName, ("kind", "BufferFull"), ("site", "PLE")).Should().Be(9);
        reader.Sum(StreamMetrics.QueueRefusedName, ("kind", "DeviceShareFull"), ("site", "AUH-TA")).Should().Be(3);
        reader.Sum(StreamMetrics.QueueRefusedName, ("kind", "Late"), ("site", "PLE")).Should().Be(0, "nothing to add");
    }

    #endregion

    #region Helpers

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, EventCatalog.Json);

    private static T RoundTrip<T>(T state) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(state, EventCatalog.Json), EventCatalog.Json);

    private static VendorLineCrossingBatch Push(DateTime received, params (string Line, DateTime At)[] crossings) => new()
    {
        Id = Guid.NewGuid(),
        DeviceId = Guid.NewGuid(),
        DeviceCode = "S-1",
        SiteCode = "PLE",
        QueueZoneName = "Q",
        Dialect = "canonical",
        Commissioned = true,
        ReceivedUtc = received,
        Clock = new ClockReading(0, true, ClockState.Ok),
        Crossings = [.. crossings.Select(c => new Sensed<LineCrossing>(new LineCrossing(c.Line, CrossingDirection.In, null, c.At), c.At, SensedFlags.None))]
    };

    #endregion
}
