using System.Text.Json;
using System.Text.Json.Nodes;
using Ariva.Business.Contracts.Aman.V1;
using Ariva.Core.Border;
using Ariva.Infra.Border;
using PactNet;
using PactNet.Matchers;

namespace Ariva.UnitTests.Contracts;

/// <summary>
/// What Ariva needs from AMAN's feed (ARV-068): one message interaction per V1 contract on <c>aman.feed.*.v1</c>, with
/// matchers for the shape Ariva reads strictly (<see cref="AmanFeedJson{T}"/>) and the rules it applies
/// (<see cref="ImmigrationRules"/>): codes, times with an offset, one-minute intervals, enums by name, counts as integers.
/// The examples are consistent records Ariva accepts. Pact lets a provider add members; Ariva does not (an unknown member
/// is dead-lettered) and the data boundary forbids identifiers; Pact also matches a pattern against the text of a number or a
/// boolean. So the pact's metadata states the closed shape and the provider harness (wiki 08) checks members and their JSON
/// types beside the pact.
/// </summary>
public static class AmanFeedPacts
{
    public const string Consumer = "Ariva";
    public const string Provider = "AMAN";
    public const string FileName = "Ariva-AMAN.json";

    /// <summary>The time the examples are read at: rules on times are checked against it, so the examples never age.</summary>
    public static readonly DateTime ReadAt = new(2026, 10, 4, 8, 17, 0, DateTimeKind.Utc);

    // [0-9], never \d: Pact's regex engine (Rust) counts any Unicode digit as \d, and Ariva's reader does not.
    private const string Time = @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?(Z|[+-][0-9]{2}:[0-9]{2})$";
    private const string Minute = @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:00(\.0{1,7})?(Z|[+-][0-9]{2}:[0-9]{2})$";
    private const string Site = @"^[A-Z0-9]{2,8}(-[A-Z0-9]{1,8})?$";
    private const string Code = @"^[A-Z0-9][A-Z0-9._/-]{0,31}$";
    private const string SourceEvent = @"^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}$";
    private const string Lane = "^(CIT|RES|VIS|CRW)$";

    public static readonly string BoundaryRule =
        "Aggregate only (ADR-0010): no officer, traveller, passenger or document identifier, name, nationality or date of birth " +
        "in any member. The members in each example are the whole contract: Ariva dead-letters a record with any other member, " +
        "so a provider adds none, and each member keeps its example's JSON type (Pact allows extra members and matches a pattern " +
        "against a number's or a boolean's text; the provider harness in Ariva's wiki 08 checks both).";

    public static readonly string SuppressionRule =
        "E-gate rejects by category: a category other than Other holds 0 or at least 3; smaller counts are added to Other.";

    /// <summary>One interaction: what AMAN sends, on which topic, the provider state, the body with matchers and how Ariva reads it.</summary>
    public sealed record Interaction(string Description, string State, string Contract, string Topic, Type Record, object Body, Func<JsonElement, IReadOnlyList<string>> Read);

    public static IReadOnlyList<Interaction> All { get; } =
    [
        new("a desk session change", "a border desk opens, pauses or closes", "desk-session-changed", "aman.feed.desk-session-changed.v1", typeof(DeskSessionChanged),
            new
            {
                siteCode = Match.Regex("DMO", Site),
                deskCode = Match.Regex("IN01", Code),
                state = Match.Regex("Opened", "^(Opened|Closed|Paused)$"),
                // Empty exactly when the desk closes.
                laneCategory = Match.Regex("CIT", "^(CIT|RES|VIS|CRW)?$"),
                occurredAtUtc = Match.Regex("2026-10-04T08:14:37+00:00", Time),
                sourceEventId = Match.Regex("aman-ds-IN01-0001", SourceEvent)
            },
            json => Read<DeskSessionChanged>(json, r => ImmigrationRules.Check(r, ReadAt, TimeSpan.FromMinutes(5)))),
        new("a desk's one-minute statistics", "a border desk's minute closes", "desk-interval-stats", "aman.feed.desk-interval-stats.v1", typeof(DeskIntervalStats),
            new
            {
                siteCode = Match.Regex("DMO", Site),
                deskCode = Match.Regex("IN01", Code),
                intervalStartUtc = Match.Regex("2026-10-04T08:15:00+00:00", Minute),
                intervalSeconds = 60,
                transactionsProcessed = Match.Integer(2),
                documentsProcessed = Match.Integer(3),
                meanServiceSeconds = Match.Number(41.5),
                p90ServiceSeconds = Match.Number(66.4),
                meanCycleSeconds = Match.Number(30.0),
                laneCategory = Match.Regex("CIT", Lane),
                sourceEventId = Match.Regex("aman-dk-IN01-0001", SourceEvent)
            },
            json => Read<DeskIntervalStats>(json, r => ImmigrationRules.Check(r, ReadAt, TimeSpan.FromMinutes(5)))),
        new("an e-gate's one-minute statistics", "an e-gate's minute closes", "egate-interval-stats", "aman.feed.egate-interval-stats.v1", typeof(EGateIntervalStats),
            new
            {
                siteCode = Match.Regex("DMO", Site),
                gateCode = Match.Regex("EG01", Code),
                intervalStartUtc = Match.Regex("2026-10-04T08:15:00+00:00", Minute),
                intervalSeconds = 60,
                attempts = Match.Integer(9),
                accepted = Match.Integer(5),
                rejected = Match.Integer(4),
                rejectsByCategory = EachKeyAndValue(
                    new JsonObject { ["DocumentRead"] = 3, ["Other"] = 1 },
                    "^(Other|DocumentRead|BiometricCapture|Eligibility|ReferredToOfficer|Technical)$"),
                meanCycleSeconds = Match.Number(14.2),
                sourceEventId = Match.Regex("aman-eg-EG01-0001", SourceEvent)
            },
            json => Read<EGateIntervalStats>(json, r => ImmigrationRules.Check(r, ReadAt, TimeSpan.FromMinutes(5)))),
        new("an inbound flight's lane demand", "AMAN computes an inbound flight's lane demand", "inbound-flight-lane-demand", "aman.feed.inbound-flight-lane-demand.v1", typeof(InboundFlightLaneDemand),
            new
            {
                siteCode = Match.Regex("DMO", Site),
                flightKey = Match.Regex("DM214-20261004-A", SourceEvent),
                scheduledArrivalUtc = Match.Regex("2026-10-04T10:40:00+00:00", Time),
                boardedTotal = Match.Integer(180),
                passengersByLane = EachKeyAndValue(new JsonObject { ["CIT"] = 40, ["VIS"] = 70 }, Lane),
                eGateEligible = Match.Integer(60),
                computedAtUtc = Match.Regex("2026-10-04T08:10:00+00:00", Time),
                sourceEventId = Match.Regex("aman-ld-DM214-0001", SourceEvent)
            },
            json => Read<InboundFlightLaneDemand>(json, r => ImmigrationRules.Check(r, ReadAt, TimeSpan.FromMinutes(5))))
    ];

    /// <summary>
    /// A map whose keys match <paramref name="keyPattern"/> and whose values are integers, any number of entries (Pact V4
    /// each-key and each-value, which PactNet has no helper for; the Pact FFI reads them from the integration JSON).
    /// </summary>
    private static JsonObject EachKeyAndValue(JsonObject example, string keyPattern) => new()
    {
        ["pact:matcher:type"] = "each-value",
        ["rules"] = new JsonArray(new JsonObject { ["pact:matcher:type"] = "integer" }),
        ["value"] = new JsonObject
        {
            ["pact:matcher:type"] = "each-key",
            ["rules"] = new JsonArray(new JsonObject { ["pact:matcher:type"] = "regex", ["regex"] = keyPattern }),
            ["value"] = example
        }
    };

    /// <summary>
    /// What Ariva makes of a message: read strictly (null is a dead letter), then the intake's rules. The answer is the
    /// reasons it would refuse it; none when it is kept.
    /// </summary>
    private static IReadOnlyList<string> Read<T>(JsonElement json, Func<T, IReadOnlyList<string>> rules)
        where T : class
    {
        var record = new AmanFeedJson<T>().Deserialize(JsonSerializer.SerializeToUtf8Bytes(json), isNull: false, default);
        return record is null ? ["Unreadable: Ariva would dead-letter it."] : rules(record);
    }

    /// <summary>The message pact, written to <paramref name="directory"/>, every example read by Ariva; the reasons per interaction.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Write(string directory)
    {
        // The Pact FFI reports usage to Pact's analytics unless PACT_DO_NOT_TRACK is set in the process environment
        // (Ariva.UnitTests.runsettings and ci.yml set it; .NET cannot set it for native code once the process runs).
        if (!string.Equals(Environment.GetEnvironmentVariable("PACT_DO_NOT_TRACK"), "true", StringComparison.Ordinal))
            throw new InvalidOperationException("Set PACT_DO_NOT_TRACK=true before running the pact tests (Ariva.UnitTests.runsettings does).");
        var config = new PactConfig { PactDir = directory, DefaultJsonSettings = new JsonSerializerOptions() };
        var pact = Pact.V4(Consumer, Provider, config).WithMessageInteractions()
            // One namespace per entry: the FFI keeps only the last value written to a namespace.
            .WithPactMetadata("arivaContract", "version", ContractVersion.Current)
            .WithPactMetadata("arivaDataBoundary", "rule", BoundaryRule)
            .WithPactMetadata("arivaSmallCellSuppression", "rule", SuppressionRule);
        var results = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var interaction in All)
        {
            pact.ExpectsToReceive(interaction.Description)
                .Given(interaction.State)
                .WithMetadata("contentType", "application/json")
                .WithMetadata("kafkaTopic", interaction.Topic)
                .WithJsonContent(interaction.Body)
                .Verify<JsonElement>(message => results[interaction.Description] = interaction.Read(message));
        }

        return results;
    }
}
