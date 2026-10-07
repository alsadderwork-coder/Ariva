using Ariva.Core.Desks;
using Ariva.Core.Queueing;
using Ariva.Core.Queueing.Replay;
using Ariva.Simulation.Api.Scenarios.Engine;
using Ariva.UnitTests.Replay;
using FluentAssertions;

namespace Ariva.UnitTests.Desks;

/// <summary>
/// ARV-116: the reference evening (seed 9303) with the Visitors desks' staff and service zones reported by S-18 to S-20
/// of the sensor emulator, through Ingest, the A-VIS zone processor and a desk engine that has the zones alone (no AMAN):
/// the engine's open desks follow the scenario's, and the queue's own outputs do not change.
/// </summary>
public sealed class DeskZoneScenarioTests(ITestOutputHelper output)
{
    /// <summary>The Proposed agreement bound (docs/product/decisions.md, pending the owner): within one desk for at least 95 percent of minutes.</summary>
    private const double RequiredShare = 0.95;

    private static readonly Lazy<ScenarioDay> Day = new(() => ScenarioDay.Run(ScenarioConfig.Reference() with { Lite = true }));

    private static readonly Lazy<(ReplayHashes Hashes, Dictionary<string, List<ZoneOutputs>> Outputs)> Desks = new(() => ReferenceReplay.RunDesks());

    private static IReadOnlyList<DeskZoneSample> Readings => Desks.Value.Outputs["DMO/A-VIS"][0].DeskReadings;

    /// <summary>The desk feed's engine settings (lateness 90 seconds, sensor T1 60 seconds, the rest the reference values).</summary>
    private static readonly DeskStateSettings FeedSettings = new Ariva.Infra.Border.DeskFeedSettings().Engine;

    /// <summary>
    /// Feeds the readings to a desk engine of sensor-only Visitors desks as the desk feed would (each minute's readings
    /// arrive with the minute's push, a minute after their time) and returns the closed minutes.
    /// </summary>
    internal static List<DeskMinute> SensorOnlyMinutes(IReadOnlyList<DeskZoneSample> readings, DeskStateSettings settings = null)
    {
        var profiles = ReferenceReplay.VisitorDesks.Select(d =>
            new DeskProfile(ReferenceReplay.DeskKeyOf(d), "VIS", HasTransactions: false, HasSession: false, HasStaffZone: true, HasServiceZone: true));
        var engine = new DeskStateEngine(profiles, ReferenceReplay.From, settings ?? FeedSettings);
        var minutes = new List<DeskMinute>();
        var byArrival = readings.Select(r => (Arrival: r.TimeUtc.AddMinutes(1), Reading: r)).OrderBy(r => r.Arrival).ToList();
        var next = 0;
        for (var now = ReferenceReplay.From; now <= ReferenceReplay.To.AddMinutes(5); now = now.AddSeconds(15))
        {
            while (next < byArrival.Count && byArrival[next].Arrival <= now)
                engine.Offer(byArrival[next++].Reading.ToSignal(), now);
            minutes.AddRange(engine.Advance(now).Minutes);
        }

        engine.Counters.Invalid.Should().Be(0);
        engine.Counters.UnknownDesk.Should().Be(0);
        engine.Counters.BufferFull.Should().Be(0);
        return minutes;
    }

    [Fact]
    public void Engine_Should_MatchTheScenariosOpenDesksWithinOneDesk_When_FedTheStaffAndServiceZonesAlone()
    {
        var day = Day.Value;
        var q = ScenarioModel.Q("A-VIS");
        var minutes = SensorOnlyMinutes(Readings);

        // n_open as the nowcast's desk term counts it (F8, ARV-064): a desk Idle or Serving for at least half the minute.
        var open = minutes.GroupBy(m => m.MinuteUtc).ToDictionary(g => g.Key, g => g.Count(m => m.Open >= TimeSpan.FromSeconds(30)));
        int compared = 0, within = 0, exact = 0, worst = 0;
        // Every minute of the evening from 17:01: the engine starts at 17:00 with every source unheard (Unknown), and the
        // first readings (stamped 17:00) arrive with the 17:00 push at 17:01.
        for (var minute = 1021; minute < 1230; minute++)
        {
            var at = ReferenceReplay.WallOf(minute);
            var scenario = day.ServerStates(q, minute).Count(s => s.State is "serving" or "idle");
            open.Should().ContainKey(at, "every desk closes minute {0}", ScenarioMath.Clock(minute));
            var difference = Math.Abs(open[at] - scenario);
            compared++;
            within += difference <= 1 ? 1 : 0;
            exact += difference == 0 ? 1 : 0;
            worst = Math.Max(worst, difference);
        }

        output.WriteLine($"Minutes compared {compared}; within one desk {within} ({(double)within / compared:P1}); exact {exact} ({(double)exact / compared:P1}); largest difference {worst}");
        ((double)within / compared).Should().BeGreaterThanOrEqualTo(RequiredShare);
    }

    [Fact]
    public void Engine_Should_TurnTheDesksOfASilentSensorUnknownAndDegraded_When_ItsReadingsStop()
    {
        // The reference evening marks no Visitors desk unknown, so the test silences S-19 (desks AR-13 to AR-17) from 18:30
        // to 18:45 (no scripted event changes): its desks' zones go stale after T_stale (2 minutes) and their minutes are
        // Unknown and Degraded; the other desks' minutes are exactly as before.
        var silenced = Ariva.Simulation.Api.Emulators.Sensors.SensorTraffic.DesksOf(Ariva.Simulation.Api.Emulators.Sensors.SensorTraffic.Sensor("S-19"))
            .Select(ReferenceReplay.DeskKeyOf).ToHashSet(StringComparer.Ordinal);
        var from = ReferenceReplay.WallOf(1110);
        var to = ReferenceReplay.WallOf(1125);
        var all = SensorOnlyMinutes(Readings).ToDictionary(m => (m.DeskCode, m.MinuteUtc));
        var quiet = SensorOnlyMinutes([.. Readings.Where(r => !(silenced.Contains(r.DeskKey) && r.TimeUtc >= from && r.TimeUtc < to))]);

        silenced.Should().HaveCount(5);
        foreach (var m in quiet)
        {
            if (!silenced.Contains(m.DeskCode) || m.MinuteUtc < from || m.MinuteUtc >= to.AddMinutes(1))
            {
                m.Should().Be(all[(m.DeskCode, m.MinuteUtc)], "{0} at {1:HH:mm} is not silenced", m.DeskCode, m.MinuteUtc);
                continue;
            }

            // Last heard at 18:29 (stamped at the minute's start): stale from 18:31; heard again with the 18:45 reading.
            var unknown = m.MinuteUtc >= from.AddMinutes(1) && m.MinuteUtc < to;
            if (unknown)
            {
                m.Unknown.Should().Be(TimeSpan.FromMinutes(1), "{0} at {1:HH:mm}", m.DeskCode, m.MinuteUtc);
                m.Degraded.Should().BeTrue();
                m.Open.Should().Be(TimeSpan.Zero);
            }
            else
            {
                m.Unknown.Should().Be(TimeSpan.Zero, "{0} at {1:HH:mm}", m.DeskCode, m.MinuteUtc);
            }
        }
    }

    [Fact]
    public void Replay_Should_KeepEveryQueueOutput_When_TheDeskZoneSensorsJoinTheZone()
    {
        // The queue's traffic is unchanged by the desk sensors: replaying A-VIS with S-18 to S-20 added gives the golden
        // replay's A-VIS outputs exactly, plus the desk readings; without the desk zones in the geometry, no desk reading.
        var golden = ReferenceReplay.Run(zones: ["A-VIS"]).Outputs["DMO/A-VIS"][0];
        var withDesks = Desks.Value.Outputs["DMO/A-VIS"][0];
        var withoutDeskZones = ReferenceReplay.RunDesks(deskGeometry: false).Outputs["DMO/A-VIS"][0];

        foreach (var outputs in new[] { withDesks, withoutDeskZones })
        {
            outputs.Minutes.Should().BeEquivalentTo(golden.Minutes, o => o.WithStrictOrdering());
            outputs.Bins.Should().BeEquivalentTo(golden.Bins, o => o.WithStrictOrdering());
            outputs.Live.Should().BeEquivalentTo(golden.Live, o => o.WithStrictOrdering());
            outputs.Recomputations.Should().BeEquivalentTo(golden.Recomputations, o => o.WithStrictOrdering());
            outputs.Outages.Should().BeEquivalentTo(golden.Outages, o => o.WithStrictOrdering());
            outputs.Lines.Should().BeEquivalentTo(golden.Lines, o => o.WithStrictOrdering());
            outputs.Health.Should().BeEquivalentTo(golden.Health, o => o.WithStrictOrdering());
            outputs.Overflow.Should().BeEquivalentTo(golden.Overflow, o => o.WithStrictOrdering());
        }

        golden.DeskReadings.Should().BeEmpty();
        withoutDeskZones.DeskReadings.Should().BeEmpty();
        withDesks.DeskReadings.Should().NotBeEmpty();
        withDesks.DeskReadings.Select(r => r.DeskKey).Distinct().Should().BeEquivalentTo(ReferenceReplay.VisitorDesks.Select(ReferenceReplay.DeskKeyOf));
        output.WriteLine($"Desk readings {withDesks.DeskReadings.Count}; desk case head {Desks.Value.Hashes.OutputHead} over {Desks.Value.Hashes.Outputs} outputs");
    }

    [Fact]
    public void Replay_Should_GiveTheSameDeskReadings_When_RunTwice()
    {
        var again = ReferenceReplay.RunDesks();

        again.Hashes.Should().Be(Desks.Value.Hashes);
    }
}
