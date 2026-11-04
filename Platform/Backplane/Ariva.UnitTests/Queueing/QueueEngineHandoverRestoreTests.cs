using System.Text.Json;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Queueing;
using Ariva.Infra.Messaging;
using FluentAssertions;

namespace Ariva.UnitTests.Queueing;

/// <summary>
/// ARV-238 (CWE-501, CWE-120): the queue engine's restore takes a hand-over deadline only as the engine sets one (its sighting plus
/// the hand-over window, at a sighting it applied, at most MaxAhead after its clock, never after its entrant's last sighting) and a
/// held entrant's last sighting only as the engine sets it (none without a track, at or after its entry, applied, at most MaxAhead
/// after the clock), the instant a full step stopped inside counting as applied; a zone snapshot of version 9 or later without the
/// engine's clock is refused, one written before version 9 has its events ahead of the clock bounded a lateness past the clock it
/// derives, and no buffered event not marked ahead may be after the zone's reference. Genuine snapshots still restore and continue as
/// the engine did.
/// </summary>
public sealed class QueueEngineHandoverRestoreTests
{
    #region Fields

    private static readonly DateTime Midnight = new(2026, 11, 4, 0, 0, 0, DateTimeKind.Utc);

    private static readonly QueueZoneGeometry Hall = new("Q", new HashSet<string> { "in" }, new HashSet<string> { "out" },
        new HashSet<string> { "ov-in" }, new HashSet<string> { "OV" });

    private static readonly QueueEngineSettings Settings = new()
    {
        Lateness = TimeSpan.FromSeconds(10), HandoverWindow = TimeSpan.FromSeconds(30), CoalescePositions = false
    };

    #endregion

    #region Hand-overs

    [Fact]
    public void Restore_Should_RefuseAHandover_When_ItIsNotOneTheEngineSet()
    {
        var (engine, t0) = TrackedEntrant();
        var state = RoundTrip(engine.Capture());
        var live = state.Handovers.Should().Contain(h => h.Seen == t0.AddSeconds(10)).Which;
        live.Deadline.Should().Be(t0.AddSeconds(40), "the engine sets a sighting plus the hand-over window");
        var clock = state.ClockUtc.Should().NotBeNull().And.Subject.Value;
        Handover(live.Key, state.WatermarkUtc.AddSeconds(-1)).Seen.Should().BeAfter(live.Seen, "the case after the entrant's last sighting is applied");

        foreach (var (bad, because) in new (QueueEngineState, string)[]
                 {
                     (state with { Handovers = [live with { Deadline = live.Deadline.AddSeconds(1) }] }, "a deadline past its sighting plus the window"),
                     (state with { Handovers = [live with { Deadline = live.Seen }] }, "a deadline at its sighting"),
                     (state with { Handovers = [Handover(live.Key, state.WatermarkUtc.AddSeconds(1))] }, "a sighting the engine had not applied"),
                     (state with { Handovers = [Handover(live.Key, clock.Add(Settings.MaxAhead).AddSeconds(1))] }, "a sighting further after the clock than MaxAhead"),
                     (state with { Handovers = [Handover(live.Key, state.WatermarkUtc.AddSeconds(-1))] }, "a sighting after the entrant's last one"),
                     // A track no entrant holds, so the entrant's rule cannot be what refuses it.
                     (state with { Handovers = [Handover("Z/9", state.WatermarkUtc.AddSeconds(1))] }, "a sighting the engine had not applied, of a track nobody holds"),
                     // X/9's buffered time: first in the buffer, but no step stopped there (the watermark is not one tick before it).
                     (state with { Handovers = [Handover("Z/9", t0.AddSeconds(40))] }, "a sighting at the first buffered event's time, where no step stopped")
                 })
        {
            var restore = () => QueueStateEngine.Restore(Hall, Settings, RoundTrip(bad));
            restore.Should().Throw<InvalidDataException>(because);
        }
    }

    [Fact]
    public void Restore_Should_RefuseAHandoverSightedFurtherAfterTheClockThanMaxAhead_When_AQuietZonesWatermarkPassedIt()
    {
        // A quiet engine's watermark follows the receive clock past its event clock (ARV-239), so a sighting behind the watermark can
        // still be after the clock; one more than MaxAhead after it is no sighting the engine applied.
        var engine = new QueueStateEngine(Hall, Settings);
        var t0 = Midnight.AddHours(8);
        engine.Offer(new QueueCrossing("in", CrossingDirection.In, "D/1", t0), t0);
        engine.Offer(new QueueTrackSeen("D/1", t0.AddSeconds(5)), t0.AddSeconds(5));
        engine.Advance(t0.AddMinutes(10));
        var state = RoundTrip(engine.Capture());
        var clock = state.ClockUtc.Should().NotBeNull().And.Subject.Value;
        var beyond = clock.Add(Settings.MaxAhead).AddSeconds(1);
        beyond.Should().BeBefore(state.WatermarkUtc, "the watermark of a quiet zone passed the clock");

        var within = () => QueueStateEngine.Restore(Hall, Settings, RoundTrip(state with { Handovers = [Handover("Z/9", clock.Add(Settings.MaxAhead))] }));
        var refused = () => QueueStateEngine.Restore(Hall, Settings, RoundTrip(state with { Handovers = [Handover("Z/9", beyond)] }));

        within.Should().NotThrow("a sample ahead of the clock may be applied, at most MaxAhead after it");
        refused.Should().Throw<InvalidDataException>("a sighting further after the clock than MaxAhead");
    }

    [Fact]
    public void Restore_Should_ContinueAsTheEngine_When_ItsHandoversAreTheEnginesOwnOrSuperseded()
    {
        var (engine, t0) = TrackedEntrant();
        var state = RoundTrip(engine.Capture());
        state.Handovers.Should().Contain(h => h.Seen == t0.AddSeconds(5), "the first sighting's deadline, superseded, is still in the heap");

        var restored = QueueStateEngine.Restore(Hall, Settings, state);
        var superseded = QueueStateEngine.Restore(Hall, Settings, RoundTrip(state with { Handovers = [Handover("D/1", t0.AddSeconds(5))] }));

        Json(restored.Capture()).Should().Be(Json(engine.Capture()));
        Json(restored.Advance(t0.AddMinutes(5))).Should().Be(Json(engine.Advance(t0.AddMinutes(5))), "a genuine snapshot continues as the engine");
        superseded.Should().NotBeNull("a deadline before the entrant's last sighting is one the engine drops when it comes due");
    }

    [Fact]
    public void Restore_Should_AcceptAHandoverAtTheInstantAFullStepStoppedInside_And_ContinueIdentically()
    {
        // Review (M2): a step that fills MaxStepRecords inside an instant leaves the watermark one tick before it with part of it
        // applied. At 08:00:10 C/x (held, not sighted before) and E/b are sighted; applying the first fragments 1,000 entrants last
        // seen at 08:00:00 (the step's 1,000 records) and sights C/x, and the step stops before E/b: C/x's hand-over and last
        // sighting are after the watermark, at the instant the step stopped in.
        var settings = Settings with { Lateness = TimeSpan.Zero, HandoverWindow = TimeSpan.FromSeconds(1), MaxStepRecords = 1_000 };
        var engine = new QueueStateEngine(Hall, settings);
        var t1 = Midnight.AddHours(8);
        var at = t1.AddSeconds(10);
        engine.Offer(new QueueCrossing("in", CrossingDirection.In, "C/x", t1), t1);
        for (var k = 0; k < 1_000; k++)
        {
            engine.Offer(new QueueCrossing("in", CrossingDirection.In, $"D/{k}", t1), t1);
            engine.Offer(new QueueTrackSeen($"D/{k}", t1), t1);
        }

        for (var step = 0; step < 10 && engine.Advance(t1).More; step++)
        {
        }

        engine.Offer(new QueueTrackSeen("C/x", at), at);
        engine.Offer(new QueueTrackSeen("E/b", at), at);
        engine.Advance(at).More.Should().BeTrue("the step is full inside 08:00:10");
        var state = RoundTrip(engine.Capture());
        state.WatermarkUtc.Should().Be(at.AddTicks(-1));
        state.Handovers.Should().ContainSingle(h => h.Key == "C/x").Which.Seen.Should().Be(at, "C/x was sighted before the step stopped");
        state.Held.Should().ContainSingle(h => h.TrackKey == "C/x").Which.LastSeenUtc.Should().Be(at);
        state.Buffer.Should().ContainSingle().Which.Input.TrackKey.Should().Be("E/b", "the step stopped at E/b");

        var restored = QueueStateEngine.Restore(Hall, settings, state);

        Json(restored.Capture()).Should().Be(Json(engine.Capture()));
        var later = at.AddSeconds(5);
        for (var step = 0; step < 10; step++)
            Json(restored.Advance(later)).Should().Be(Json(engine.Advance(later)), "a genuine snapshot continues as the engine");
        Json(restored.Capture()).Should().Be(Json(engine.Capture()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Engine_Should_ContinueIdentically_When_RestoredAtRandomPointsWithTracksSighted(int seed)
    {
        // Tracks walk in, are sighted out of order and in pauses past the hand-over window (fragmenting them), and leave or are
        // censored; at random points the engine is captured, restored and both continue on the same events.
        var random = new Bogus.Randomizer(seed);
        var straight = new QueueStateEngine(Hall, Settings);
        var restored = new QueueStateEngine(Hall, Settings);
        var t = Midnight.AddHours(9);
        for (var step = 0; step < 400; step++)
        {
            t = t.AddSeconds(random.Number(1, 6));
            var track = "D/" + random.Number(1, 12);
            QueueInput input = random.Number(0, 9) switch
            {
                0 => new QueueCrossing("in", CrossingDirection.In, track, t.AddSeconds(-random.Number(0, 8))),
                1 => new QueueCrossing("out", CrossingDirection.Out, track, t.AddSeconds(-random.Number(0, 8))),
                _ => new QueueTrackSeen(track, t.AddSeconds(-random.Number(0, 8)))
            };
            straight.Offer(input, t, "D");
            restored.Offer(input, t, "D");
            if (random.Number(0, 4) == 0)
                Json(restored.Advance(t)).Should().Be(Json(straight.Advance(t)));
            if (random.Number(0, 30) == 0)
                restored = QueueStateEngine.Restore(Hall, Settings, RoundTrip(restored.Capture()));
        }

        Json(restored.Advance(t.AddMinutes(10))).Should().Be(Json(straight.Advance(t.AddMinutes(10))));
    }

    #endregion

    #region Last sightings

    private const string LastSightingProblem = "The queue engine snapshot has an entrant's last sighting that is not one the engine applied.";

    [Fact]
    public void Restore_Should_RefuseAnEntrantsLastSighting_When_ItIsNotOneTheEngineApplied()
    {
        // Review (L1): the engine sets a last sighting only when it applies a sighting of the entrant's track, so a snapshot cannot
        // move it before the entry, past what was applied, or onto an entrant without a track. Without the deadlines, so the
        // hand-over rules cannot be what refuses them.
        var (engine, t0) = TrackedEntrant();
        var state = RoundTrip(engine.Capture()) with { Handovers = [] };
        var entrant = state.Held.Should().ContainSingle().Subject;
        Func<QueueStateEngine> With(DateTime? seen, string track = "D/1") =>
            () => QueueStateEngine.Restore(Hall, Settings, RoundTrip(state with { Held = [entrant with { LastSeenUtc = seen, TrackKey = track }] }));

        foreach (var (seen, because) in new (DateTime?, string)[] { (t0.AddSeconds(10), "its own"), (t0, "at its entry"), (t0.AddSeconds(30), "at the watermark"), (null, "never sighted") })
            With(seen).Should().NotThrow(because);
        foreach (var (seen, track, because) in new (DateTime?, string, string)[]
                 {
                     (t0.AddHours(-1), "D/1", "before its entry"),
                     (t0.AddTicks(-1), "D/1", "a tick before its entry"),
                     (t0.AddSeconds(30).AddTicks(1), "D/1", "after the watermark, not applied"),
                     (t0.AddSeconds(10), null, "an entrant without a track is never sighted")
                 })
            With(seen, track).Should().Throw<InvalidDataException>(because).WithMessage(LastSightingProblem);
    }

    [Fact]
    public void Restore_Should_RefuseALastSightingFurtherAfterTheClockThanMaxAhead_When_AQuietZonesWatermarkPassedIt()
    {
        // A held entrant never sighted (no deadline), in a quiet zone whose watermark followed the receive clock past the event clock.
        var engine = new QueueStateEngine(Hall, Settings);
        var t0 = Midnight.AddHours(8);
        engine.Offer(new QueueCrossing("in", CrossingDirection.In, "D/2", t0), t0);
        engine.Advance(t0.AddMinutes(10));
        var state = RoundTrip(engine.Capture());
        var clock = state.ClockUtc.Should().NotBeNull().And.Subject.Value;
        var entrant = state.Held.Should().ContainSingle().Subject;
        entrant.LastSeenUtc.Should().BeNull();
        var beyond = clock.Add(Settings.MaxAhead).AddSeconds(1);
        beyond.Should().BeBefore(state.WatermarkUtc, "the watermark of a quiet zone passed the clock");
        Func<QueueStateEngine> With(DateTime seen) => () => QueueStateEngine.Restore(Hall, Settings, RoundTrip(state with { Held = [entrant with { LastSeenUtc = seen }] }));

        With(clock.Add(Settings.MaxAhead)).Should().NotThrow("a sample ahead of the clock may be applied, at most MaxAhead after it");
        With(beyond).Should().Throw<InvalidDataException>("a sighting further after the clock than MaxAhead").WithMessage(LastSightingProblem);
    }

    #endregion

    #region The clock

    [Fact]
    public void Restore_Should_BoundEventsAheadOfTheClockALatenessFurther_When_TheSnapshotHasNoClock()
    {
        // A snapshot without its clock (written before version 9) starts at the latest of its watermark and held events, at most a
        // lateness behind the clock it was written with, so its events ahead are bounded a lateness past what it derives.
        var settings = Settings with { MaxAhead = TimeSpan.FromSeconds(1) };
        var engine = new QueueStateEngine(Hall, settings);
        var now = Midnight.AddHours(10);
        engine.Offer(new QueueCrossing("in", CrossingDirection.In, "A/1", now), now);
        engine.Offer(new QueueCrossing("in", CrossingDirection.In, "A/2", now.AddMilliseconds(500)), now);
        var state = RoundTrip(engine.Capture());
        var ahead = state.Buffer.Should().ContainSingle(b => b.Ahead).Subject;
        var at = (DateTime t) => state with { Buffer = [.. state.Buffer.Select(b => b.Ahead ? b with { Input = b.Input with { TimeUtc = t } } : b)] };

        var clockless = () => QueueStateEngine.Restore(Hall, settings, RoundTrip(at(now.AddSeconds(5)) with { ClockUtc = null }));
        var withClock = () => QueueStateEngine.Restore(Hall, settings, RoundTrip(at(now.AddSeconds(5))));
        var beyond = () => QueueStateEngine.Restore(Hall, settings, RoundTrip(at(now.Add(settings.Lateness).Add(settings.MaxAhead).AddSeconds(1)) with { ClockUtc = null }));

        ahead.Input.TimeUtc.Should().Be(now.AddMilliseconds(500));
        clockless.Should().NotThrow("5 s ahead is within the lateness and MaxAhead past the derived clock");
        withClock.Should().Throw<InvalidDataException>("with its clock the bound is MaxAhead");
        beyond.Should().Throw<InvalidDataException>("past the lateness and MaxAhead an event ahead is refused without a clock too");
    }

    [Fact]
    public void Checks_Should_RefuseAZoneSnapshotWithoutTheEnginesClock_When_ItsVersionIsNineOrLater()
    {
        const string Problem = "A snapshot of version 9 or later carries the queue engine's clock.";
        var zone = new ZoneProcessor("S/Q", Hall, 1);
        zone.Tick(Midnight.AddHours(11));
        var state = zone.Capture();
        state.Engine.ClockUtc.Should().NotBeNull("the engine writes its clock in every snapshot");
        var notAfter = Midnight.AddDays(1);

        SnapshotChecks.Of(state, "S/Q", Hall.QueueZone, 1, notAfter).Should().NotContain(Problem);
        SnapshotChecks.Of(state with { Engine = state.Engine with { ClockUtc = null } }, "S/Q", Hall.QueueZone, 1, notAfter).Should().Contain(Problem);
        SnapshotChecks.Of(state with { Version = ZoneProcessorState.ClockSinceVersion, Engine = state.Engine with { ClockUtc = null } }, "S/Q", Hall.QueueZone, 1, notAfter)
            .Should().Contain(Problem, "version 9 itself carries the clock");
        SnapshotChecks.Of(state with { Version = 8, Engine = state.Engine with { ClockUtc = null } }, "S/Q", Hall.QueueZone, 1, notAfter)
            .Should().NotContain(Problem, "a snapshot before version 9 had no clock");
        ZoneProcessorState.ClockSinceVersion.Should().Be(9);
    }

    [Fact]
    public void Checks_Should_RefuseABufferedEventNotMarkedAhead_When_ItIsAfterTheZonesReference()
    {
        // Review (M1): an event not marked ahead was at or before the reference it arrived with, so a clockless snapshot (one marked
        // as written before version 9) cannot derive its clock, and with it the bound of its events ahead, past the zone's reference.
        const string Implausible = "1 values of the snapshot are not plausible (negative counts, implausible times or empty items).";
        var settings = new ZoneProcessorSettings();
        var zone = new ZoneProcessor("S/Q", Hall, 1, settings);
        var now = Midnight.AddHours(12);
        zone.Tick(now);
        var state = zone.Capture();
        var engine = new QueueStateEngine(Hall, settings.Engine);
        engine.Offer(new QueueCrossing("in", CrossingDirection.In, "A/1", now), now);
        var held = RoundTrip(engine.Capture());
        var buffered = held.Buffer.Should().ContainSingle().Subject;
        buffered.Ahead.Should().BeFalse();
        var notAfter = Midnight.AddDays(2);
        ZoneProcessorState At(DateTime time, bool ahead) =>
            state with { Engine = held with { Buffer = [buffered with { Ahead = ahead, Input = buffered.Input with { TimeUtc = time } }] } };

        SnapshotChecks.Of(At(now, ahead: false), "S/Q", Hall.QueueZone, 1, notAfter).Should().BeEmpty("an event at the reference it arrived with");
        SnapshotChecks.Of(At(now.AddHours(20), ahead: false), "S/Q", Hall.QueueZone, 1, notAfter).Should().Equal([Implausible], "not marked ahead, yet after the reference");
        SnapshotChecks.Of(At(now.AddHours(20), ahead: false) with { Version = 8, Engine = At(now.AddHours(20), false).Engine with { ClockUtc = null } }, "S/Q", Hall.QueueZone, 1, notAfter)
            .Should().Contain(Implausible, "a snapshot marked as written before version 9 is refused alike");
        SnapshotChecks.Of(At(now.AddMinutes(4), ahead: true), "S/Q", Hall.QueueZone, 1, notAfter).Should().BeEmpty("an event marked ahead is bounded by the engine's restore");
        var restore = () => ZoneProcessor.Restore("S/Q", Hall, 1, settings, At(now.AddHours(20), ahead: false), notAfter);
        restore.Should().Throw<InvalidDataException>();
    }

    #endregion

    #region Helpers

    /// <summary>
    /// A track that entered at t0 and was sighted at t0 + 5 s and t0 + 10 s; a sighting of a track nobody holds at t0 + 40 s moves the
    /// clock there and the watermark to t0 + 30 s (a lateness behind), so both sightings are applied and neither deadline (t0 + 35 s,
    /// t0 + 40 s) has come due.
    /// </summary>
    private static (QueueStateEngine Engine, DateTime T0) TrackedEntrant()
    {
        var engine = new QueueStateEngine(Hall, Settings);
        var t0 = Midnight.AddHours(8);
        engine.Offer(new QueueCrossing("in", CrossingDirection.In, "D/1", t0), t0);
        engine.Offer(new QueueTrackSeen("D/1", t0.AddSeconds(5)), t0.AddSeconds(5));
        engine.Offer(new QueueTrackSeen("D/1", t0.AddSeconds(10)), t0.AddSeconds(10));
        engine.Offer(new QueueTrackSeen("X/9", t0.AddSeconds(40)), t0.AddSeconds(40));
        engine.Advance(t0.AddSeconds(40));
        var state = engine.Capture();
        state.WatermarkUtc.Should().Be(t0.AddSeconds(30));
        state.Held.Should().ContainSingle().Which.LastSeenUtc.Should().Be(t0.AddSeconds(10));
        return (engine, t0);
    }

    private static QueueHandoverState Handover(string key, DateTime seen) => new(key, seen, seen.Add(Settings.HandoverWindow));

    private static QueueEngineState RoundTrip(QueueEngineState state) =>
        JsonSerializer.Deserialize<QueueEngineState>(JsonSerializer.Serialize(state, EventCatalog.Json), EventCatalog.Json);

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, EventCatalog.Json);

    #endregion
}
