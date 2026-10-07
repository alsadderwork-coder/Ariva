using System.Text.Json;
using Ariva.Core.Queueing;
using Ariva.Core.Queueing.Replay;
using Ariva.Core.Sensing;
using Ariva.Infra.Sensing;
using Ariva.Simulation.Api.Emulators.Sensors;
using Ariva.Simulation.Api.Scenarios.Engine;
using FluentAssertions;

namespace Ariva.UnitTests.Queueing;

/// <summary>
/// ARV-117: the shadow nowcast without AMAN inputs (F8). It is Nowcast.Compute on the same queue-side input as the
/// published nowcast with the sensor-only desk term: n_open from the desks' zones alone (no AMAN session), no cycle time
/// (no AMAN interval statistics, no AMAN transactions), no reject rate. The F8 cases show the two differ only through
/// those inputs: the shadow does not move when only AMAN's inputs change, and equals the published nowcast when there are
/// none.
/// </summary>
public sealed class ShadowNowcastTests
{
    private static readonly DateTime T = new(2026, 10, 5, 18, 0, 0, DateTimeKind.Utc);

    // A desk minute: open through AMAN (a session) when sensor is 0, through its zones alone when sensor equals the open time.
    private static DeskMinuteSample Minute(string desk, int minute, double serving = 60, double idle = 0, double sensor = 0, int transactions = 0,
        double unknown = 0, bool degraded = false) =>
        new(desk, T.AddMinutes(minute), idle, serving, unknown, transactions, degraded, sensor);

    private static IEnumerable<DeskMinuteSample> Desks(int count, double sensor, int transactions = 0, int minutes = 5) =>
        Enumerable.Range(0, count).SelectMany(d => Enumerable.Range(0, minutes).Select(m => Minute($"DMO/IMM/AR-{d:00}", m, sensor: sensor, transactions: transactions)));

    private static (NowcastResult Published, NowcastResult Shadow) Both(DeskTerm desks, int queue = 29, long? exits = null)
    {
        var (published, shadow) = ShadowNowcasts.Inputs(new NowcastInput { QueueLength = queue, ExitsInWindow = exits, ExitWindowMinutes = 5 }, desks);
        return (Nowcast.Compute(published), Nowcast.Compute(shadow));
    }

    [Fact]
    public void Shadow_Should_FallBackToTheExitTerm_When_SixAmanDesksCycleInOneAndAHalf()
    {
        // F8: Q = 29, n_open = 6, c = 1.5 gives 7.5 minutes; the six desks are open through their AMAN sessions and c is
        // AMAN's lane cycle time, so the shadow sees neither.
        var term = DeskTerms.Compute(Desks(6, sensor: 0), 5, T.AddMinutes(10), laneCycleMinutes: 1.5);

        var (published, shadow) = Both(term);

        published.Minutes.Should().BeApproximately(7.5, 1e-9);
        term.SensorOnly.Should().BeNull("desks open through AMAN alone are not seen by the sensors (Unknown to the shadow)");
        shadow.Minutes.Should().BeNull();
        shadow.NoService.Should().Be(NoServiceReason.NoThroughputData, "without AMAN and without an exit rate nothing measures the throughput");

        // F8: with 18 exits in the last 5 minutes and beta 0.5 the published nowcast is 7.89; the shadow is the exit term
        // alone, 30 / 3.6, flagged.
        var (blend, exitsOnly) = Both(term, exits: 18);
        Math.Round(blend.Minutes!.Value, 2).Should().Be(7.89);
        exitsOnly.Minutes.Should().BeApproximately(30 / 3.6, 1e-9);
        exitsOnly.Source.Should().Be(ThroughputSource.Exits);
        exitsOnly.Degraded.Should().BeTrue();
    }

    [Fact]
    public void Shadow_Should_CountTheSensorDesksButTakeNoAmanCycleTime_When_TheZonesShowSixOpen()
    {
        // The same six desks open through their staff and service zones (sensor-derived), with AMAN's cycle time 1.5.
        var term = DeskTerms.Compute(Desks(6, sensor: 60, transactions: 2), 5, T.AddMinutes(10), laneCycleMinutes: 1.5);

        term.SensorOnly.Should().Be(new DeskTerm(T.AddMinutes(4), 6, null, false, 6), "n_open from the zones, no cycle time without AMAN");
        var (published, shadow) = Both(term, exits: 18);
        Math.Round(published.Minutes!.Value, 2).Should().Be(7.89);
        shadow.Minutes.Should().BeApproximately(30 / 3.6, 1e-9, "the F8 fallback without a cycle time: the exit term alone");
        shadow.Degraded.Should().BeTrue();
    }

    [Fact]
    public void Shadow_Should_GiveNoServiceToo_When_EveryDeskIsClosed()
    {
        // F8: n_open = 0 is no service, whoever says so: every desk known closed.
        var term = DeskTerms.Compute(Enumerable.Range(0, 3).Select(d => Minute($"D{d}", 0, serving: 0)), 5, T.AddMinutes(1));

        var (published, shadow) = Both(term, exits: 18);

        published.NoService.Should().Be(NoServiceReason.NothingOpen);
        shadow.NoService.Should().Be(NoServiceReason.NothingOpen);
        shadow.Minutes.Should().BeNull();
    }

    [Fact]
    public void Shadow_Should_FlagTheTerm_When_SomeOpenDesksAreSeenByAmanOnly()
    {
        List<DeskMinuteSample> minutes = [Minute("A", 0, sensor: 60), Minute("B", 0, sensor: 0), Minute("C", 0, serving: 0), Minute("D", 0, serving: 40, sensor: 20)];

        var term = DeskTerms.SensorOnly(minutes, 5, T.AddMinutes(1));

        term.Should().Be(new DeskTerm(T, 1, null, true, 4), "A is open by its zones; B, and D for most of its open time, by AMAN only");
        DeskTerms.Compute(minutes, 5, T.AddMinutes(1)).OpenServers.Should().Be(3);
        DeskTerms.SensorOnly([Minute("A", 0, sensor: 0, unknown: 0)], 5, T.AddMinutes(1)).Should().BeNull("no desk open by the zones and one the zones cannot see");
        DeskTerms.SensorOnly([Minute("A", 0, sensor: double.NaN)], 5, T.AddMinutes(1)).Should().BeNull("a sensor share that is not a number proves nothing");
        DeskTerms.SensorOnly([Minute("A", 0, sensor: 90)], 5, T.AddMinutes(1)).OpenServers.Should().Be(1, "never more than the open time");
        DeskTerms.SensorOnly([], 5, T).Should().BeNull();
        var bad = () => DeskTerms.SensorOnly([], 61, T);
        bad.Should().Throw<ArgumentOutOfRangeException>();
    }

    #region ARV-117a: the sensor-only desk engine's minutes

    // Six desks with AMAN codes at an all-AMAN site, AMAN live: the published engine has them open through their sessions
    // (no sensor-derived seconds), and the sensor-only engine has its own minute for each from the zones alone.
    private static IEnumerable<DeskMinuteSample> AmanDesksWithZones(Func<int, DeskSensorSample> zones, int count = 6, int minutes = 5) =>
        Enumerable.Range(0, count).SelectMany(d => Enumerable.Range(0, minutes).Select(m =>
            Minute($"AUH/IMM/D{d:00}", m, sensor: 0, transactions: 2) with { Sensor = zones(d) }));

    [Fact]
    public void Shadow_Should_TakeNOpenFromTheZones_When_AmanIsLiveAtAnAllAmanSite()
    {
        // F8: Q = 29, n_open = 6, c = 1.5 (AMAN's lane cycle time): the published nowcast is 7.5 minutes, 7.89 with 18 exits
        // in 5 minutes and beta 0.5. The sensor-only engine sees the six desks open from their staff and service zones, so
        // the shadow's desk term is n_open = 6 from the zones (not "no term" as in ARV-117), with no cycle time: the F8
        // fallback, the exit term alone (30 / 3.6), flagged. The published nowcast is unchanged by the sensor minutes.
        var open = AmanDesksWithZones(_ => new DeskSensorSample(60, 0, false)).ToList();
        var term = DeskTerms.Compute(open, 5, T.AddMinutes(10), laneCycleMinutes: 1.5);

        term.SensorOnly.Should().Be(new DeskTerm(T.AddMinutes(4), 6, null, false, 6), "n_open from the zones while AMAN is live");
        var (published, shadow) = Both(term);
        published.Minutes.Should().BeApproximately(7.5, 1e-9);
        shadow.NoService.Should().Be(NoServiceReason.NoThroughputData, "a desk term without a cycle time and no exit rate measure no throughput");
        var (blend, exitsOnly) = Both(term, exits: 18);
        Math.Round(blend.Minutes!.Value, 2).Should().Be(7.89);
        exitsOnly.Minutes.Should().BeApproximately(30 / 3.6, 1e-9);
        exitsOnly.Degraded.Should().BeTrue();

        // The published term is exactly the one without sensor minutes (ARV-117's input).
        var without = DeskTerms.Compute(open.Select(m => m with { Sensor = null }), 5, T.AddMinutes(10), laneCycleMinutes: 1.5);
        (term with { SensorOnly = null }).Should().Be(without with { SensorOnly = null }, "the sensor-only minutes never reach the published term");
        without.SensorOnly.Should().BeNull("before ARV-117a the shadow saw no desk at this site while AMAN was live");
        Both(term, exits: 18).Published.Should().Be(Both(without, exits: 18).Published);
    }

    [Fact]
    public void Shadow_Should_FollowTheZonesNotAman_When_TheyDisagree()
    {
        // AMAN keeps six desks logged in (published n_open = 6, 7.5 minutes); the zones show only four staffed, then none.
        var four = DeskTerms.Compute(AmanDesksWithZones(d => new DeskSensorSample(d < 4 ? 60 : 0, 0, false)), 5, T.AddMinutes(10), laneCycleMinutes: 1.5);
        four.OpenServers.Should().Be(6);
        four.SensorOnly.Should().Be(new DeskTerm(T.AddMinutes(4), 4, null, false, 6));
        Both(four).Published.Minutes.Should().BeApproximately(7.5, 1e-9);

        // F8 n_open = 0: the zones show every desk closed, so the shadow has no service while AMAN's nowcast is 7.5.
        var none = DeskTerms.Compute(AmanDesksWithZones(_ => new DeskSensorSample(0, 0, false)), 5, T.AddMinutes(10), laneCycleMinutes: 1.5);
        var (published, shadow) = Both(none, exits: 18);
        Math.Round(published.Minutes!.Value, 2).Should().Be(7.89);
        shadow.NoService.Should().Be(NoServiceReason.NothingOpen, "every desk closed by its zones is n_open = 0 (F8: no service)");
    }

    [Fact]
    public void Shadow_Should_FlagOrDropTheTerm_When_TheSensorMinutesAreUnknownOrDegraded()
    {
        // A desk whose zones are silent (Unknown for the minute) or flagged flags the term; with none open from the zones
        // and one Unknown there is no term, never "nothing open". The published minute's own flag no longer matters for a
        // desk with a sensor minute.
        var silent = DeskTerms.Compute(AmanDesksWithZones(d => new DeskSensorSample(d < 3 ? 60 : 0, d == 5 ? 60 : 0, false)), 5, T.AddMinutes(10), 1.5);
        silent.SensorOnly.Should().Be(new DeskTerm(T.AddMinutes(4), 3, null, true, 6));
        var flagged = DeskTerms.Compute(AmanDesksWithZones(d => new DeskSensorSample(60, 0, d == 0)), 5, T.AddMinutes(10), 1.5);
        flagged.SensorOnly.Should().Be(new DeskTerm(T.AddMinutes(4), 6, null, true, 6), "a flagged minute still counts open time, and flags the term");
        DeskTerms.Compute(AmanDesksWithZones(_ => new DeskSensorSample(0, 60, true)), 5, T.AddMinutes(10), 1.5).SensorOnly
            .Should().BeNull("no desk open from the zones and every one Unknown: n_open is not known");
        DeskTerms.Compute(AmanDesksWithZones(_ => new DeskSensorSample(double.NaN, 0, false)), 5, T.AddMinutes(10), 1.5).SensorOnly
            .Should().BeNull("a sensor minute that is not a number proves nothing");
        DeskTerms.SensorOnly([Minute("A", 0, degraded: true) with { Sensor = new DeskSensorSample(60, 0, false) }], 5, T.AddMinutes(1))
            .Should().Be(new DeskTerm(T, 1, null, false, 1), "AMAN's flag on the published minute is AMAN's, not the zones'");

        // A desk without zones (AMAN only) beside desks with sensor minutes keeps ARV-117's rule: open through AMAN only, so
        // Unknown to the shadow, which flags the term.
        List<DeskMinuteSample> mixed = [.. AmanDesksWithZones(_ => new DeskSensorSample(60, 0, false), count: 2, minutes: 1), Minute("AUH/IMM/X", 0, sensor: 0)];
        DeskTerms.SensorOnly(mixed, 5, T.AddMinutes(1)).Should().Be(new DeskTerm(T, 2, null, true, 3));
    }

    [Fact]
    public void Shadow_Should_NotMove_When_AmanChangesAndTheSensorMinutesStay()
    {
        // 400 random desk sets with sensor-only minutes: AMAN's inputs (its lane cycle time, transactions, open and Unknown
        // seconds, the published flag and sensor-derived seconds) change at random; the shadow term never moves.
        var random = new Seeded(1171);
        for (var k = 0; k < 400; k++)
        {
            var desks = random.Next(1, 8);
            var sensors = Enumerable.Range(0, desks).Select(_ => new DeskSensorSample(random.Next(0, 61), random.Next(3) == 0 ? random.Next(0, 61) : 0, random.Next(5) == 0))
                .ToList();
            IEnumerable<DeskMinuteSample> Aman() => Enumerable.Range(0, desks).Select(d =>
            {
                var serving = random.Next(0, 61);
                return new DeskMinuteSample($"D{d}", T, random.Next(0, 61 - serving), serving, random.Next(0, 30), random.Next(0, 9), random.Next(2) == 0,
                    random.Next(0, 61), sensors[d]);
            }).ToList();

            var first = DeskTerms.SensorOnly(Aman(), 5, T.AddMinutes(1));
            for (var again = 0; again < 3; again++)
                DeskTerms.SensorOnly(Aman(), 5, T.AddMinutes(1)).Should().Be(first, "set {0}: only AMAN's inputs changed", k);
        }
    }

    #endregion

    [Fact]
    public void Inputs_Should_DifferOnlyInTheDeskTerm_When_BuiltFromOneQueueInput()
    {
        var random = new Seeded(9303);
        var allowed = new[] { nameof(NowcastInput.OpenServers), nameof(NowcastInput.CycleMinutes), nameof(NowcastInput.Degraded) };
        for (var k = 0; k < 500; k++)
        {
            var queue = new NowcastInput
            {
                QueueLength = random.Next(-1, 200),
                ExitsInWindow = random.Next(3) == 0 ? null : random.Next(0, 100),
                ExitWindowMinutes = random.Next(1, 10),
                MergeShare = random.NextDouble(),
                RejectRate = random.NextDouble() / 5,
                Degraded = random.Next(2) == 0
            };
            var term = new DeskTerm(T, random.Next(0, 12), random.Next(2) == 0 ? null : 0.2 + random.NextDouble() * 3, random.Next(2) == 0, 12)
            {
                SensorOnly = random.Next(2) == 0 ? null : new DeskTerm(T, random.Next(0, 12), null, random.Next(2) == 0, 12)
            };

            var (published, shadow) = ShadowNowcasts.Inputs(queue, term);

            foreach (var property in typeof(NowcastInput).GetProperties().Where(p => !allowed.Contains(p.Name)))
                property.GetValue(shadow).Should().Be(property.GetValue(published), "{0} is a queue-side input, the same for both", property.Name);
            published.Should().Be(ShadowNowcasts.WithDesks(queue, term), "the published nowcast takes the desk term as before");
            shadow.CycleMinutes.Should().BeNull("the shadow takes no cycle time");
        }
    }

    [Fact]
    public void Shadow_Should_NotMove_When_OnlyAmanInputsChange()
    {
        // Random desks and minutes; AMAN's inputs (its lane cycle time, its transactions, the desks open through a session
        // only) are changed while the sensor-derived time stays: the shadow never moves. With no AMAN input at all the
        // shadow is the published nowcast.
        var random = new Seeded(117);
        for (var k = 0; k < 400; k++)
        {
            var desks = random.Next(1, 8);
            var minutes = new List<DeskMinuteSample>();
            for (var d = 0; d < desks; d++)
            {
                for (var m = 0; m < 5; m++)
                {
                    var open = random.Next(5) == 0 ? 0 : random.Next(0, 61);
                    var unknown = random.Next(10) == 0 ? 60 - open : 0;
                    var sensor = random.Next(3) == 0 ? 0 : open;
                    minutes.Add(Minute($"D{d}", m, serving: open, sensor: sensor, unknown: unknown, degraded: random.Next(20) == 0));
                }
            }

            var exits = random.Next(4) == 0 ? (long?)null : random.Next(0, 60);
            var queue = random.Next(0, 150);
            var plain = DeskTerms.Compute(minutes, 5, T.AddMinutes(10));
            var withAman = DeskTerms.Compute(minutes.Select(m => m with { Transactions = random.Next(0, 4) }), 5, T.AddMinutes(10), 0.5 + random.NextDouble() * 2);
            var (_, shadowPlain) = Both(plain, queue, exits);
            var (_, shadowWithAman) = Both(withAman, queue, exits);

            shadowWithAman.Should().Be(shadowPlain, "AMAN's cycle time and transactions are not shadow inputs (case {0})", k);
            if (withAman is not null)
                Both(withAman with { CycleMinutes = 42, OpenServers = 99 }, queue, exits).Shadow.Should().Be(shadowWithAman, "the published term's n_open and c are not shadow inputs");

            // AMAN's sessions: a desk open through AMAN only, closed instead, changes the shadow at most by its flag (a desk
            // the sensors cannot see is Unknown to it), never by a number taken from AMAN.
            var sessionsClosed = minutes.Select(m => m.SensorOpenSeconds >= 30 || m.OpenSeconds < 30 ? m : m with { ServingSeconds = 0, IdleSeconds = 0 }).ToList();
            var (_, shadowNoSessions) = Both(DeskTerms.Compute(sessionsClosed, 5, T.AddMinutes(10)), queue, exits);
            if (withAman?.SensorOnly is { OpenServers: > 0, Degraded: false })
                shadowNoSessions.Should().Be(shadowWithAman, "case {0}", k);

            // No AMAN input at all (every open second from the zones, no transactions, no lane cycle): the same nowcast.
            var (publishedNoAman, shadowNoAman) = Both(DeskTerms.Compute(minutes.Select(m => m with { SensorDerivedSeconds = m.OpenSeconds }), 5, T.AddMinutes(10)), queue, exits);
            shadowNoAman.Should().Be(publishedNoAman, "without AMAN inputs the two nowcasts are the same (case {0})", k);
        }
    }

    [Fact]
    public void Compute_Should_GiveExactlyANumberOrAReason_When_InputsAreAnything()
    {
        // ARV-117 second review: script 0041's ck_queue_minute_shadow_flag holds a shadow's flag exactly when it has a
        // number or a reason, so a shadow with neither (and a flag) would be refused and stall the checkpoint. Every
        // Nowcast.Compute path returns exactly one of the two, whatever the inputs (negative, NaN, infinite, missing).
        double[] doubles = [double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1, 0, 1e-9, 0.04, 0.5, 1, 2.5, 60, 61, 1e9];
        var random = new Seeded(1171);
        for (var k = 0; k < 5000; k++)
        {
            double? Maybe() => random.Next(4) == 0 ? null : doubles[random.Next(doubles.Length)];
            var input = new NowcastInput
            {
                QueueLength = random.Next(5) == 0 ? null : random.Next(-2, 400),
                ExitsInWindow = random.Next(5) == 0 ? null : random.Next(-2, 200),
                ExitWindowMinutes = doubles[random.Next(doubles.Length)],
                OpenServers = random.Next(4) == 0 ? null : random.Next(-1, 20),
                CycleMinutes = Maybe(),
                MergeShare = doubles[random.Next(doubles.Length)],
                RejectRate = doubles[random.Next(doubles.Length)],
                Degraded = random.Next(2) == 0
            };
            var result = Nowcast.Compute(input);

            (result.Minutes is null).Should().Be(result.NoService is not null, "a nowcast is a number or a reason, never both or neither (case {0})", k);
            var shadow = ShadowNowcast.From(result);
            (shadow.Minutes is null).Should().Be(shadow.NoService is not null, "the shadow keeps the result's number or reason (case {0})", k);
        }
    }

    private static readonly DateTime Midnight = new(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);
    private static readonly QueueZoneGeometry Geometry = new("A-VIS", new HashSet<string> { "A-VIS entry" }, new HashSet<string> { "A-VIS exit" },
        new HashSet<string>(), new HashSet<string>());

    private static DateTime WallOf(double minute) => Midnight.AddMinutes(minute);

    // The reference evening's S-15 traffic (as in DeskTermTests), each minute offered with the given desk term.
    private static List<QueueLiveMinute> Evening(Func<int, DeskTerm> desks)
    {
        var day = ScenarioDay.Run(ScenarioConfig.Reference() with { Lite = true });
        var sensor = SensorTraffic.Sensor("S-15");
        var zone = new ZoneProcessor("DMO/A-VIS", Geometry, 3);
        var live = new List<QueueLiveMinute>();
        for (var minute = 1020; minute < 1100; minute++)
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

            zone.UseDesks(desks(minute));
            if (mapped.Crossings.Count > 0)
                zone.Offer(Stamp(new VendorLineCrossingBatch { Crossings = [.. mapped.Crossings.Select(c => new Sensed<LineCrossing>(c, c.TimeUtc, SensedFlags.None))] }), DateTime.MaxValue);
            if (mapped.Occupancy.Count > 0)
                zone.Offer(Stamp(new ZoneOccupancyBatch { Occupancy = [.. mapped.Occupancy.Select(o => new Sensed<ZoneOccupancy>(o, o.TimeUtc, SensedFlags.None))] }), DateTime.MaxValue);
            live.AddRange(zone.Drain().Live);
        }

        return live;
    }

    [Fact]
    public void Zone_Should_WriteTheShadowBesideThePublishedNowcast_When_ADeskTermHasASensorPart()
    {
        var aman = Evening(minute => new DeskTerm(WallOf(minute - 1), 6, 1.5, false, 6)).ToDictionary(l => l.MinuteUtc);
        var sensors = Evening(minute => new DeskTerm(WallOf(minute - 1), 6, 1.5, false, 6) { SensorOnly = new DeskTerm(WallOf(minute - 1), 6, null, false, 6) })
            .ToDictionary(l => l.MinuteUtc);
        var closed = Evening(minute => new DeskTerm(WallOf(minute - 1), 6, 1.5, false, 6) { SensorOnly = new DeskTerm(WallOf(minute - 1), 0, null, false, 6) })
            .ToDictionary(l => l.MinuteUtc);
        var none = Evening(_ => null);
        var minute = WallOf(1090);

        aman[minute].NowcastDegraded.Should().BeFalse();
        aman[minute].Shadow.Should().NotBeNull("every live minute carries its shadow");
        aman[minute].Shadow.Minutes.Should().NotBe(aman[minute].NowcastMinutes, "AMAN's desks and cycle time are left out of the shadow");
        aman[minute].Shadow.Degraded.Should().BeTrue("the exit term alone is flagged");
        sensors[minute].Should().BeEquivalentTo(aman[minute], o => o.Excluding(l => l.Shadow), "the shadow never changes the published row");
        sensors[minute].Shadow.Should().Be(aman[minute].Shadow, "n_open without a cycle time leaves the exit term (F8 fallback)");
        closed[minute].Shadow.Should().Be(new ShadowNowcast(null, NoServiceReason.NothingOpen, false));
        closed[minute].NowcastMinutes.Should().NotBeNull("the published nowcast follows AMAN's six open desks");
        none.Should().NotBeEmpty().And.OnlyContain(l => l.Shadow == new ShadowNowcast(l.NowcastMinutes, l.NoService, l.NowcastDegraded),
            "without a desk term (a replay, or no desks) the shadow is the published nowcast");
    }

    [Fact]
    public void Json_Should_LeaveTheShadowOut_When_ALiveMinuteIsSerialised()
    {
        var live = new QueueLiveMinute("DMO/A-VIS", T, 61, true, false, 15.4, 4.0, null, false) { Shadow = new ShadowNowcast(987.654, null, true) };

        var ledger = JsonSerializer.Serialize(live, ReplayLedger.Json);
        var snapshot = JsonSerializer.Serialize(Ariva.Infra.Live.LiveZoneSnapshot.From(live, T), Ariva.Infra.Messaging.EventCatalog.Json);
        var plain = JsonSerializer.Serialize(live);

        foreach (var json in new[] { ledger, snapshot, plain })
        {
            json.Should().NotContainEquivalentOf("shadow");
            json.Should().NotContain("987.654");
            json.Should().Contain("15.4");
        }
    }

    // A seeded sequence through Bogus (not System.Random, CA5394): Next(min, max) excludes max, as Random does.
    private sealed class Seeded(int seed)
    {
        private readonly Bogus.Randomizer _random = new(seed);

        public int Next(int min, int max) => _random.Number(min, max - 1);

        public int Next(int max) => _random.Number(0, max - 1);

        public double NextDouble() => _random.Double();
    }
}
