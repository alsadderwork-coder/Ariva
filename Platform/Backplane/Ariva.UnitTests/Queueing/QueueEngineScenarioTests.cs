using System.Text.Json;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Queueing;
using Ariva.Infra.Sensing;
using Ariva.Simulation.Api.Emulators.Sensors;
using Ariva.Simulation.Api.Scenarios.Engine;
using FluentAssertions;

namespace Ariva.UnitTests.Queueing;

/// <summary>
/// ARV-030 against the reference evening: the sensor emulator's pushes for arrivals Visitors (S-15) go through Ingest's
/// own mappers into the queue engine, as the Stream host will feed it. Tracked crossings (T3) give one realised wait per
/// passenger who entered in the window; interval counts of the same day (T1, Xovis) give cumulative-curve waits close
/// to them; nobody is lost or counted twice.
/// </summary>
public sealed class QueueEngineScenarioTests
{
    private static readonly Lazy<ScenarioDay> Day = new(() => ScenarioDay.Run(ScenarioConfig.Reference() with { Lite = true }));
    private static readonly DateTime Anchor = new(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime WallOf(double minute) => Anchor.AddMinutes(minute);

    private static readonly QueueZoneGeometry Visitors = new(
        "A-VIS",
        new HashSet<string>(StringComparer.Ordinal) { "A-VIS entry" },
        new HashSet<string>(StringComparer.Ordinal) { "A-VIS exit" },
        new HashSet<string>(StringComparer.Ordinal) { "A-OV entry" },
        new HashSet<string>(StringComparer.Ordinal) { "A-OV" });

    private static List<QueueStep> Play(EmulatedDialect dialect, int from, int to, QueueZoneGeometry geometry = null)
    {
        var engine = new QueueStateEngine(geometry ?? Visitors, new QueueEngineSettings { Lateness = TimeSpan.FromSeconds(30) });
        var sensor = SensorTraffic.Sensor("S-15");
        var steps = new List<QueueStep>();
        for (var minute = from; minute < to; minute++)
        {
            var push = SensorTraffic.Build(Day.Value, sensor, dialect, minute, minute, WallOf, WallOf(minute + 1));
            using var document = JsonDocument.Parse(push.Json);
            var mapped = dialect == EmulatedDialect.Xovis
                ? XovisPushMapper.Map(document.RootElement, new DevicePose(0, 0, 0), 3000)
                : CanonicalPushMapper.Map(document.RootElement, 3000);
            var received = WallOf(minute + 1).AddSeconds(1);
            foreach (var c in mapped.Crossings)
                engine.Offer(new QueueCrossing(c.LineName, c.Direction, "S-15/" + c.TrackId, c.TimeUtc), received);
            foreach (var i in mapped.Intervals)
                engine.Offer(new QueueInterval(i.LineName, i.In, i.Out, i.FromUtc, i.TimeUtc), received);
            foreach (var o in mapped.Occupancy)
                engine.Offer(new QueueOccupancy(o.ZoneName, o.Count, o.TimeUtc), received);
            steps.Add(engine.Advance(received));
        }

        steps.Add(engine.Advance(WallOf(to + 300)));
        return steps;
    }

    [Fact]
    public void Tracks_Should_GiveOneWaitPerPassenger_When_TheEveningIsPlayed()
    {
        var steps = Play(EmulatedDialect.Canonical, 1000, 1300);

        var waits = steps.SelectMany(s => s.Waits).ToList();
        var entries = steps.SelectMany(s => s.Movements).Sum(m => m.Entries);
        var exits = steps.SelectMany(s => s.Movements).Sum(m => m.Exits);
        var unmatched = steps.Sum(s => s.Rejections.UnmatchedExits);

        waits.Should().NotBeEmpty().And.OnlyContain(w => w.Method == WaitMethod.Track && w.Wait >= TimeSpan.Zero && !w.Degraded);
        (waits.Count + unmatched).Should().Be(exits, "every exit is a tracked wait or someone who entered before 16:40");
        waits.Select(w => w.TrackKey).Should().OnlyHaveUniqueItems();
        (entries - waits.Count).Should().Be(steps[^1].OpenEntrants + steps.Sum(s => s.Resolutions.Count), "everyone who entered is held, resolved or exited");
        steps.Sum(s => s.Rejections.Late + s.Rejections.Duplicates + s.Rejections.UnknownGeometry + s.Rejections.NegativeWaits).Should().Be(0);
        waits.Max(w => w.Wait).Should().BeGreaterThan(TimeSpan.FromMinutes(15), "the Visitors wave waits beyond 15 minutes at 18:05");
    }

    [Fact]
    public void Intervals_Should_GiveCumulativeWaitsCloseToTheTracks_When_TheSameEveningIsCounted()
    {
        var tracked = Play(EmulatedDialect.Canonical, 0, 1440).SelectMany(s => s.Waits).Where(w => w.EntryUtc >= WallOf(1080) && w.EntryUtc < WallOf(1110)).ToList();
        var counted = Play(EmulatedDialect.Xovis, 0, 1440).SelectMany(s => s.Waits).Where(w => w.EntryUtc >= WallOf(1080) && w.EntryUtc < WallOf(1110)).ToList();

        counted.Should().OnlyContain(w => w.Method == WaitMethod.Cumulative);
        counted.Count.Should().BeCloseTo(tracked.Count, 3);
        var trackedMean = tracked.Average(w => w.Wait.TotalMinutes);
        var countedMean = counted.Average(w => w.Wait.TotalMinutes);
        countedMean.Should().BeApproximately(trackedMean, 1.0, "minute counts spread evenly reproduce the passengers' waits within a minute");
    }

    [Fact]
    public void Length_Should_FollowTheOccupancyReadings_When_TheLeadSensorReportsThem()
    {
        // Without the overflow band in the geometry the queue zone's own reading is the whole length.
        var steps = Play(EmulatedDialect.Canonical, 1080, 1090, Visitors with { OverflowZones = new HashSet<string>(), OverflowEntryLines = new HashSet<string>() });
        var q = ScenarioModel.Q("A-VIS");
        int Occupancy(int minute) => (int)Math.Floor(Day.Value.CumA[q][minute + ScenarioModel.Pre + 1] + 1e-9) - (int)Math.Floor(Day.Value.CumD[q][minute + ScenarioModel.Pre + 1] + 1e-9);

        // The step after minute m is taken 30 seconds behind the reading at the end of m, so it shows the end of m - 1.
        for (var k = 1; k < 10; k++)
        {
            steps[k].Length.FromSensors.Should().BeTrue();
            steps[k].Length.Count.Should().Be(Occupancy(1080 + k - 1), "minute {0}", 1080 + k);
        }
    }
}
