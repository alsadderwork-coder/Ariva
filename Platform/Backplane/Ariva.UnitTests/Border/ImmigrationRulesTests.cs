using System.Text;
using System.Text.Json;
using Ariva.Api.Integration.Batches;
using Ariva.Business.Contracts.Aman.V1;
using Ariva.Core.Border;
using Ariva.Infra.Border;
using Ariva.Simulation.Api.Emulators.Aman;
using Ariva.Simulation.Api.Scenarios.Engine;
using FluentAssertions;

namespace Ariva.UnitTests.Border;

/// <summary>
/// ARV-048, CWE-501: what an immigration record must be before Ariva keeps it. Every record the AMAN emulator publishes
/// for a busy minute passes; one-minute intervals on whole minutes, counts that add up, AMAN's small-cell suppression,
/// lanes and times are enforced, and a refusal never quotes a value. Both transports read strictly: an unknown member or
/// an enum that is not an exact member name (also as a dictionary key) or a time without an offset is refused (REST 400,
/// Kafka unreadable and dead-lettered); counts that would overflow a sum are refused without an exception.
/// </summary>
public sealed class ImmigrationRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 3, 18, 31, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Ahead = TimeSpan.FromMinutes(5);
    private static readonly DateTimeOffset Minute = new(2026, 10, 3, 18, 30, 0, TimeSpan.Zero);

    private static DeskIntervalStats Desk(int transactions = 2, int documents = 3, int seconds = 60, DateTimeOffset? start = null, string lane = "VIS", string code = "IN09",
        double service = 41, string id = "aman-1") =>
        new("DMO", code, start ?? Minute, seconds, transactions, documents, service, 60, 30, lane, id);

    private static EGateIntervalStats Gate(int attempts, int accepted, int rejected, Dictionary<EGateRejectCategory, int> categories) =>
        new("DMO", "EGIN3", Minute, 60, attempts, accepted, rejected, categories, 18, "aman-2");

    [Fact]
    public void Rules_Should_PassEveryRecord_When_TheAmanEmulatorPublishesABusyMinute()
    {
        var day = ScenarioDay.Run(ScenarioConfig.Reference(ScenarioModel.DefaultSeed));
        var dayStart = Minute.UtcDateTime.AddMinutes(-1110);
        var minute = AmanFeed.Build(day, 1110, m => dayStart.AddMinutes(m), "DMO", BorderSides.Both, firstOfRun: true);
        minute.Count.Should().BeGreaterThan(50);

        minute.Sessions.SelectMany(r => ImmigrationRules.Check(r, Now, Ahead)).Should().BeEmpty();
        minute.Desks.SelectMany(r => ImmigrationRules.Check(r, Now, Ahead)).Should().BeEmpty();
        minute.Gates.SelectMany(r => ImmigrationRules.Check(r, Now, Ahead)).Should().BeEmpty();
        minute.Demand.SelectMany(r => ImmigrationRules.Check(r, Now, Ahead)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("interval", "intervalSeconds is 60")]
    [InlineData("second", "whole minute")]
    [InlineData("future", "intervalStartUtc is at most 7 days ago")]
    [InlineData("old", "intervalStartUtc is at most 7 days ago")]
    [InlineData("sums", "sums do not add up")]
    [InlineData("zero", "sums do not add up")]
    [InlineData("lane", "laneCategory is CIT")]
    [InlineData("code", "deskCode is 1 to 32")]
    [InlineData("lower", "deskCode is 1 to 32")]
    [InlineData("service", "meanServiceSeconds")]
    [InlineData("event", "sourceEventId")]
    public void DeskInterval_Should_BeRefusedWithAnArivaReason_When_ItBreaksARule(string what, string reason)
    {
        var item = what switch
        {
            "interval" => Desk(seconds: 30),
            "second" => Desk(start: Minute.AddSeconds(15)),
            "future" => Desk(start: Minute.AddMinutes(10)),
            "old" => Desk(start: Minute.AddDays(-8)),
            "sums" => Desk(transactions: 5, documents: 3),
            "zero" => Desk(transactions: 1, documents: 0),
            "lane" => Desk(lane: "EG"),
            "code" => Desk(code: "<script>"),
            "lower" => Desk(code: "in09"),
            "service" => Desk(service: double.NaN),
            _ => Desk(id: "id with spaces")
        };

        var errors = ImmigrationRules.Check(item, Now, Ahead);

        errors.Should().ContainSingle().Which.Should().Contain(reason);
        string.Join(" ", errors).Should().NotContain("<script>").And.NotContain("id with spaces");
    }

    [Fact]
    public void EgateInterval_Should_EnforceSumsAndSmallCellSuppression_When_Checked()
    {
        ImmigrationRules.Check(Gate(10, 7, 3, new() { [EGateRejectCategory.DocumentRead] = 3 }), Now, Ahead).Should().BeEmpty();
        ImmigrationRules.Check(Gate(10, 8, 2, new() { [EGateRejectCategory.Other] = 2 }), Now, Ahead).Should().BeEmpty("small cells go to Other");
        ImmigrationRules.Check(Gate(10, 7, 3, new() { [EGateRejectCategory.Other] = 1, [EGateRejectCategory.DocumentRead] = 2 }), Now, Ahead)
            .Should().ContainSingle().Which.Should().Contain("small-cell suppression");
        ImmigrationRules.Check(Gate(10, 7, 2, new() { [EGateRejectCategory.Other] = 2 }), Now, Ahead).Should().ContainSingle().Which.Should().Contain("add up to it");
        ImmigrationRules.Check(Gate(10, 7, 3, new() { [EGateRejectCategory.Other] = 4 }), Now, Ahead).Should().ContainSingle().Which.Should().Contain("adds up to rejected");
        ImmigrationRules.Check(Gate(10, 7, 3, new() { [(EGateRejectCategory)42] = 3 }), Now, Ahead).Should().ContainSingle().Which.Should().Contain("coarse categories");
        ImmigrationRules.Check(Gate(0, 0, 0, null), Now, Ahead).Should().BeEmpty("a quiet gate reports zeros");
    }

    [Fact]
    public void DeskSession_Should_HaveALaneExactlyWhenOpen_When_Checked()
    {
        DeskSessionChanged Session(DeskSessionState state, string lane) => new("DMO", "IN09", state, lane, Minute, "aman-3");
        ImmigrationRules.Check(Session(DeskSessionState.Opened, "VIS"), Now, Ahead).Should().BeEmpty();
        ImmigrationRules.Check(Session(DeskSessionState.Paused, "CIT"), Now, Ahead).Should().BeEmpty();
        ImmigrationRules.Check(Session(DeskSessionState.Closed, ""), Now, Ahead).Should().BeEmpty();
        ImmigrationRules.Check(Session(DeskSessionState.Closed, "VIS"), Now, Ahead).Should().ContainSingle();
        ImmigrationRules.Check(Session(DeskSessionState.Opened, ""), Now, Ahead).Should().ContainSingle();
        ImmigrationRules.Check(Session((DeskSessionState)0, "VIS"), Now, Ahead).Should().ContainSingle().Which.Should().Contain("Opened, Closed or Paused");
    }

    [Fact]
    public void LaneDemand_Should_AddUpWithinTheBoardedTotal_When_Checked()
    {
        InboundFlightLaneDemand Demand(int boarded, Dictionary<string, int> lanes, int eligible = 44) =>
            new("DMO", "DM214-20261003-A", Minute.AddHours(2), boarded, lanes, eligible, Minute, "aman-4");
        var reference = new Dictionary<string, int> { ["CIT"] = 42, ["RES"] = 24, ["VIS"] = 70, ["CRW"] = 4 };
        ImmigrationRules.Check(Demand(200, reference), Now, Ahead).Should().BeEmpty();
        ImmigrationRules.Check(Demand(150, reference), Now, Ahead).Should().ContainSingle().Which.Should().Contain("at most boardedTotal");
        ImmigrationRules.Check(Demand(200, new() { ["EG"] = 10 }), Now, Ahead).Should().ContainSingle().Which.Should().Contain("CIT, RES, VIS and CRW");
        ImmigrationRules.Check(Demand(200, new() { ["VIS"] = -1 }), Now, Ahead).Should().ContainSingle();
        ImmigrationRules.Check(Demand(200, reference) with { FlightKey = "DM 214" }, Now, Ahead).Should().ContainSingle().Which.Should().Contain("flightKey");
        ImmigrationRules.Check(Demand(200, reference) with { ScheduledArrivalUtc = Minute.AddDays(-5) }, Now, Ahead).Should().ContainSingle();
    }

    [Fact]
    public void Bodies_Should_BeReadStrictly_When_AnUnknownMemberOrANumericEnumArrives()
    {
        var good = JsonSerializer.Serialize(new { items = new[] { new DeskSessionChanged("DMO", "IN09", DeskSessionState.Opened, "VIS", Minute, "a-1") } },
            AmanContracts.Json);
        var (batch, error) = BatchBody.Parse<DeskSessionChanged>(Encoding.UTF8.GetBytes(good), BatchBody.StrictWithEnumNames);
        error.Should().BeNull();
        batch.Items.Single().State.Should().Be(DeskSessionState.Opened);

        BatchBody.Parse<DeskSessionChanged>(Encoding.UTF8.GetBytes(good.Replace("\"siteCode\"", "\"officerId\":\"x\",\"siteCode\"", StringComparison.Ordinal)),
            BatchBody.StrictWithEnumNames).Error.Should().NotBeNull().And.NotContain("officerId", "an unknown member's name is never echoed");
        BatchBody.Parse<DeskSessionChanged>(Encoding.UTF8.GetBytes(good.Replace("\"Opened\"", "1", StringComparison.Ordinal)), BatchBody.StrictWithEnumNames)
            .Error.Should().NotBeNull("enums are names");

        // Kafka: the same strictness; an unreadable value is null, which the endpoint dead-letters.
        var record = JsonSerializer.SerializeToUtf8Bytes(new EGateIntervalStats("DMO", "EGIN3", Minute, 60, 4, 2, 2,
            new Dictionary<EGateRejectCategory, int> { [EGateRejectCategory.Other] = 2 }, 18, "a-2"), AmanContracts.Json);
        var reader = new AmanFeedJson<EGateIntervalStats>();
        reader.Deserialize(record, false, default).RejectsByCategory.Should().ContainKey(EGateRejectCategory.Other);
        var unknown = Encoding.UTF8.GetString(record).Replace("\"siteCode\"", "\"passportNumber\":\"x\",\"siteCode\"", StringComparison.Ordinal);
        reader.Deserialize(Encoding.UTF8.GetBytes(unknown), false, default).Should().BeNull();
        reader.Deserialize("not json"u8, false, default).Should().BeNull();
    }

    [Fact]
    public void EgateInterval_Should_BeRefusedWithoutAnException_When_CountsWouldOverflowTheSum()
    {
        var check = () => ImmigrationRules.Check(Gate(10, 7, 3,
            new() { [EGateRejectCategory.DocumentRead] = int.MaxValue, [EGateRejectCategory.BiometricCapture] = int.MaxValue }), Now, Ahead);
        check.Should().NotThrow().Which.Should().ContainSingle().Which.Should().Contain("coarse categories");
        ImmigrationRules.Check(Gate(10, int.MaxValue, int.MinValue + 11, null), Now, Ahead).Should().Contain(e => e.Contains("add up to it", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("\"state\":\"opened\"")]
    [InlineData("\"state\":\" Opened\"")]
    [InlineData("\"state\":\"Opened, Closed\"")]
    [InlineData("\"state\":\"1\"")]
    [InlineData("\"state\":1")]
    public void DeskSession_Should_BeUnreadableOnBothTransports_When_TheStateIsNotAnExactName(string state)
    {
        var record = JsonSerializer.Serialize(new DeskSessionChanged("DMO", "IN09", DeskSessionState.Opened, "VIS", Minute, "a-1"), AmanContracts.Json)
            .Replace("\"state\":\"Opened\"", state, StringComparison.Ordinal);
        record.Should().Contain(state);

        BatchBody.Parse<DeskSessionChanged>(Encoding.UTF8.GetBytes($"{{\"items\":[{record}]}}"), BatchBody.StrictWithEnumNames).Error.Should().NotBeNull();
        new AmanFeedJson<DeskSessionChanged>().Deserialize(Encoding.UTF8.GetBytes(record), false, default).Should().BeNull();
    }

    [Theory]
    [InlineData("documentRead")]
    [InlineData("DocumentRead, BiometricCapture")]
    [InlineData("1")]
    [InlineData(" DocumentRead")]
    public void EgateInterval_Should_BeUnreadableOnBothTransports_When_ACategoryKeyIsNotAnExactName(string key)
    {
        var record = JsonSerializer.Serialize(new EGateIntervalStats("DMO", "EGIN3", Minute, 60, 10, 7, 3,
            new Dictionary<EGateRejectCategory, int> { [EGateRejectCategory.DocumentRead] = 3 }, 18, "a-2"), AmanContracts.Json);
        record = record.Replace("\"DocumentRead\":", $"\"{key}\":", StringComparison.Ordinal);
        record.Should().Contain($"\"{key}\":");

        BatchBody.Parse<EGateIntervalStats>(Encoding.UTF8.GetBytes($"{{\"items\":[{record}]}}"), BatchBody.StrictWithEnumNames).Error.Should().NotBeNull();
        new AmanFeedJson<EGateIntervalStats>().Deserialize(Encoding.UTF8.GetBytes(record), false, default).Should().BeNull();
    }

    [Theory]
    [InlineData("2026-10-03T18:30:00", false)]
    [InlineData("2026-10-03T18:30:00.000", false)]
    [InlineData("2026-10-03", false)]
    [InlineData("2026-10-03T18:30:00Z", true)]
    [InlineData("2026-10-03T18:30:00+00:00", true)]
    [InlineData("2026-10-03T21:30:00+03:00", true)]
    public void Times_Should_CarryAnExplicitOffset_When_ReadOnEitherTransport(string time, bool readable)
    {
        var record = JsonSerializer.Serialize(new DeskSessionChanged("DMO", "IN09", DeskSessionState.Opened, "VIS", Minute, "a-1"), AmanContracts.Json);
        record = System.Text.RegularExpressions.Regex.Replace(record, "\"occurredAtUtc\":\"[^\"]*\"", $"\"occurredAtUtc\":\"{time}\"");

        var kafka = new AmanFeedJson<DeskSessionChanged>().Deserialize(Encoding.UTF8.GetBytes(record), false, default);
        var rest = BatchBody.Parse<DeskSessionChanged>(Encoding.UTF8.GetBytes($"{{\"items\":[{record}]}}"), BatchBody.StrictWithEnumNames);

        (kafka is not null).Should().Be(readable);
        (rest.Error is null).Should().Be(readable);
        if (readable)
            kafka.OccurredAtUtc.Should().Be(Minute);
    }

    [Fact]
    public void FeedConverters_Should_WriteWhatTheyRead_When_ARecordRoundTrips()
    {
        var gate = new EGateIntervalStats("DMO", "EGIN3", Minute, 60, 10, 7, 3, new Dictionary<EGateRejectCategory, int> { [EGateRejectCategory.DocumentRead] = 3 }, 18, "a-2");
        var json = JsonSerializer.Serialize(gate, AmanFeedJson<EGateIntervalStats>.Options);
        json.Should().Contain("\"DocumentRead\":3").And.Contain("\"intervalStartUtc\":\"2026-10-03T18:30:00");
        var read = JsonSerializer.Deserialize<EGateIntervalStats>(json, AmanFeedJson<EGateIntervalStats>.Options);
        read.RejectsByCategory.Should().Equal(gate.RejectsByCategory);
        read.IntervalStartUtc.Should().Be(gate.IntervalStartUtc);
    }

    [Fact]
    public void IdempotencyOperations_Should_MatchTheDatabaseCheck_When_TheLatestScriptSetsIt()
    {
        var script = Ariva.Infra.Timescale.SqlScriptCatalog.Embedded().Last(s => s.Sql.Contains("integration_idempotency_operation_check", StringComparison.Ordinal));
        var listed = System.Text.RegularExpressions.Regex.Matches(script.Sql[script.Sql.LastIndexOf("CHECK (operation IN", StringComparison.Ordinal)..], "'([a-z.-]+)'")
            .Select(m => m.Groups[1].Value);
        listed.Should().BeEquivalentTo(Ariva.Core.Integration.IntegrationBatches.Operations, "a claim for an operation the table refuses fails as a server error");
    }
}
