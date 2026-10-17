using Ariva.Core.Domain.Entities;
using Ariva.Simulation.Api.Emulators.Aman;
using Ariva.Simulation.Api.Emulators.Validation;
using Ariva.Simulation.Api.Scenarios.Engine;
using FluentAssertions;

namespace Ariva.UnitTests.Simulation;

/// <summary>
/// ARV-151: the scenario's desk truth (<see cref="ScenarioDay.ServerStates"/>, the observers' <see cref="ValidationTruth"/>)
/// and the AMAN emulator's transactions (<see cref="AmanDeskProcess"/>) give every person served to the same desk. Two
/// implementations of one rule: the people a lane serves in a minute are shared among its working desks (open, not paused,
/// or hidden from the sensors) by speed, and each desk works its share times its own seconds per person. Compared desk by
/// desk and minute by minute over the whole day of every immigration lane, on the reference day (seed 9303) and on a
/// held-out day (seed 2026); a desk is serving in the truth exactly when it works at least half the minute or people wait.
/// </summary>
public sealed class DeskTruthTests(ITestOutputHelper output)
{
    private static readonly Dictionary<uint, Lazy<ScenarioDay>> Days = new()
    {
        [ScenarioModel.DefaultSeed] = new(() => ScenarioDay.Run(ScenarioConfig.Reference() with { Lite = true })),
        [2026] = new(() => ScenarioDay.Run(ScenarioConfig.Reference(2026) with { Lite = true }))
    };

    private const int FirstMinute = -ScenarioModel.Pre;
    private const int EndMinute = ScenarioModel.N - ScenarioModel.Pre;

    private static bool Works(string state) => state is "serving" or "idle" or "unknown";

    [Theory]
    [InlineData(9303u)]
    [InlineData(2026u)]
    public void WorkSeconds_Should_EqualTheAmanEmulatorsWorkedSeconds_When_EveryDeskMinuteIsCompared(uint seed)
    {
        var day = Days[seed].Value;
        var differences = new List<string>();
        int working = 0, worked = 0;
        foreach (var queue in AmanDeskProcess.Queues)
        {
            var q = ScenarioModel.Q(queue);
            for (var minute = FirstMinute; minute < EndMinute; minute++)
            {
                var states = day.ServerStates(q, minute);
                for (var k = 0; k < states.Count; k++)
                {
                    var truth = day.WorkSeconds(q, minute, k);
                    var aman = AmanDeskProcess.WorkedSeconds(day, q, k, minute);
                    // The emulator's desk works at most the minute (faster when the scenario's capacity noise serves more).
                    if (Math.Abs(aman - Math.Min(truth, 60)) > 1e-9 || (!Works(states[k].State) && truth != 0))
                        differences.Add($"{queue} {states[k].Id} ({states[k].State}) minute {minute}: truth {truth}, AMAN emulator {aman}");
                    working += Works(states[k].State) ? 1 : 0;
                    worked += truth > 0 ? 1 : 0;
                }
            }
        }

        output.WriteLine($"Seed {seed}: {working} working desk minutes, {worked} with people served, each the same seconds in the truth and in the AMAN emulator");
        differences.Should().BeEmpty("the truth and the AMAN emulator share the served people among the same desks alike");
        worked.Should().BeGreaterThan(10_000, "the day serves people at the immigration desks most of the day");
    }

    [Theory]
    [InlineData(9303u)]
    [InlineData(2026u)]
    public void ServerStates_Should_ServeExactlyWhenPeopleWaitOrTheDeskWorksHalfTheMinute_When_TheDeskWorks(uint seed)
    {
        var day = Days[seed].Value;
        var differences = new List<string>();
        int serving = 0, idle = 0, mixed = 0;
        foreach (var queue in AmanDeskProcess.Queues)
        {
            var q = ScenarioModel.Q(queue);
            for (var minute = FirstMinute; minute < EndMinute; minute++)
            {
                var states = day.ServerStates(q, minute);
                var waiting = day.L[q][minute + ScenarioModel.Pre] > 0.5;
                for (var k = 0; k < states.Count; k++)
                {
                    var serves = waiting || AmanDeskProcess.WorkedSeconds(day, q, k, minute) >= ScenarioDay.ServingSeconds;
                    var plain = states[k].State is "serving" or "idle";
                    if (ValidationTruth.Serves(day, q, minute, k) != serves || (plain && states[k].State != (serves ? "serving" : "idle")))
                        differences.Add($"{queue} {states[k].Id} ({states[k].State}) minute {minute}: serves {serves}");
                    serving += states[k].State == "serving" ? 1 : 0;
                    idle += states[k].State == "idle" ? 1 : 0;
                }

                // The working desks share the minute's people by speed, so each works the same seconds: they serve or idle together.
                mixed += states.Where(s => s.State is "serving" or "idle").Select(s => s.State).Distinct().Count() > 1 ? 1 : 0;
            }
        }

        output.WriteLine($"Seed {seed}: serving desk minutes {serving}, idle {idle}");
        differences.Should().BeEmpty("a desk serves when people wait or it works at least half the minute, and idles otherwise");
        mixed.Should().Be(0, "no lane has a serving and an idle desk in the same minute");
        serving.Should().BeGreaterThan(0);
        idle.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData(9303u)]
    [InlineData(2026u)]
    public void AmanDocuments_Should_ComeFromTheDesksTheTruthHasWorking_When_TransactionsComplete(uint seed)
    {
        // Person by person: every person AMAN counts at a desk completes in a minute in which the truth has that desk working
        // on people; over the day each desk's documents follow the people the truth gives it (each transaction's service time
        // varies around its people's, so the two agree in the mean, within the variation of the desk's few hundred families).
        var day = Days[seed].Value;
        var differences = new List<string>();
        var compared = 0;
        foreach (var queue in AmanDeskProcess.Queues)
        {
            var q = ScenarioModel.Q(queue);
            var desks = ScenarioModel.Queues[q].Servers.Count;
            var documents = new int[desks];
            var people = new double[desks];
            for (var minute = FirstMinute; minute < EndMinute; minute++)
            {
                var states = day.ServerStates(q, minute);
                for (var k = 0; k < desks; k++)
                {
                    var figures = AmanDeskProcess.Of(day, q, k, minute);
                    var work = day.WorkSeconds(q, minute, k);
                    if (figures.Transactions > 0 && (!Works(states[k].State) || !(work > 0)))
                        differences.Add($"{queue} {states[k].Id} ({states[k].State}) minute {minute}: {figures.Documents} documents, truth works {work} s");
                    documents[k] += figures.Documents;
                    people[k] += work / Math.Max(states[k].Svc, 1);
                }
            }

            for (var k = 0; k < desks; k++)
            {
                if (people[k] < 300)
                    continue;
                var ratio = documents[k] / people[k];
                output.WriteLine($"Seed {seed} {queue} desk {ScenarioModel.Queues[q].Servers[k]}: {documents[k]} documents, {people[k]:F1} people in the truth ({ratio:F3})");
                if (ratio is < 0.85 or > 1.15)
                    differences.Add($"{queue} {ScenarioModel.Queues[q].Servers[k]}: {documents[k]} documents for {people[k]:F1} people");
                compared++;
            }
        }

        differences.Should().BeEmpty();
        compared.Should().BeGreaterThan(20);
    }

    [Fact]
    public void Serves_Should_FollowTheWaitingOrTheHalfMinuteOfWork_When_ADeskIsHiddenFromTheSensors()
    {
        // The observers' truth of a desk the scenario hides from the sensors ("unknown"): Paused when it pauses, otherwise
        // Serving when it serves by the rule and Idle when it does not.
        var day = Days[ScenarioModel.DefaultSeed].Value;
        var seen = new Dictionary<ObservedDeskState, int>();
        foreach (var queue in AmanDeskProcess.Queues)
        {
            var q = ScenarioModel.Q(queue);
            for (var minute = 0; minute < ScenarioModel.Day; minute++)
            {
                var states = day.ServerStates(q, minute);
                for (var k = 0; k < states.Count; k++)
                {
                    if (states[k].State != "unknown")
                        continue;
                    var i = minute + ScenarioModel.Pre;
                    var pausing = day.Servers[q][k].Pause[i] != 0;
                    var serves = day.L[q][i] > 0.5 || day.WorkSeconds(q, minute, k) >= ScenarioDay.ServingSeconds;
                    var observed = ValidationTruth.Observed("unknown", pausing, ValidationTruth.Serves(day, q, minute, k));
                    observed.Should().Be(pausing ? ObservedDeskState.Paused : serves ? ObservedDeskState.Serving : ObservedDeskState.Idle);
                    seen[observed] = seen.GetValueOrDefault(observed) + 1;
                }
            }
        }

        output.WriteLine(string.Join(", ", seen.Select(s => $"{s.Key} {s.Value}")));
        seen.Should().NotBeEmpty("the reference day hides some desks from the sensors");
    }
}
