using System.Text.Json;
using Ariva.Core.Desks;
using Ariva.Infra.Messaging;
using FluentAssertions;

namespace Ariva.UnitTests.Desks;

/// <summary>
/// ARV-117a: the sensor-only desk engine of a site, run beside the published one in the desk feed. It holds the desks
/// with staff or service zones with those zones as their only sources, applies zone readings only (CWE-501: no AMAN
/// session, statistic, transaction or heartbeat changes its output), is bounded (CWE-120: desks per site, readings
/// buffered per desk and per engine) and refuses an invalid, oversized or AMAN-tainted snapshot.
/// </summary>
public sealed class SensorOnlyDeskEngineTests
{
    private static readonly DateTime T = new(2026, 10, 7, 18, 0, 0, DateTimeKind.Utc);
    private static readonly DeskStateSettings Settings = new Ariva.Infra.Border.DeskFeedSettings().SensorEngine;

    private static DeskProfile Aman(string code, bool staff = true, bool service = true) => new(code, "VIS", true, true, staff, service);

    private static DeskProfile Zoned(string code) => new(code, "VIS", false, false, true, true);

    #region Profiles and bounds

    [Fact]
    public void Profiles_Should_KeepTheDesksWithZonesWithTheirZonesAlone_When_TheSiteHasAmanAndSensorDesks()
    {
        var (desks, leftOut) = SensorOnlyDeskEngine.Profiles([Aman("AUH/IMM/B2"), Aman("AUH/IMM/A1", service: false), Aman("AUH/IMM/N1", staff: false, service: false),
            Zoned("AUH/IMM/S1"), Zoned("AUH/IMM/S1"), Zoned(new string('k', 65)), null]);

        leftOut.Should().Be(0);
        desks.Should().Equal(
            new DeskProfile("AUH/IMM/A1", "VIS", false, false, true, false),
            new DeskProfile("AUH/IMM/B2", "VIS", false, false, true, true),
            new DeskProfile("AUH/IMM/S1", "VIS", false, false, true, true));
    }

    [Fact]
    public void Start_Should_LeaveOutAndCountTheDesksBeyondTheCap_When_ASiteHasMoreThanItHolds()
    {
        var many = Enumerable.Range(0, SensorOnlyDeskEngine.MaxDesks + 7).Select(i => Zoned($"BIG/C/{i:00000}")).ToList();

        var engine = SensorOnlyDeskEngine.Start(many, T, Settings);

        engine.Desks.Should().Be(SensorOnlyDeskEngine.MaxDesks);
        engine.LeftOut.Should().Be(7);
        engine.Status("BIG/C/00000").Should().NotBeNull();
        engine.Status($"BIG/C/{SensorOnlyDeskEngine.MaxDesks + 6:00000}").Should().BeNull("the last desks in key order are left out");
        SensorOnlyDeskEngine.Bounded(new DeskStateSettings { MaxDesks = 50_000, MaxBufferedSignalsPerDesk = 10 }).MaxDesks.Should().Be(SensorOnlyDeskEngine.MaxDesks);
        Settings.MaxBufferedSignals.Should().Be(100_000);
        Settings.MaxBufferedSignalsPerDesk.Should().Be(1_000);
        var bad = () => SensorOnlyDeskEngine.Profiles([], 0);
        bad.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Offer_Should_RefuseAndCountReadingsBeyondItsBounds_When_OneDeskIsFlooded()
    {
        // 50,000 readings on one desk: it holds at most its share (1,000), the rest are counted as refused; the other desk
        // is still served.
        var engine = SensorOnlyDeskEngine.Start([Zoned("X/C/A"), Zoned("X/C/B")], T, Settings);
        for (var i = 0; i < 50_000; i++)
            engine.Offer(new DeskZoneReading("X/C/A", T.AddMilliseconds(i), DeskSource.StaffZone, i % 2), T.AddMinutes(1));
        engine.Offer(new DeskZoneReading("X/C/B", T, DeskSource.StaffZone, 1), T.AddMinutes(1));
        engine.Offer(new DeskZoneReading("X/C/B", T, DeskSource.ServiceZone, 1), T.AddMinutes(1));

        engine.Counters.BufferFull.Should().Be(50_000 - Settings.MaxBufferedSignalsPerDesk);
        var minutes = Advance(engine, T.AddMinutes(1).Add(Settings.Lateness));
        minutes.Single(m => m.DeskCode == "X/C/B").Serving.Should().Be(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Advance_Should_CloseNoMoreMinutesThanTheBudget_When_ItCatchesUpAfterAPause()
    {
        // Six desks two hours behind (720 minutes): a budget of 100 records a step gives at most 100 minutes a step, and the
        // steps together are exactly the minutes of one unbudgeted catch-up, in the same order (CWE-120, the desk feed's cap).
        var desks = Enumerable.Range(0, 6).Select(i => Zoned($"AUH/IMM/D{i}")).ToArray();
        var budgeted = SensorOnlyDeskEngine.Start(desks, T, Settings);
        var straight = SensorOnlyDeskEngine.Start(desks, T, Settings);
        foreach (var engine in new[] { budgeted, straight })
        {
            for (var i = 0; i < 6; i++)
                engine.Offer(new DeskZoneReading($"AUH/IMM/D{i}", T.AddSeconds(10 + i), DeskSource.StaffZone, 1), T.AddMinutes(1));
        }

        var to = T.AddHours(2).Add(Settings.Lateness);
        var steps = new List<DeskStep>();
        for (var i = 0; i < 100; i++)
        {
            var step = budgeted.Advance(to, 100);
            steps.Add(step);
            if (!step.More)
                break;
        }

        steps.Should().AllSatisfy(s => s.Minutes.Count.Should().BeLessThanOrEqualTo(100));
        steps.Take(steps.Count - 1).Should().AllSatisfy(s => s.More.Should().BeTrue());
        steps[^1].More.Should().BeFalse();
        var expected = Advance(straight, to);
        expected.Should().HaveCount(720);
        steps.SelectMany(s => s.Minutes).Should().Equal(expected);
        var zero = () => budgeted.Advance(to, 0);
        zero.Should().Throw<ArgumentOutOfRangeException>();
    }

    #endregion

    #region Zone readings only (CWE-501)

    private static List<DeskMinute> Advance(SensorOnlyDeskEngine engine, DateTime to)
    {
        var minutes = new List<DeskMinute>();
        for (var i = 0; i < 100; i++)
        {
            var step = engine.Advance(to);
            minutes.AddRange(step.Minutes);
            if (!step.More)
                break;
        }

        return minutes;
    }

    // An hour of random staff and service zone readings for three desks, in time order.
    private static List<DeskZoneReading> Readings(Seeded random)
    {
        var readings = new List<DeskZoneReading>();
        for (var s = 0; s < 3600; s += random.Next(5, 40))
        {
            var desk = $"AUH/IMM/D{random.Next(3)}";
            readings.Add(new DeskZoneReading(desk, T.AddSeconds(s), random.Next(2) == 0 ? DeskSource.StaffZone : DeskSource.ServiceZone, random.Next(3),
                random.Next(20) == 0));
        }

        return readings;
    }

    // Every kind of AMAN input the published engine takes: sessions, statistics, transactions and heartbeats.
    private static DeskSignal AmanSignal(Seeded random, string desk, DateTime at) => random.Next(6) switch
    {
        0 => new DeskSessionChangedSignal(desk, at, (DeskSessionSignal)random.Next(3)),
        1 => new DeskTransactionsCompleted(desk, at, random.Next(0, 20)),
        2 => new DeskTransactionStarted(desk, at),
        3 => new DeskTransactionEnded(desk, at),
        4 => new DeskHeartbeat(desk, at, DeskSource.Session),
        _ => new DeskHeartbeat(desk, at, DeskSource.Transactions)
    };

    [Fact]
    public void Offer_Should_GiveTheSameMinutes_When_AmanSessionsStatisticsAndTransactionsAreOfferedToo()
    {
        // 50 random hours: the same zone readings with and without AMAN inputs interleaved give exactly the same minutes,
        // and every AMAN input is refused and counted.
        for (var seed = 0; seed < 50; seed++)
        {
            var random = new Seeded(9303 + seed);
            var desks = new[] { Aman("AUH/IMM/D0"), Aman("AUH/IMM/D1"), Aman("AUH/IMM/D2", service: false) };
            var readings = Readings(random);
            var plain = SensorOnlyDeskEngine.Start(desks, T, Settings);
            var withAman = SensorOnlyDeskEngine.Start(desks, T, Settings);
            List<DeskMinute> plainMinutes = [], amanMinutes = [];
            var offered = 0;
            for (var minute = 1; minute <= 62; minute++)
            {
                var now = T.AddMinutes(minute);
                foreach (var r in readings.Where(r => r.TimeUtc >= now.AddMinutes(-1) && r.TimeUtc < now))
                {
                    plain.Offer(r, now);
                    withAman.Offer(AmanSignal(random, r.DeskCode, r.TimeUtc), now);
                    withAman.Offer(r, now);
                    withAman.Offer(AmanSignal(random, r.DeskCode, r.TimeUtc), now);
                    offered += 2;
                }

                plainMinutes.AddRange(Advance(plain, now));
                amanMinutes.AddRange(Advance(withAman, now));
            }

            amanMinutes.Should().Equal(plainMinutes, "seed {0}: no AMAN input changes the sensor-only engine's output", seed);
            plainMinutes.Should().NotBeEmpty().And.OnlyContain(m => m.Transactions == 0 && m.PresentNotProcessing == TimeSpan.Zero);
            withAman.Refused.Should().Be(offered);
            plain.Refused.Should().Be(0);
            withAman.Capture().Should().BeEquivalentTo(plain.Capture(), o => o.Excluding(s => s.Counters).Excluding(s => s.Sequence));
        }
    }

    [Fact]
    public void Engine_Should_ServeFromTheZones_When_AmanSaysTheDeskIsLoggedOut()
    {
        // The published engine closes an AMAN desk that AMAN reports logged out (F10 row 2) and records the staff as present;
        // the sensor-only engine, which never hears AMAN, has it Serving from its staff and service zones (row 6).
        var desk = Aman("AUH/IMM/A1");
        var published = new DeskStateEngine([desk], T, Settings);
        var sensor = SensorOnlyDeskEngine.Start([desk], T, Settings);
        for (var m = 0; m < 5; m++)
        {
            var at = T.AddMinutes(m);
            var now = at.AddMinutes(1);
            DeskSignal[] zones = [new DeskZoneReading(desk.DeskCode, at, DeskSource.StaffZone, 1), new DeskZoneReading(desk.DeskCode, at, DeskSource.ServiceZone, 1)];
            published.Offer(new DeskSessionChangedSignal(desk.DeskCode, at, DeskSessionSignal.Closed), now);
            published.Offer(new DeskHeartbeat(desk.DeskCode, at, DeskSource.Transactions), now);
            foreach (var z in zones)
            {
                published.Offer(z, now);
                sensor.Offer(z, now);
            }

            published.Advance(now);
            sensor.Advance(now);
        }

        var last = T.AddMinutes(5).Add(Settings.Lateness);
        published.Advance(last);
        sensor.Advance(last);
        published.Status(desk.DeskCode).Should().Be(new DeskEvaluation(DeskStatus.Closed, false, true, false));
        sensor.Status(desk.DeskCode).Should().Be(new DeskEvaluation(DeskStatus.Serving, true, false, false));
    }

    #endregion

    #region Snapshots

    [Fact]
    public void Restore_Should_ContinueExactly_When_TheSnapshotRoundTripsThroughJson()
    {
        var random = new Seeded(117);
        var desks = new[] { Zoned("AUH/IMM/D0"), Zoned("AUH/IMM/D1"), Zoned("AUH/IMM/D2") };
        var readings = Readings(random);
        var straight = SensorOnlyDeskEngine.Start(desks, T, Settings);
        var resumed = SensorOnlyDeskEngine.Start(desks, T, Settings);
        List<DeskMinute> a = [], b = [];
        for (var minute = 1; minute <= 62; minute++)
        {
            var now = T.AddMinutes(minute);
            foreach (var r in readings.Where(r => r.TimeUtc >= now.AddMinutes(-1) && r.TimeUtc < now))
            {
                straight.Offer(r, now);
                resumed.Offer(r, now);
            }

            a.AddRange(Advance(straight, now));
            b.AddRange(Advance(resumed, now));
            if (minute % 7 == 0)
            {
                var json = JsonSerializer.Serialize(resumed.Capture(), EventCatalog.Json);
                resumed = SensorOnlyDeskEngine.Restore(desks, Settings, JsonSerializer.Deserialize<DeskEngineState>(json, EventCatalog.Json));
            }
        }

        b.Should().Equal(a);
    }

    public static TheoryData<string> Hostile => new()
    {
        "session memory", "transaction memory", "transaction count", "present ticks", "a session signal buffered", "a transaction heartbeat buffered",
        "too many desks", "too many buffered readings", "a count above the bound", "a bad version",
        // Without a login or transaction source every open state is sensor-derived (F10 rows 6 and 7).
        "an open state not sensor-derived", "an idle state not sensor-derived", "open time not sensor-derived"
    };

    [Theory]
    [MemberData(nameof(Hostile))]
    public void Restore_Should_RefuseTheSnapshot_When_ItIsInvalidOversizedOrHoldsAman(string forgery)
    {
        var desks = new[] { Zoned("AUH/IMM/D0") };
        var engine = SensorOnlyDeskEngine.Start(desks, T, Settings);
        engine.Offer(new DeskZoneReading("AUH/IMM/D0", T.AddSeconds(5), DeskSource.StaffZone, 1), T);
        engine.Advance(T.AddMinutes(1));
        engine.Offer(new DeskZoneReading("AUH/IMM/D0", T.AddMinutes(2), DeskSource.StaffZone, 1), T.AddMinutes(2));
        var state = engine.Capture();
        var desk = state.Desks[0];
        var pending = desk.Pending[0];
        var forged = forgery switch
        {
            "session memory" => state with { Desks = [desk with { Memory = desk.Memory with { Session = DeskSessionSignal.Opened, SessionHeard = T } }] },
            "transaction memory" => state with { Desks = [desk with { Memory = desk.Memory with { LastTransaction = T } }] },
            "transaction count" => state with { Desks = [desk with { Transactions = 3 }] },
            "present ticks" => state with { Desks = [desk with { PresentTicks = 10 }] },
            "a session signal buffered" => state with { Desks = [desk with { Pending = [pending with { Signal = new DeskSignalState("session", "AUH/IMM/D0", T.AddMinutes(2), State: DeskSessionSignal.Opened) }] }] },
            "a transaction heartbeat buffered" => state with { Desks = [desk with { Pending = [pending with { Signal = new DeskSignalState("heartbeat", "AUH/IMM/D0", T.AddMinutes(2), DeskSource.Transactions) }] }] },
            "too many desks" => state with { Desks = [.. Enumerable.Repeat(desk, SensorOnlyDeskEngine.MaxDesks + 1)] },
            "too many buffered readings" => state with { Desks = [desk with { Pending = [.. Enumerable.Repeat(pending, Settings.MaxBufferedSignalsPerDesk + 1)] }] },
            "a count above the bound" => state with { Desks = [desk with { Pending = [pending with { Signal = pending.Signal with { Count = 51 } }] }] },
            "an open state not sensor-derived" => state with { Desks = [desk with { Current = new DeskEvaluation(DeskStatus.Serving, false, false, false) }] },
            "an idle state not sensor-derived" => state with { Desks = [desk with { Current = new DeskEvaluation(DeskStatus.Idle, false, false, false) }] },
            "open time not sensor-derived" => state with { Desks = [desk with { Ticks = [0, 20 * TimeSpan.TicksPerSecond, 0, 0, 0], SensorTicks = 5 * TimeSpan.TicksPerSecond }] },
            _ => state with { Version = 99 }
        };

        var restore = () => SensorOnlyDeskEngine.Restore(desks, Settings, forged);

        restore.Should().Throw<InvalidDataException>();
        SensorOnlyDeskEngine.Restore(desks, Settings, state).Capture().Should().BeEquivalentTo(state, "the untouched snapshot restores");
    }

    #endregion

    // A seeded sequence through Bogus (not System.Random, CA5394): Next(min, max) excludes max, as Random does.
    private sealed class Seeded(int seed)
    {
        private readonly Bogus.Randomizer _random = new(seed);

        public int Next(int min, int max) => _random.Number(min, max - 1);

        public int Next(int max) => _random.Number(0, max - 1);
    }
}
