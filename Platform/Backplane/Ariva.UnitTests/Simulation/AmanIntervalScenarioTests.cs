using System.Text.Json;
using Ariva.Business.Contracts.Aman.V1;
using Ariva.Core.Border;
using Ariva.Core.Desks;
using Ariva.Simulation.Api.Emulators.Aman;
using Ariva.Simulation.Api.Scenarios.Engine;
using FluentAssertions;

namespace Ariva.UnitTests.Simulation;

/// <summary>
/// ARV-117c on the reference day (seed 9303, Ariva's own simulator, not field data): the AMAN emulator's desk interval
/// statistics come from the scenario's served people, worked through by each desk as discrete transactions
/// (<see cref="AmanDeskProcess"/>). Per lane, AMAN's transaction-weighted lane cycle time (F10, <see cref="LaneCycle"/>,
/// over the 12 minutes the desk term reads) is compared with the scenario's own service time: the people served in the
/// same minutes weighted by each desk's service time per person, times 1.25 people a transaction for the per-transaction
/// figure AMAN publishes; since ARV-117d also the published reading, per person in mean service time. Every record stays
/// inside the contract's bounds and carries counts and times only.
/// </summary>
public sealed class AmanIntervalScenarioTests(ITestOutputHelper output)
{
    private static readonly Lazy<ScenarioDay> Day = new(() => ScenarioDay.Run(ScenarioConfig.Reference() with { Lite = true }));
    private static readonly DateTime DayStart = new(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);

    private const double FamilySize = 1.25;
    private const int Window = 12;

    private static readonly string[] Lanes = ["VIS", "CIT", "RES", "CRW"];

    /// <summary>One minute of a lane: AMAN's lane cycle time per transaction and per person, the busy-time one per person, the scenario's service time per person, and whether every minute of the window ran at capacity.</summary>
    private sealed record LaneMinute(int Minute, double PerTransaction, double PerPerson, double BusyPerPerson, double Service, bool Busy, double Published = double.NaN);

    private static List<LaneMinute> Lane(string queue, int from, int to)
    {
        var day = Day.Value;
        var q = ScenarioModel.Q(queue);
        var desks = ScenarioModel.Queues[q].Servers.Count;
        var result = new List<LaneMinute>();
        for (var m = from; m <= to; m++)
        {
            var intervals = new List<DeskIntervalFigures>();
            double work = 0, people = 0;
            var busy = true;
            for (var x = m - Window; x <= m; x++)
            {
                var i = x + ScenarioModel.Pre;
                busy &= day.C[q][i] > 0 && day.D[q][i] >= 0.99 * day.C[q][i];
                var states = day.ServerStates(q, x);
                var active = states.Where(s => s.State is "serving" or "idle" or "unknown").ToList();
                var speed = active.Sum(s => 1 / Math.Max(s.Svc, 1));
                foreach (var s in active)
                {
                    var share = day.D[q][i] / Math.Max(s.Svc, 1) / speed;
                    work += share * Math.Max(s.Svc, 1);
                    people += share;
                }

                for (var k = 0; k < desks; k++)
                {
                    if (AmanDeskProcess.Of(day, q, k, x) is { Transactions: > 0 } f)
                        intervals.Add(f);
                }
            }

            if (people < 1 || LaneCycle.PerTransaction(intervals.Select(f => (f.MeanCycleSeconds, f.Transactions))) is not { } perTransaction)
                continue;
            // Per person: the same cycle seconds over the documents (people) instead of the transactions.
            var perPerson = LaneCycle.PerTransaction(intervals.Select(f => (f.MeanCycleSeconds * f.Transactions / f.Documents, f.Documents))) ?? double.NaN;
            var busyPerPerson = LaneCycle.PerTransaction(intervals.Select(f => (f.MeanBusyCycleSeconds * f.Transactions / f.Documents, f.Documents))) ?? double.NaN;
            // ARV-117d: the published reading, per person in mean service time (LaneCycle.PerPerson as the desk term takes it).
            var published = LaneCycle.PerPerson(intervals.Select(f => new DeskIntervalSample(f.Transactions, f.Documents, f.MeanServiceSeconds, f.P90ServiceSeconds,
                f.MeanCycleSeconds)), Ariva.Infra.Streaming.DeskTermSource.CycleMethod).Minutes ?? double.NaN;
            result.Add(new LaneMinute(m, perTransaction, perPerson, busyPerPerson, work / people / 60, busy, published));
        }

        return result;
    }

    private static double Median(IEnumerable<double> values)
    {
        var list = values.Order().ToList();
        return list.Count == 0 ? double.NaN : list.Count % 2 == 1 ? list[list.Count / 2] : (list[list.Count / 2 - 1] + list[list.Count / 2]) / 2;
    }

    [Fact]
    public void LaneCycle_Should_TrackTheScenarioServiceTime_When_TheLaneRunsAtCapacity()
    {
        output.WriteLine("Reference day, seed 9303 (Ariva's own simulator, not field data). Ratios are medians of AMAN's lane cycle time over the scenario's service time.");
        output.WriteLine("lane: minutes (at capacity) | scenario service min/person | per transaction / (1.25 x service): all, at capacity | per person: all, at capacity | busy time per person: all | published since ARV-117d: all, at capacity");
        var checkedLanes = 0;
        foreach (var (side, from, to) in new[] { ("A", 1040, 1225), ("D", 0, 1439) })
        {
            foreach (var lane in Lanes)
            {
                var minutes = Lane(side + "-" + lane, from, to);
                var atCapacity = minutes.Where(l => l.Busy).ToList();
                var perTransaction = Median(atCapacity.Select(l => l.PerTransaction / (FamilySize * l.Service)));
                var perPerson = Median(atCapacity.Select(l => l.PerPerson / l.Service));
                output.WriteLine($"{side}-{lane}: {minutes.Count} ({atCapacity.Count}) | {Median(minutes.Select(l => l.Service)):F2} | " +
                                 $"{Median(minutes.Select(l => l.PerTransaction / (FamilySize * l.Service))):F2}, {perTransaction:F2} | " +
                                 $"{Median(minutes.Select(l => l.PerPerson / l.Service)):F2}, {perPerson:F2} | {Median(minutes.Select(l => l.BusyPerPerson / l.Service)):F2} | " +
                                 $"{Median(minutes.Select(l => l.Published / l.Service)):F2}, {Median(atCapacity.Select(l => l.Published / l.Service)):F2}");
                minutes.Should().NotBeEmpty($"{side}-{lane} serves people in the window");
                minutes.Should().OnlyContain(l => l.PerTransaction >= l.BusyPerPerson, "a cycle in open time is never shorter than in working time, nor a transaction than a person");
                if (atCapacity.Count < 10)
                    continue;
                // Proposed: within 10 percent while the lane runs at capacity (no idle time between transaction starts).
                perTransaction.Should().BeInRange(0.9, 1.1, $"{side}-{lane}'s lane cycle per transaction tracks 1.25 people's service time");
                perPerson.Should().BeInRange(0.9, 1.1, $"{side}-{lane}'s lane cycle per person tracks the service time");
                Median(atCapacity.Select(l => l.Published / l.Service)).Should().BeInRange(0.9, 1.1, $"{side}-{lane}'s published lane cycle (ARV-117d) tracks the service time");
                checkedLanes++;
            }
        }

        checkedLanes.Should().BeGreaterThanOrEqualTo(3, "A-VIS, A-CIT and the departure lanes that run at capacity are checked");
    }

    [Fact]
    public void LaneCycle_Should_IncludeIdleTime_When_ADeskWaitsForPassengersWhileOpen()
    {
        // AMAN's cycle is start to next start while the desk is open (contract V1, data boundary): after a lull it carries the
        // idle time, so over a whole evening AMAN's lane cycle time is longer than the service time in quiet lanes. In
        // working time only it would track the service time; the published figure does not.
        var visitors = Lane("A-VIS", 1040, 1225);
        var crew = Lane("A-CRW", 1040, 1225);
        Median(visitors.Select(l => l.BusyPerPerson / l.Service)).Should().BeInRange(0.9, 1.1);
        Median(crew.Select(l => l.PerPerson / l.Service)).Should().BeGreaterThan(2, "one crew desk mostly idle between a few crews");
        Median(visitors.Select(l => l.PerPerson / l.Service)).Should().BeGreaterThan(1.1, "the Visitors desks idle between the evening's waves");
        // ARV-117d: the published reading leaves the idle time out, so it tracks the service time over all minutes too.
        Median(visitors.Select(l => l.Published / l.Service)).Should().BeInRange(0.9, 1.1);
        Median(crew.Select(l => l.Published / l.Service)).Should().BeInRange(0.8, 1.2, "the idle crew desk's published cycle is its service time, not its idle time");
    }

    [Fact]
    public void DeskIntervals_Should_AddUpToTheScenarioAndStayInsideTheContract_When_TheWholeDayPlays()
    {
        var day = Day.Value;
        var now = DayStart.AddDays(1);
        var minutes = Enumerable.Range(0, ScenarioModel.Day).Select(m => AmanFeed.Build(day, m, x => DayStart.AddMinutes(x), "DMO", BorderSides.Both, m == 0)).ToList();
        var desks = minutes.SelectMany(m => m.Desks).ToList();

        desks.SelectMany(d => ImmigrationRules.Check(d, now, TimeSpan.FromMinutes(5))).Should().BeEmpty("every record is inside the AMAN feed contract V1 bounds");
        desks.Should().OnlyContain(d => d.MeanCycleSeconds <= ImmigrationRules.MaxSeconds && d.P90ServiceSeconds <= ImmigrationRules.MaxSeconds);
        desks.Where(d => d.TransactionsProcessed > 0).Should().OnlyContain(d => d.MeanServiceSeconds > 0 && d.MeanCycleSeconds > 0 && d.P90ServiceSeconds >= d.MeanServiceSeconds);
        desks.Where(d => d.TransactionsProcessed == 0).Should().OnlyContain(d => d.MeanServiceSeconds == 0 && d.MeanCycleSeconds == 0 && d.P90ServiceSeconds == 0);

        // Documents add up to the people the scenario's desks served over the day, within 4 percent or 15 people (a transaction
        // counts its family whole when it completes, its work varies around the mean, and a close drops the one in progress);
        // 1.25 people a transaction on average.
        foreach (var side in new[] { "A", "D" })
        {
            foreach (var lane in Lanes)
            {
                var q = ScenarioModel.Q(side + "-" + lane);
                var served = Enumerable.Range(0, ScenarioModel.Day).Sum(m => day.ServerStates(q, m).Any(s => s.State is "serving" or "idle" or "unknown") ? day.D[q][m + ScenarioModel.Pre] : 0);
                var reported = desks.Where(d => d.DeskCode.StartsWith(side == "A" ? "IN" : "OUT", StringComparison.Ordinal) && d.LaneCategory == lane).ToList();
                var documents = reported.Sum(d => d.DocumentsProcessed);
                output.WriteLine($"{side}-{lane}: scenario served {served:F0}, AMAN documents {documents}, transactions {reported.Sum(d => d.TransactionsProcessed)}");
                if (served < 200)
                    continue;
                ((double)documents).Should().BeApproximately(served, Math.Max(served * 0.04, 15), $"{side}-{lane} documents over the day");
                var transactions = reported.Sum(d => d.TransactionsProcessed);
                if (transactions >= 1_000)
                    ((double)documents / transactions).Should().BeApproximately(FamilySize, 0.05, $"{side}-{lane} people a transaction");
            }
        }
    }

    [Fact]
    public void DeskIntervals_Should_BeTheSameEveryRun_When_TheSameSeedPlays()
    {
        // Deterministic for a seed: a fresh day gives the same records, byte for byte, whatever minute is read first.
        var other = ScenarioDay.Run(ScenarioConfig.Reference() with { Lite = true });
        var a = JsonSerializer.Serialize(AmanFeed.Build(Day.Value, 1110, x => DayStart.AddMinutes(x), "DMO", BorderSides.Both, firstOfRun: true).Desks, AmanContracts.Json);
        var b = JsonSerializer.Serialize(AmanFeed.Build(other, 1110, x => DayStart.AddMinutes(x), "DMO", BorderSides.Both, firstOfRun: true).Desks, AmanContracts.Json);
        a.Should().Be(b);
        JsonSerializer.Deserialize<List<DeskIntervalStats>>(a, AmanContracts.Json).Should().Contain(d => d.TransactionsProcessed > 1 && d.MeanCycleSeconds != Math.Round(60.0 / d.TransactionsProcessed, 1),
            "the cycle time is the desk's own, not the interval over its transaction count");
    }

    [Fact]
    public void Families_Should_AverageOneAndAQuarter_When_DrawnFromTheUnitInterval()
    {
        var sizes = Enumerable.Range(0, 10_000).Select(i => AmanDeskProcess.FamilySize((i + 0.5) / 10_000)).ToList();
        sizes.Average().Should().BeApproximately(FamilySize, 0.001);
        sizes.Should().OnlyContain(s => s >= 1 && s <= 3);
        var variations = Enumerable.Range(0, 100_000).Select(i => AmanDeskProcess.Variation((i + 0.5) / 100_000)).ToList();
        variations.Average().Should().BeApproximately(1, 0.01);
        variations.Should().OnlyContain(v => v >= 0.5 && v <= 4);
        AmanDeskProcess.Variation(1).Should().BeLessThanOrEqualTo(4, "the top of the unit interval stays bounded");
    }
}
