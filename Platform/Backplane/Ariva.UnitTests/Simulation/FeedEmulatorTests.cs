using System.Globalization;
using System.Text;
using System.Text.Json;
using Ariva.Business.Contracts.Aman.V1;
using Ariva.Core.Border;
using Ariva.Core.Flights;
using Ariva.Core.Integration;
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
/// are read and mapped by Ariva's own readers without a refusal, and their times move as the day plays. ARV-279c: the
/// source event ids and the REST batches' Idempotency-Keys name the anchor the day is laid on (one anchor, one set; another
/// anchor of the same date, other ids and keys with the same figures), and pass Ariva's own rules at their longest.
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

    #region ARV-279c: the anchor in the ids and batch keys

    /// <summary>Every source event id of a minute, sessions, desks, gates and lane demand in that order.</summary>
    private static IEnumerable<string> Ids(AmanMinute minute) =>
        minute.Sessions.Select(s => s.SourceEventId).Concat(minute.Desks.Select(d => d.SourceEventId)).Concat(minute.Gates.Select(g => g.SourceEventId))
            .Concat(minute.Demand.Select(d => d.SourceEventId));

    /// <summary>
    /// A minute's figures without its ids, times and flight keys (a flight key holds the origin date, which moves with the
    /// anchor): what the counts hashing the date alone keep the same on every anchor of one date.
    /// </summary>
    private static List<string> Figures(AmanMinute minute)
    {
        var figures = new List<string>();
        foreach (var s in minute.Sessions)
            figures.Add(string.Create(CultureInfo.InvariantCulture, $"ds {s.DeskCode} {s.State} {s.LaneCategory}"));
        foreach (var d in minute.Desks)
        {
            figures.Add(string.Create(CultureInfo.InvariantCulture,
                $"dk {d.DeskCode} {d.IntervalSeconds} {d.TransactionsProcessed} {d.DocumentsProcessed} {d.MeanServiceSeconds:R} {d.P90ServiceSeconds:R} {d.MeanCycleSeconds:R} {d.LaneCategory}"));
        }

        foreach (var g in minute.Gates)
        {
            var categories = string.Join(",", g.RejectsByCategory.Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.Key}={c.Value}")));
            figures.Add(string.Create(CultureInfo.InvariantCulture, $"eg {g.GateCode} {g.IntervalSeconds} {g.Attempts} {g.Accepted} {g.Rejected} {categories} {g.MeanCycleSeconds:R}"));
        }

        foreach (var d in minute.Demand)
        {
            var lanes = string.Join(",", d.PassengersByLane.Select(l => string.Create(CultureInfo.InvariantCulture, $"{l.Key}={l.Value}")));
            figures.Add(string.Create(CultureInfo.InvariantCulture, $"ld {d.BoardedTotal} {lanes} {d.EGateEligible}"));
        }

        return figures;
    }

    [Fact]
    public void SourceEventIds_Should_NameTheSystemSeedAndAnchor_When_AMinuteIsBuilt()
    {
        // <system>-<seed>-<yyyyMMdd'T'HHmm of minute 0 in UTC>-<kind>-<code>-<minute>; a lane demand ends with its flight key and computation.
        var anchor = new DateTime(2026, 10, 3, 5, 17, 0, DateTimeKind.Utc);
        var aman = AmanFeed.Build(Day, 1110, m => anchor.AddMinutes(m), "DMO", BorderSides.Both, firstOfRun: true);
        var other = AmanFeed.Build(Day, 1110, m => anchor.AddMinutes(m), "DMO", BorderSides.Departure, firstOfRun: true, system: "imm");

        aman.Sessions.Should().NotBeEmpty().And.OnlyContain(s => s.SourceEventId == "aman-9303-20261003T0517-ds-" + s.DeskCode + "-1110");
        aman.Desks.Should().NotBeEmpty().And.OnlyContain(d => d.SourceEventId == "aman-9303-20261003T0517-dk-" + d.DeskCode + "-1110");
        aman.Gates.Should().NotBeEmpty().And.OnlyContain(g => g.SourceEventId == "aman-9303-20261003T0517-eg-" + g.GateCode + "-1110");
        aman.Demand.Should().NotBeEmpty().And.OnlyContain(d => d.SourceEventId == "aman-9303-20261003T0517-ld-" + d.FlightKey + "-1" ||
            d.SourceEventId == "aman-9303-20261003T0517-ld-" + d.FlightKey + "-2");
        other.Desks.Should().NotBeEmpty().And.OnlyContain(d => d.SourceEventId == "imm-9303-20261003T0517-dk-" + d.DeskCode + "-1110", "another system's ids are its own");
        AmanFeed.IdTag("aman", 9303, anchor).Should().Be("aman-9303-20261003T0517");
        AmanFeed.CountTag("aman", 9303, anchor).Should().Be("aman-9303-20261003", "the counts hash the date alone");
    }

    [Fact]
    public void SourceEventIds_Should_DifferWhileTheFiguresStay_When_TheSameMinutesAreLaidOnAnotherAnchorOfTheDate()
    {
        // A second run on the same UTC date, or a demo restarted or jumped, lays the day on another minute (FeedTime).
        var midnight = new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);
        var later = new DateTime(2026, 10, 3, 9, 41, 0, DateTimeKind.Utc);
        List<AmanMinute> Play(DateTime dayStart) =>
            [.. Enumerable.Range(1080, 61).Select(m => AmanFeed.Build(Day, m, x => dayStart.AddMinutes(x), "DMO", BorderSides.Both, firstOfRun: m == 1080))];

        var first = Play(midnight);
        var again = Play(midnight);
        var other = Play(later);

        var ids = first.SelectMany(Ids).ToList();
        ids.Should().HaveCountGreaterThan(1000).And.OnlyHaveUniqueItems();
        again.SelectMany(Ids).Should().Equal(ids, "one anchor, one set of ids");
        other.SelectMany(Ids).Should().NotIntersectWith(ids, "the same minutes on another anchor of the date get other ids")
            .And.OnlyContain(id => id.StartsWith("aman-9303-20261003T0941-", StringComparison.Ordinal));
        other.SelectMany(Figures).Should().Equal(first.SelectMany(Figures), "the counts hash the date alone, so they are the same on every anchor of it");
        other.Select(m => m.Count).Should().Equal(first.Select(m => m.Count));
    }

    [Fact]
    public void SourceEventIds_Should_PassArivasRule_When_TheLongestSeedCodesAndMinutesAreUsed()
    {
        // Every id of the whole day as the emulator builds it (every desk, e-gate and flight it publishes, minutes 0000 to 1439).
        var anchor = new DateTime(2026, 12, 31, 23, 59, 0, DateTimeKind.Utc);
        var prefix = AmanFeed.IdTag("aman", ScenarioModel.DefaultSeed, anchor) + "-";
        var ids = Enumerable.Range(0, ScenarioModel.Day).SelectMany(m => Ids(AmanFeed.Build(Day, m, x => anchor.AddMinutes(x), "DMO", BorderSides.Both, m == 0))).ToList();
        ids.Should().NotBeEmpty().And.OnlyContain(id => id.StartsWith(prefix, StringComparison.Ordinal) && ImmigrationRules.IsSourceEventId(id));

        // The longest an id can be: the largest seed (ten digits), the longest system name ("aman"; the other emulator is "imm"),
        // the longest AMAN code (EGOUT1 to EGOUT4) at the last minute of the day in each kind, and the longest lane demand id.
        var tag = AmanFeed.IdTag("aman", uint.MaxValue, anchor);
        var code = AmanCodes.All().Select(p => p.Aman).MaxBy(c => c.Length);
        var flight = ids.Where(id => id.Contains("-ld-", StringComparison.Ordinal)).Select(id => id[prefix.Length..]).MaxBy(rest => rest.Length);
        string[] longest =
        [
            .. new[] { "ds", "dk", "eg" }.Select(kind => string.Create(CultureInfo.InvariantCulture, $"{tag}-{kind}-{code}-{ScenarioModel.Day - 1:D4}")),
            tag + "-" + flight
        ];
        code.Should().HaveLength(6);
        flight.Should().StartWith("ld-");
        longest.Should().OnlyContain(id => id.Length <= 64 && ImmigrationRules.IsSourceEventId(id),
            "Ariva keeps a source event id of 1 to 64 letters, digits or . _ : - (CWE-501)");
    }

    [Fact]
    public void BatchKey_Should_NameTheAnchorAndPassArivasKeyRule_When_TheLongestSeedMinuteAndContractAreUsed()
    {
        var midnight = new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);
        ImmigrationFeedEmulator.BatchKey(9303, midnight, 1110, AmanContracts.DeskIntervalStats, 0).Should().Be("sim-9303-20261003T0000-1110-desk-interval-stats");
        ImmigrationFeedEmulator.BatchKey(9303, midnight, 1110, AmanContracts.DeskIntervalStats, 1).Should().Be("sim-9303-20261003T0000-1110-desk-interval-stats-1");
        ImmigrationFeedEmulator.BatchKey(9303, midnight.AddMinutes(77), 1110, AmanContracts.DeskIntervalStats, 0).Should()
            .Be("sim-9303-20261003T0117-1110-desk-interval-stats", "another anchor of the date gives another key");

        // The largest seed, the last minute, every contract (egate-interval-stats is the longest) and a second part.
        var latest = new DateTime(2026, 12, 31, 23, 59, 0, DateTimeKind.Utc);
        var keys = AmanContracts.All.SelectMany(kind => new[] { 0, 1 }.Select(part => ImmigrationFeedEmulator.BatchKey(uint.MaxValue, latest, ScenarioModel.Day - 1, kind, part)))
            .ToList();
        keys.Should().Contain("sim-4294967295-20261231T2359-1439-egate-interval-stats-1");
        keys.Should().OnlyContain(k => k.Length <= 64 && IntegrationBatches.IsKey(k), "Ariva takes an Idempotency-Key of 8 to 64 letters, digits or . _ : - (CWE-501)");
    }

    #endregion

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

    [Fact]
    public void FeedTime_Should_LayTheNextMinuteOnItsWallMinute_When_RestartedAfterAFasterRun()
    {
        // A run at speed 60 plays minutes 1095 to 1099 within seconds, each dated a minute later: 1099 is four minutes ahead.
        var wall = new DateTime(2026, 10, 16, 9, 30, 10, DateTimeKind.Utc);
        var time = new FeedTime();
        var unrestarted = new FeedTime();
        for (var minute = 1095; minute <= 1099; minute++)
        {
            time.Observe(minute, _ => wall);
            unrestarted.Observe(minute, _ => wall);
        }

        unrestarted.Observe(1100, _ => wall.AddSeconds(5)).Should().Be(new DateTime(2026, 10, 16, 9, 35, 0, DateTimeKind.Utc),
            "a minute that follows inherits the faster run's lead (the aman-feed spec's refusal)");
        time.Restart();
        time.At(1100).Should().Be(default, "the anchor is forgotten");
        time.Observe(1100, _ => wall.AddSeconds(5)).Should().Be(new DateTime(2026, 10, 16, 9, 30, 0, DateTimeKind.Utc), "laid on its own wall minute");
        time.Observe(1101, _ => wall.AddSeconds(6)).Should().Be(new DateTime(2026, 10, 16, 9, 31, 0, DateTimeKind.Utc), "and the run follows from there");
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
