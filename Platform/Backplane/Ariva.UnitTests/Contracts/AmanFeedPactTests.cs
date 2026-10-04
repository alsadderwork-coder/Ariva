using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ariva.Business.Contracts.Aman.V1;
using Ariva.Core.Border;
using Ariva.Core.Messaging;
using Ariva.Simulation.Api.Emulators.Aman;
using Ariva.Simulation.Api.Scenarios.Engine;
using FluentAssertions;
using PactNet;
using PactNet.Exceptions;
using PactNet.Infrastructure.Outputters;
using PactNet.Verifier;

namespace Ariva.UnitTests.Contracts;

/// <summary>
/// The AMAN feed's message pact, written once per run into a directory of this process (two test runs in one checkout
/// never share a file), then published to .verify/pacts/Ariva-AMAN.json by an atomic rename (CI uploads it as an artifact).
/// Every example is read by Ariva's strict reader and intake rules while the pact is written.
/// </summary>
public sealed class AmanPactFile : IDisposable
{
    private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ariva-pact-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// True in a Stryker.NET run (ARV-069): its test server has STRYKER_MUTANT_FILE from the start (the initial run uses
    /// the unmutated build), and a mutated Ariva.Core carries Stryker's injected MutantControl. The pacts are no engine
    /// test, and the Pact FFI aborts the test host Stryker runs in server mode, so they do not run there.
    /// </summary>
    public static bool UnderMutationTesting { get; } =
        Environment.GetEnvironmentVariable("STRYKER_MUTANT_FILE") is not null ||
        typeof(Ariva.Core._IAssemblyMark).Assembly.GetTypes().Any(t => t.Name == "MutantControl");

    public AmanPactFile()
    {
        if (UnderMutationTesting)
        {
            Path = string.Empty;
            Reasons = new Dictionary<string, IReadOnlyList<string>>();
            Pact = [];
            return;
        }

        System.IO.Directory.CreateDirectory(_directory);
        Path = System.IO.Path.Combine(_directory, AmanFeedPacts.FileName);
        Reasons = AmanFeedPacts.Write(_directory);
        Pact = JsonNode.Parse(File.ReadAllText(Path))!.AsObject();

        var published = System.IO.Path.Combine(AmanFeedPactTests.Directory(), AmanFeedPacts.FileName);
        var staging = published + "." + Guid.NewGuid().ToString("N");
        File.Copy(Path, staging);
        File.Move(staging, published, overwrite: true);
    }

    public string Path { get; }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Reasons { get; }

    public JsonObject Pact { get; }

    public void Dispose()
    {
        if (System.IO.Directory.Exists(_directory))
            System.IO.Directory.Delete(_directory, recursive: true);
    }
}

/// <summary>
/// ARV-068, CWE-501: Ariva is the consumer of aman.feed.*; the pact says what it needs from each V1 message, AMAN verifies
/// its producers against it (wiki 08 has the harness), and the same harness is run here against the simulator's AMAN.
/// The data boundary is checked beside the pact: Pact lets a provider add members, Ariva dead-letters them.
/// </summary>
public sealed class AmanFeedPactTests : IClassFixture<AmanPactFile>
{
    private readonly AmanPactFile _file;

    public AmanFeedPactTests(AmanPactFile file)
    {
        Assert.SkipWhen(AmanPactFile.UnderMutationTesting, "Mutation run (Stryker.NET): the pacts are not engine tests.");
        _file = file;
    }

    private static readonly DateTimeOffset Minute = new(2026, 10, 3, 18, 30, 0, TimeSpan.Zero);

    internal static string Directory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "Ariva.slnx")))
            directory = directory.Parent;
        var pacts = System.IO.Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Ariva.slnx not found."), ".verify", "pacts");
        System.IO.Directory.CreateDirectory(pacts);
        return pacts;
    }

    public static TheoryData<string> Descriptions => [.. AmanFeedPacts.All.Select(i => i.Description)];

    private JsonObject Interaction(string description) =>
        ((JsonArray)_file.Pact["interactions"]!).Select(i => i!.AsObject()).Single(i => i["description"]!.GetValue<string>() == description);

    private static IEnumerable<string> Members(Type record) =>
        record.GetConstructors().Single().GetParameters().Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name!));

    [Theory]
    [MemberData(nameof(Descriptions))]
    public void Ariva_Should_KeepTheExample_When_ItReadsItLikeAnAmanRecord(string description)
    {
        _file.Reasons.Should().ContainKey(description);
        _file.Reasons[description].Should().BeEmpty("the example is a record Ariva's strict reader and intake rules keep");
    }

    [Fact]
    public void Pact_Should_HoldOneV4MessagePerContract_BetweenArivaAndAman()
    {
        _file.Pact["consumer"]!["name"]!.GetValue<string>().Should().Be("Ariva");
        _file.Pact["provider"]!["name"]!.GetValue<string>().Should().Be("AMAN");
        _file.Pact["metadata"]!["pactSpecification"]!["version"]!.GetValue<string>().Should().StartWith("4.");
        _file.Pact["metadata"]!["arivaContract"]!["version"]!.GetValue<string>().Should().Be(ContractVersion.Current);
        _file.Pact["metadata"]!["arivaDataBoundary"]!["rule"]!.GetValue<string>().Should().Contain("no officer, traveller, passenger or document identifier");
        _file.Pact["metadata"]!["arivaSmallCellSuppression"]!["rule"]!.GetValue<string>().Should().Contain("at least 3");

        var interactions = ((JsonArray)_file.Pact["interactions"]!).Select(i => i!.AsObject()).ToList();
        interactions.Should().HaveCount(4);
        interactions.Select(i => i["type"]!.GetValue<string>()).Should().AllBe("Asynchronous/Messages");
        AmanFeedPacts.All.Select(i => i.Topic).Should().BeEquivalentTo(KafkaTopics.All.Where(t => t.StartsWith("aman.feed.", StringComparison.Ordinal)));
        foreach (var interaction in AmanFeedPacts.All)
        {
            var written = Interaction(interaction.Description);
            written["metadata"]!["kafkaTopic"]!.GetValue<string>().Should().Be(interaction.Topic);
            written["providerStates"]![0]!["name"]!.GetValue<string>().Should().Be(interaction.State);
        }
    }

    [Theory]
    [MemberData(nameof(Descriptions))]
    public void Pact_Should_NameEveryMemberOfTheContract_AndNoOther(string description)
    {
        // The example is the whole contract, so it tells AMAN the closed member list the boundary check holds it to.
        var interaction = AmanFeedPacts.All.Single(i => i.Description == description);
        var content = Interaction(description)["contents"]!["content"]!.AsObject();
        content.Select(m => m.Key).Should().BeEquivalentTo(Members(interaction.Record));
    }

    [Theory]
    [MemberData(nameof(Descriptions))]
    public void Pact_Should_CarryNoIdentifier(string description)
    {
        var content = Interaction(description)["contents"]!["content"]!.AsObject();
        foreach (var (member, _) in content)
            DataBoundary.IdentifierIn(member).Should().BeNull("{0} is an aggregate member (ADR-0010)", member);
    }

    [Theory]
    [MemberData(nameof(Descriptions))]
    public void Pact_Should_MatchEveryMemberByItsRule_NotByTheExample(string description)
    {
        // AMAN's live records differ from the examples: each member but the fixed interval length has a matcher.
        var rules = Interaction(description)["matchingRules"]!["body"]!.AsObject();
        var content = Interaction(description)["contents"]!["content"]!.AsObject();
        foreach (var (member, _) in content.Where(m => m.Key != "intervalSeconds"))
            rules.Should().ContainKey($"$.{member}", "{0} is matched by type, pattern or each key and value", member);
        if (content.ContainsKey("intervalSeconds"))
            content["intervalSeconds"]!.GetValue<int>().Should().Be(ImmigrationRules.IntervalSeconds, "V1 intervals are one minute, matched exactly");
        foreach (var map in new[] { "rejectsByCategory", "passengersByLane" }.Where(content.ContainsKey))
        {
            var kinds = rules[$"$.{map}"]!["matchers"]!.AsArray().Select(m => m!["match"]!.GetValue<string>()).ToList();
            kinds.Should().Contain(["eachKey", "eachValue"], "{0} may hold any of its keys, each with an integer", map);
        }
    }

    [Theory]
    [MemberData(nameof(Descriptions))]
    public void Ariva_Should_DeadLetter_AnExampleWithAnIdentifierAdded(string description)
    {
        // The other half of the boundary: whatever Pact tolerates, Ariva's reader refuses a member outside the contract.
        var interaction = AmanFeedPacts.All.Single(i => i.Description == description);
        var content = Interaction(description)["contents"]!["content"]!.DeepClone().AsObject();
        content["officerId"] = "O-1234";
        interaction.Read(JsonSerializer.SerializeToElement(content)).Should().ContainSingle().Which.Should().Contain("dead-letter");
    }

    [Fact]
    public void SimulatorAman_Should_HonourThePact_AndTheClosedShape()
    {
        // The provider harness of wiki 08, run against the simulator's AMAN: open, paused and closed desks, busy and idle
        // gates (an empty reject map), several desks and flights, each verified in turn and read by Ariva's strict reader.
        var day = ScenarioDay.Run(ScenarioConfig.Reference(ScenarioModel.DefaultSeed));
        var dayStart = Minute.UtcDateTime.AddMinutes(-1110);
        var minutes = new List<AmanMinute> { AmanFeed.Build(day, 1110, x => dayStart.AddMinutes(x), "DMO", BorderSides.Both, firstOfRun: true) };
        // Desks close as the day's waves pass: the first minutes with a closed desk and with an idle e-gate join the sample.
        minutes.Add(Enumerable.Range(1111, 300).Select(m => AmanFeed.Build(day, m, x => dayStart.AddMinutes(x), "DMO", BorderSides.Both, firstOfRun: false))
            .First(m => m.Sessions.Any(s => s.State == DeskSessionState.Closed)));
        minutes.Add(Enumerable.Range(0, 1440).Select(m => AmanFeed.Build(day, m, x => dayStart.AddMinutes(x), "DMO", BorderSides.Both, firstOfRun: false))
            .First(m => m.Gates.Any(g => g.Rejected == 0)));
        var sessions = minutes.SelectMany(m => m.Sessions).GroupBy(s => s.State).Select(g => g.First()).ToList();
        var gates = minutes.SelectMany(m => m.Gates).GroupBy(g => g.Rejected == 0).Select(g => g.First()).ToList();
        sessions.Select(s => s.State).Should().BeEquivalentTo([DeskSessionState.Opened, DeskSessionState.Paused, DeskSessionState.Closed]);
        gates.Should().HaveCount(2, "an e-gate with rejects and one without");

        var samples = sessions.Select(s => (Description: "a desk session change", Record: (object)s))
            .Concat(minutes.SelectMany(m => m.Desks).Take(3).Select(d => ("a desk's one-minute statistics", (object)d)))
            .Concat(gates.Select(g => ("an e-gate's one-minute statistics", (object)g)))
            .Concat(minutes.SelectMany(m => m.Demand).Take(3).Select(d => ("an inbound flight's lane demand", (object)d)))
            .ToList();
        samples.Select(s => s.Item1).Distinct().Should().HaveCount(4);
        foreach (var (description, record) in samples)
        {
            Verify(description, record);
            ShapeErrors(description, record).Should().BeEmpty("the simulator's AMAN sends the contract's members only, each with its type");
            var read = AmanFeedPacts.All.Single(i => i.Description == description).Read(JsonSerializer.SerializeToElement(record, record.GetType(), AmanContracts.Json));
            read.Should().NotContain(r => r.Contains("dead-letter", StringComparison.Ordinal), "Ariva's strict reader reads what passes the harness");
        }
    }


    [Fact]
    public void Harness_Should_Fail_When_AmanBreaksTheContract()
    {
        // What breaks Ariva's reader fails verification: a wrong enum spelling, a time without an offset or with other digits,
        // a count as text, another interval length, a missing member, an unknown reject category.
        var open = new DeskSessionChanged("DMO", "IN01", DeskSessionState.Opened, "CIT", Minute, "aman-1");
        var desk = new DeskIntervalStats("DMO", "IN01", Minute, 60, 2, 3, 41, 66, 30, "CIT", "aman-2");
        var gate = new EGateIntervalStats("DMO", "EG01", Minute, 60, 9, 5, 4, new Dictionary<EGateRejectCategory, int> { [EGateRejectCategory.DocumentRead] = 4 }, 14, "aman-3");
        var broken = new (string Description, object Record)[]
        {
            ("a desk session change", Mutate(open, o => o["state"] = "OPEN")),
            ("a desk session change", Mutate(open, o => o["occurredAtUtc"] = "2026-10-03T18:30:00")),
            ("a desk session change", Mutate(open, o => o["occurredAtUtc"] = "\u0662\u0660\u0662\u0666-10-03T18:30:00+00:00")),
            ("a desk's one-minute statistics", Mutate(desk, o => o["documentsProcessed"] = "3")),
            ("a desk's one-minute statistics", Mutate(desk, o => o["intervalSeconds"] = 300)),
            ("a desk's one-minute statistics", Mutate(desk, o => o.Remove("laneCategory"))),
            ("an e-gate's one-minute statistics", Mutate(gate, o => o["rejectsByCategory"] = new JsonObject { ["Fraud"] = 4 })),
            ("an e-gate's one-minute statistics", Mutate(gate, o => o["rejectsByCategory"] = new JsonObject { ["DocumentRead"] = "4" }))
        };
        foreach (var (description, record) in broken)
            FluentActions.Invoking(() => Verify(description, record)).Should().Throw<PactFailureException>(record.ToString())
                .Which.Message.Should().Contain("has a matching body (FAILED)", "the body breaks the contract, not the transport")
                .And.NotContain("Request Failed");

        // And the shape check catches what Pact lets through: an added member, and a code or an id that is not a string
        // (Pact matches its pattern against the number's or the boolean's text). Ariva would dead-letter each.
        var passed = new (object Record, string Error)[]
        {
            (Mutate(open, o => o["officerBadge"] = "B-77"), "officerBadge is not a member of the contract"),
            (Mutate(open, o => o["deskCode"] = 101), "deskCode is Number, the contract has String"),
            (Mutate(open, o => o["siteCode"] = 12), "siteCode is Number, the contract has String"),
            (Mutate(open, o => o["sourceEventId"] = true), "sourceEventId is True, the contract has String")
        };
        var reader = AmanFeedPacts.All.Single(i => i.Description == "a desk session change");
        foreach (var (record, error) in passed)
        {
            FluentActions.Invoking(() => Verify("a desk session change", record)).Should().NotThrow("Pact lets {0} through", record.ToString());
            ShapeErrors("a desk session change", record).Should().Equal(error);
            reader.Read(JsonSerializer.SerializeToElement(record)).Should().ContainSingle().Which.Should().Contain("dead-letter");
        }
    }

    private static JsonObject Mutate(object record, Action<JsonObject> change)
    {
        var json = JsonSerializer.SerializeToNode(record, record.GetType(), AmanContracts.Json)!.AsObject();
        change(json);
        return json;
    }

    /// <summary>
    /// The shape check of wiki 08, beside the pact: each member the provider sends is one the pact's example names, with the
    /// example's JSON type (a string stays a string, a number a number, a map a map).
    /// </summary>
    private List<string> ShapeErrors(string description, object record)
    {
        var sent = JsonSerializer.SerializeToElement(record, record.GetType(), AmanContracts.Json);
        var example = JsonSerializer.SerializeToElement(Interaction(description)["contents"]!["content"]);
        var errors = new List<string>();
        foreach (var member in sent.EnumerateObject())
        {
            if (!example.TryGetProperty(member.Name, out var expected))
                errors.Add($"{member.Name} is not a member of the contract");
            else if (member.Value.ValueKind != expected.ValueKind)
                errors.Add($"{member.Name} is {member.Value.ValueKind}, the contract has {expected.ValueKind}");
        }

        return errors;
    }

    /// <summary>Pact's provider verification of one message (the wiki 08 harness): the record as the producer serialises it.</summary>
    private void Verify(string description, object record)
    {
        var topic = AmanFeedPacts.All.Single(i => i.Description == description).Topic;
        var output = new Collect();
        using var verifier = new PactVerifier(AmanFeedPacts.Provider, new PactVerifierConfig { Outputters = [output], LogLevel = PactLogLevel.Warn });
        try
        {
            verifier
                // PactNet 5.0.1 with messages only registers the provider with the scheme "message", and the Pact FFI then
                // builds a message:// URL it cannot call. Declaring an HTTP endpoint first makes the message transport plain
                // HTTP on the same host; no HTTP interaction exists, so nothing is sent to it (port 9 is the discard port).
                .WithHttpEndpoint(new Uri("http://localhost:9"))
                .WithMessages(scenarios => scenarios.Add(description, builder => builder
                    .WithMetadata(new { contentType = "application/json", kafkaTopic = topic })
                    .WithContent(() => record)), AmanContracts.Json)
                .WithFileSource(new FileInfo(_file.Path))
                .WithFilter(description)
                .Verify();
            // A filter that matches nothing verifies nothing and still passes: the message must have been checked.
            output.ToString().Should().Contain("has a matching body (OK)");
        }
        catch (PactFailureException failure)
        {
            throw new PactFailureException($"{failure.Message}{Environment.NewLine}{output}", failure);
        }
    }

    private sealed class Collect : IOutput
    {
        private readonly StringBuilder _text = new();

        public void WriteLine(string line) => _text.AppendLine(line);

        public override string ToString() => _text.ToString();
    }
}
