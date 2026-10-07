using System.Text;
using System.Text.Json;
using Ariva.Business.Contracts.Aman.V1;
using Ariva.Core.Flights;
using Ariva.Infra.Flights.Acris;
using Ariva.Infra.Flights.Aidx;
using Ariva.Simulation.Api.Emulators;
using Ariva.Simulation.Api.Emulators.Aman;
using Ariva.Simulation.Api.Emulators.Aodb;
using Ariva.Simulation.Api.Scenarios.Engine;
using FluentAssertions;

namespace Ariva.UnitTests.Simulation;

/// <summary>
/// ARV-029: the AMAN, immigration and AODB emulators' content. The AMAN feed follows the scenario (desk sessions as the
/// desks open, pause and close; desk and e-gate intervals that add up to what the scenario's lanes processed; reject
/// categories that sum and are suppressed under 3; lane demand with the reference split), carries no identities and is
/// deterministic; AMAN's codes map one to one onto the demo airport's desks. The AODB's AIDX messages and ACRIS answers
/// are read and mapped by Ariva's own readers without a refusal, and their times move as the day plays.
/// </summary>
public sealed class FeedEmulatorTests
{
    private static readonly ScenarioDay Day = ScenarioDay.Run(ScenarioConfig.Reference(ScenarioModel.DefaultSeed));
    private static readonly DateTime DayStart = new(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);
    private static readonly IReadOnlySet<string> Dmo = new HashSet<string>(["DMO"]);

    private static DateTime TimeOf(int minute) => DayStart.AddMinutes(minute);

    private static AmanMinute Build(int minute, bool first = false, BorderSides sides = BorderSides.Both) =>
        AmanFeed.Build(Day, minute, TimeOf, "DMO", sides, first);

    [Fact]
    public void AmanCodes_Should_MapEveryBorderDeskAndEgateOneToOne_When_Listed()
    {
        var all = AmanCodes.All();
        all.Should().HaveCount(2 * 22 + 6 + 4);
        all.Select(p => p.Aman).Should().OnlyHaveUniqueItems().And.Contain(["IN01", "IN22", "EGIN1", "EGIN6", "OUT01", "EGOUT4"]);
        all.Should().Contain(("IN07", "AR-07")).And.Contain(("EGOUT2", "DG-2"));
        AmanCodes.Of("A01").Should().BeNull("check-in counters are not border positions");
    }

    [Fact]
    public void DeskSessions_Should_StateOpenDesksFirstThenOnlyChanges_When_TheDayPlays()
    {
        var first = Build(600, first: true);
        first.Sessions.Should().NotBeEmpty().And.OnlyContain(s => s.State != DeskSessionState.Closed && s.LaneCategory.Length == 3);

        // Replaying the changes minute by minute gives the scenario's desk states at every minute.
        var states = first.Sessions.ToDictionary(s => s.DeskCode, s => s.State);
        for (var minute = 601; minute <= 900; minute++)
        {
            var played = Build(minute);
            played.Sessions.Should().NotContain(s => s.OccurredAtUtc != new DateTimeOffset(TimeOf(minute), TimeSpan.Zero));
            foreach (var change in played.Sessions)
            {
                states.GetValueOrDefault(change.DeskCode, DeskSessionState.Closed).Should().NotBe(change.State, "a change changes something");
                states[change.DeskCode] = change.State;
                (change.State == DeskSessionState.Closed ? change.LaneCategory.Length == 0 : change.LaneCategory is "CRW" or "CIT" or "RES" or "VIS").Should().BeTrue();
            }

            var open = played.Desks.Select(d => d.DeskCode).ToHashSet();
            states.Where(s => s.Value != DeskSessionState.Closed).Select(s => s.Key).Should().BeEquivalentTo(open, "every desk with a session reports its interval");
        }
    }

    [Fact]
    public void Intervals_Should_AddUpToWhatTheScenarioProcessed_When_SummedOverAnHour()
    {
        var minutes = Enumerable.Range(1080, 60).Select(m => Build(m, first: m == 1080)).ToList();
        foreach (var lane in new[] { "CIT", "RES", "VIS" })
        {
            var scenario = Enumerable.Range(1080, 60).Sum(m => Day.D[ScenarioModel.Q("A-" + lane)][m + ScenarioModel.Pre]);
            var reported = minutes.SelectMany(m => m.Desks).Where(d => d.DeskCode.StartsWith("IN", StringComparison.Ordinal) && d.LaneCategory == lane)
                .Sum(d => d.DocumentsProcessed);
            // ARV-117c: a desk counts a transaction's family whole when it completes, so the hour's edges carry the families in
            // progress and the work varies around the mean; the whole day adds up within 4 percent (AmanIntervalScenarioTests).
            reported.Should().BeCloseTo((int)Math.Round(scenario), (uint)Math.Max(10, scenario * 0.12), $"{lane} documents over the hour");
        }

        minutes.SelectMany(m => m.Desks).Should().OnlyContain(d => d.IntervalSeconds == 60 && d.TransactionsProcessed <= d.DocumentsProcessed &&
            (d.DocumentsProcessed == 0) == (d.TransactionsProcessed == 0) && d.MeanServiceSeconds >= 0 && d.P90ServiceSeconds >= d.MeanServiceSeconds);
        var gates = minutes.SelectMany(m => m.Gates).ToList();
        gates.Should().NotBeEmpty().And.OnlyContain(g => g.IntervalSeconds == 60 && g.Accepted + g.Rejected == g.Attempts &&
            g.RejectsByCategory.Values.Sum() == g.Rejected && g.RejectsByCategory.All(c => c.Key == EGateRejectCategory.Other || c.Value >= 3));
        var egate = Enumerable.Range(1080, 60).Sum(m => Day.D[ScenarioModel.Q("A-EG")][m + ScenarioModel.Pre]);
        gates.Where(g => g.GateCode.StartsWith("EGIN", StringComparison.Ordinal)).Sum(g => g.Attempts).Should()
            .BeCloseTo((int)Math.Round(egate), (uint)Math.Max(6, egate * 0.08));
    }

    [Fact]
    public void RejectCategories_Should_SumAndFoldSmallCellsIntoOther_When_Split()
    {
        AmanFeed.Categories(2, "t", "EGIN1", 1).Should().BeEquivalentTo(new Dictionary<EGateRejectCategory, int> { [EGateRejectCategory.Other] = 2 });
        var ten = AmanFeed.Categories(10, "t", "EGIN1", 1);
        ten.Values.Sum().Should().Be(10);
        ten.Should().OnlyContain(c => c.Key == EGateRejectCategory.Other || c.Value >= 3);
        AmanFeed.Categories(0, "t", "EGIN1", 1).Should().BeEmpty();
    }

    [Fact]
    public void LaneDemand_Should_UseTheReferenceSplit_When_ComputedFor200Passengers()
    {
        var demand = AmanFeed.LaneDemand("DMO", "DM214-20261001-A", new DateTime(2026, 10, 1, 14, 5, 0, DateTimeKind.Utc), 200, ScenarioModel.BaseMix,
            DateTimeOffset.UnixEpoch, "x");
        demand.PassengersByLane.Should().BeEquivalentTo(new Dictionary<string, int> { ["CIT"] = 42, ["RES"] = 24, ["VIS"] = 70, ["CRW"] = 4 });
        demand.EGateEligible.Should().Be(44);

        // Each inbound flight is computed two hours out and again 30 minutes out, the second one later.
        var flight = Day.Schedule.Arrivals.First(f => f.Sched > 700 && !f.Scripted);
        var first = Build(flight.Sched - 120).Demand.Single(d => d.FlightKey.StartsWith(flight.Code.Replace(" ", "", StringComparison.Ordinal) + "-", StringComparison.Ordinal));
        var second = Build(flight.Sched - 30).Demand.Single(d => d.FlightKey == first.FlightKey);
        second.ComputedAtUtc.Should().BeAfter(first.ComputedAtUtc);
        second.BoardedTotal.Should().Be(flight.Pax);
        second.ScheduledArrivalUtc.Should().Be(new DateTimeOffset(TimeOf(flight.Sched), TimeSpan.Zero));
        first.SourceEventId.Should().NotBe(second.SourceEventId);
    }

    [Fact]
    public void Feed_Should_BeDeterministicAndCarryNoIdentity_When_BuiltTwice()
    {
        var a = JsonSerializer.Serialize(Build(1110, first: true), AmanContracts.Json);
        a.Should().Be(JsonSerializer.Serialize(Build(1110, first: true), AmanContracts.Json));
        var minute = Build(1110, first: true);
        var ids = minute.Sessions.Select(s => s.SourceEventId).Concat(minute.Desks.Select(d => d.SourceEventId)).Concat(minute.Gates.Select(g => g.SourceEventId))
            .Concat(minute.Demand.Select(d => d.SourceEventId)).ToList();
        ids.Should().OnlyHaveUniqueItems().And.OnlyContain(id => id.Length <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
        foreach (var forbidden in new[] { "officer", "passport", "documentNumber", "name\"", "nationality" })
            a.Should().NotContainEquivalentOf(forbidden);
        a.Should().Contain("\"state\":\"Opened\"", "enums are names on the wire").And.Contain("\"intervalSeconds\":60");
    }

    [Fact]
    public void Sides_Should_LimitWhatIsReported_When_OnlyDeparturesAreCovered()
    {
        var departures = Build(1110, first: true, BorderSides.Departure);
        departures.Desks.Should().NotBeEmpty().And.OnlyContain(d => d.DeskCode.StartsWith("OUT", StringComparison.Ordinal));
        departures.Gates.Should().OnlyContain(g => g.GateCode.StartsWith("EGOUT", StringComparison.Ordinal));
        departures.Demand.Should().BeEmpty("inbound lane demand belongs to arrivals");
    }

    [Fact]
    public void Aidx_Should_BeReadAndMappedByAriva_When_TheScheduleIsPushed()
    {
        var legs = AodbSchedule.At(Day, 1110, TimeOf, "DMO");
        legs.Should().HaveCountGreaterThan(50);
        var xml = AodbSchedule.Aidx([.. legs.Take(500)], new DateTime(2026, 10, 3, 18, 30, 0, DateTimeKind.Utc), "SIM-1110-0");

        var (message, error) = AidxReader.Read(Encoding.UTF8.GetBytes(xml));
        error.Should().BeNull();
        message.Legs.Should().HaveCount(Math.Min(500, legs.Count));
        var now = TimeOf(1110);
        foreach (var leg in message.Legs)
        {
            var (data, refused) = AidxMapping.ToLeg(leg, Dmo);
            refused.Should().BeNull();
            FlightRules.Check(data, now).Errors.Should().BeEmpty();
            legs.Select(l => l.Key).Should().Contain(data.FlightKey, "AMAN, AIDX and ACRIS share the flight key");
        }
    }

    [Fact]
    public void Acris_Should_BeReadAndMappedByAriva_When_Pulled()
    {
        var legs = AodbSchedule.At(Day, 1110, TimeOf, "DMO");
        var (flights, error) = AcrisReader.Read(AodbSchedule.Acris(legs));
        error.Should().BeNull();
        flights.Should().HaveCount(legs.Count);
        foreach (var flight in flights)
        {
            var (data, refused) = AcrisMapping.ToLeg(flight, Dmo);
            refused.Should().BeNull();
            FlightRules.Check(data, TimeOf(1110)).Errors.Should().BeEmpty();
        }
    }

    [Fact]
    public void Schedule_Should_PublishEstimatesAndBlockTimesAsTheyHappen_When_TheDayPlays()
    {
        var flight = Day.Schedule.Arrivals.First(f => f.Sched > 700 && f.OnBlock > f.Sched - 30);
        var key = AmanFeed.FlightKey(flight.Code, TimeOf(flight.Sched - AmanFeed.BlockMinutes(flight.Code)), arrival: true);
        AodbLeg At(int minute) => AodbSchedule.At(Day, minute, TimeOf, "DMO").Single(l => l.Key == key);

        At(flight.Sched - 61).EstimatedUtc.Should().BeNull();
        At(flight.Sched - 60).Should().Match<AodbLeg>(l => l.EstimatedUtc == TimeOf(flight.Eibt) && l.ChangedAt == flight.Sched - 60);
        At(flight.OnBlock - 1).ActualBlockUtc.Should().BeNull();
        At(flight.OnBlock).Should().Match<AodbLeg>(l => l.ActualBlockUtc == TimeOf(flight.OnBlock) && l.ChangedAt == flight.OnBlock);
    }

    [Fact]
    public void FeedTime_Should_LayTheDayOnRealMinutes_When_ARunStartsOrJumps()
    {
        var time = new FeedTime();
        var wall = new DateTime(2026, 10, 3, 9, 41, 27, DateTimeKind.Utc);
        time.Observe(600, _ => wall).Should().Be(new DateTime(2026, 10, 3, 9, 41, 0, DateTimeKind.Utc), "the start minute is the current wall minute");
        time.Observe(600, _ => wall.AddHours(5)).Should().Be(new DateTime(2026, 10, 3, 9, 41, 0, DateTimeKind.Utc), "another feed playing the same minute agrees");
        time.Observe(601, _ => wall.AddSeconds(1)).Should().Be(new DateTime(2026, 10, 3, 9, 42, 0, DateTimeKind.Utc), "minutes follow one minute apart at any speed");
        time.Observe(900, _ => wall.AddSeconds(2)).Should().Be(new DateTime(2026, 10, 3, 9, 41, 0, DateTimeKind.Utc), "a jump re-anchors");
    }

    [Theory]
    [InlineData("kafka.example.com:9092", "SaslPlaintext", "user", "secret", false)]
    [InlineData("kafka.example.com:9092", "Plaintext", null, null, false)]
    [InlineData("localhost:19092,kafka.example.com:9092", "Plaintext", null, null, false)]
    [InlineData("kafka.example.com:9093", "SaslSsl", "user", "secret", true)]
    [InlineData("localhost:19092", "Plaintext", null, null, true)]
    [InlineData("kafka-0.kafka.svc:9092,kafka-1.kafka.svc:9092", "Plaintext", null, null, true)]
    [InlineData("localhost:19092", "SaslPlaintext", "user", "secret", false)]
    public void AmanKafka_Should_KeepCredentialsAndTrafficOffTheNetworkInClear_When_Validated(string brokers, string protocol, string user, string password, bool valid)
    {
        var settings = new AmanEmulatorSettings
        {
            Kafka = new AmanKafkaSettings { BootstrapServers = brokers, SecurityProtocol = protocol, SaslMechanism = user is null ? null : "ScramSha512", SaslUsername = user, SaslPassword = password }
        };

        var problems = settings.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(settings)).Select(r => r.ErrorMessage).ToList();

        (problems.Count == 0).Should().Be(valid, string.Join(" ", problems));
        string.Join(" ", problems).Should().NotContain("secret", "a password is never quoted");
    }

    [Theory]
    [InlineData("http://ariva.example.com", true, false)]
    [InlineData("https://user:pw@ariva.example.com", false, false)]
    [InlineData("https://ariva.example.com/?x=1", false, false)]
    [InlineData("http://localhost:51005", false, false)]
    [InlineData("http://localhost:51005", true, true)]
    [InlineData("http://api-integration-service", true, true)]
    [InlineData("https://api-integration.example.com", false, true)]
    public void ArivaTarget_Should_RefuseUnsafeAddresses_When_Validated(string url, bool allowInsecure, bool valid)
    {
        var settings = new ArivaTargetSettings { IntegrationUrl = url, AllowInsecureTransport = allowInsecure };
        settings.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(settings)).Any().Should().Be(!valid);
    }
}
