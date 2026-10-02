using System.Text.Json;
using Ariva.Core.Queueing;
using Ariva.Infra.Sensing;
using Ariva.Simulation.Api.Emulators.Sensors;
using Ariva.Simulation.Api.Scenarios.Engine;
using FluentAssertions;

namespace Ariva.UnitTests.Queueing;

/// <summary>
/// ARV-031 against the reference day: the emulator's tracked crossings for Handler B's check-in island C (S-50) and
/// arrivals Visitors (S-15) go through Ingest's mappers, the queue engine and the bin accumulator. The scripted evening
/// shows in the bins as it does in the prototype: island C's 19:00, 19:15 and 19:30 bins end above 15 minutes, every
/// bin of the day becomes final, and the bins agree with the scenario's own realised waits.
/// </summary>
public sealed class BinScenarioTests
{
    private static readonly Lazy<ScenarioDay> Day = new(() => ScenarioDay.Run(ScenarioConfig.Reference() with { Lite = true }));
    private static readonly DateTime Midnight = new(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime WallOf(double minute) => Midnight.AddMinutes(minute);

    private static QueueZoneGeometry GeometryOf(string zone) => new(zone,
        new HashSet<string>(StringComparer.Ordinal) { zone + " entry" }, new HashSet<string>(StringComparer.Ordinal) { zone + " exit" },
        new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));

    internal static List<BinResult> PlayDay(string sensorId, string zone)
    {
        var engine = new QueueStateEngine(GeometryOf(zone), new QueueEngineSettings { Lateness = TimeSpan.FromSeconds(30) });
        var bins = new BinAccumulator(zone, 12);
        var published = new List<BinResult>();
        var sensor = SensorTraffic.Sensor(sensorId);
        for (var minute = 0; minute < ScenarioModel.Day; minute++)
        {
            var push = SensorTraffic.Build(Day.Value, sensor, EmulatedDialect.Canonical, minute, minute, WallOf, WallOf(minute + 1));
            using var document = JsonDocument.Parse(push.Json);
            var mapped = CanonicalPushMapper.Map(document.RootElement, 3000);
            var received = WallOf(minute + 1).AddSeconds(1);
            foreach (var c in mapped.Crossings)
                engine.Offer(new QueueCrossing(c.LineName, c.Direction, sensorId + "/" + c.TrackId, c.TimeUtc), received);
            published.AddRange(bins.Accept(engine.Advance(received)).Bins);
        }

        for (var extra = 1; extra <= 180; extra++)
            published.AddRange(bins.Accept(engine.Advance(WallOf(ScenarioModel.Day + extra))).Bins);
        return [.. published.Where(b => b.Status == BinStatus.Final)];
    }

    [Fact]
    public void HandlerB_Should_BreachInTheBinsOf1900To1930_When_TheReferenceDayIsMeasured()
    {
        var finals = PlayDay("S-50", "CI-C");

        var breached = finals.Where(b => b.StartUtc >= WallOf(1080) && b.StartUtc < WallOf(1260) && b.Waits.P90Minutes > 15)
            .Select(b => ScenarioMath.Clock((b.StartUtc - Midnight).TotalMinutes)).ToList();
        breached.Should().Equal("19:00", "19:15", "19:30");
        finals.Select(b => b.StartUtc).Should().OnlyHaveUniqueItems("a bin is final once");
        finals.Count(b => b.StartUtc < WallOf(ScenarioModel.Day)).Should().Be(96, "every bin of the day becomes final");
    }

    [Fact]
    public void Bins_Should_AgreeWithTheScenariosOwnWaits_When_TheVisitorsWaveIsMeasured()
    {
        var finals = PlayDay("S-15", "A-VIS");
        var q = ScenarioModel.Q("A-VIS");

        foreach (var bin in finals.Where(b => b.StartUtc >= WallOf(1035) && b.StartUtc < WallOf(1140)))
        {
            var start = (int)(bin.StartUtc - Midnight).TotalMinutes;
            // The scenario's fluid mean wait over the bin's entry minutes, weighted by arrivals.
            double weighted = 0, people = 0;
            for (var m = start; m < start + 15; m++)
            {
                var i = m + ScenarioModel.Pre;
                if (Day.Value.A[q][i] < 1e-6 || double.IsNaN(Day.Value.MeanWait[q][i]))
                    continue;
                weighted += Day.Value.A[q][i] * Day.Value.MeanWait[q][i];
                people += Day.Value.A[q][i];
            }

            if (people < 5)
                continue;
            bin.Waits.MeanMinutes.Should().BeApproximately(weighted / people, 1.0, "bin {0}: whole passengers wait like the fluid model", ScenarioMath.Clock(start));
            bin.Entries.Should().BeCloseTo((int)Math.Round(people), 2);
        }
    }
}
