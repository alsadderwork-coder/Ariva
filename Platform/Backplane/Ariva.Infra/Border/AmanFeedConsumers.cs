using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ariva.Business.Contracts.Aman.V1;
using Ariva.Core.Border;
using Ariva.Core.Services.Border;
using Ariva.Infra.Messaging.Inbox;
using Confluent.Kafka;
using MassTransit;

namespace Ariva.Infra.Border;

/// <summary>
/// AMAN's Kafka feed (ARV-048, wiki 08 section 10): the four <c>aman.feed.*.v1</c> topics, read strictly
/// (<see cref="AmanFeedJson{T}"/>; an unreadable record or one with an unknown member goes to the dead-letter topic),
/// each record handed to <see cref="ISvcImmigrationIntake"/> as the feed <see cref="Feed"/>, for the site the record names.
/// A record the rules refuse is logged with Ariva's reasons (never its values), counted and acknowledged: it would be
/// refused again on every retry.
/// </summary>
public static class AmanKafkaFeed
{
    public const string Feed = "aman-kafka";

    internal static void Log(ILogger logger, string contract, IReadOnlyList<ImmigrationItemResult> results)
    {
        foreach (var result in results.Where(r => r.HasErrors))
            logger.LogWarning("AMAN {Contract} record refused: {Reasons}", contract, string.Join(" ", result.Errors));
    }
}

public sealed class AmanDeskSessionConsumer(ISvcImmigrationIntake intake, ILogger<AmanDeskSessionConsumer> logger) : IConsumer<DeskSessionChanged>
{
    public async Task Consume(ConsumeContext<DeskSessionChanged> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        AmanKafkaFeed.Log(logger, "desk session", await intake.ApplyDeskSessionsAsync(ImmigrationScope.RecordSite, AmanKafkaFeed.Feed, [context.Message], context.CancellationToken));
    }
}

public sealed class AmanDeskIntervalConsumer(ISvcImmigrationIntake intake, ILogger<AmanDeskIntervalConsumer> logger) : IConsumer<DeskIntervalStats>
{
    public async Task Consume(ConsumeContext<DeskIntervalStats> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        AmanKafkaFeed.Log(logger, "desk interval", await intake.ApplyDeskIntervalsAsync(ImmigrationScope.RecordSite, AmanKafkaFeed.Feed, [context.Message], context.CancellationToken));
    }
}

public sealed class AmanEgateIntervalConsumer(ISvcImmigrationIntake intake, ILogger<AmanEgateIntervalConsumer> logger) : IConsumer<EGateIntervalStats>
{
    public async Task Consume(ConsumeContext<EGateIntervalStats> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        AmanKafkaFeed.Log(logger, "e-gate interval", await intake.ApplyEgateIntervalsAsync(ImmigrationScope.RecordSite, AmanKafkaFeed.Feed, [context.Message], context.CancellationToken));
    }
}

public sealed class AmanLaneDemandConsumer(ISvcImmigrationIntake intake, ILogger<AmanLaneDemandConsumer> logger) : IConsumer<InboundFlightLaneDemand>
{
    public async Task Consume(ConsumeContext<InboundFlightLaneDemand> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        AmanKafkaFeed.Log(logger, "lane demand", await intake.ApplyLaneDemandAsync(ImmigrationScope.RecordSite, AmanKafkaFeed.Feed, [context.Message], context.CancellationToken));
    }
}

/// <summary>
/// The inbox key of an AMAN record (ADR-0018 inbox): AMAN's records carry no Ariva event id, so the key is derived from
/// the contract and AMAN's source event id (the first 16 bytes of their SHA-256). A redelivered record is then skipped by
/// the inbox; the intake's unique source event id per site holds the same line for REST and Kafka together.
/// </summary>
public sealed class AmanInboxKeys : IInboxKey<DeskSessionChanged>, IInboxKey<DeskIntervalStats>, IInboxKey<EGateIntervalStats>, IInboxKey<InboundFlightLaneDemand>
{
    public Guid KeyOf(DeskSessionChanged message) => Key("desk-session-changed", message?.SiteCode, message?.SourceEventId);

    public Guid KeyOf(DeskIntervalStats message) => Key("desk-interval-stats", message?.SiteCode, message?.SourceEventId);

    public Guid KeyOf(EGateIntervalStats message) => Key("egate-interval-stats", message?.SiteCode, message?.SourceEventId);

    public Guid KeyOf(InboundFlightLaneDemand message) => Key("inbound-flight-lane-demand", message?.SiteCode, message?.SourceEventId);

    private static Guid Key(string contract, string site, string sourceEventId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"aman.feed|{contract}|{site}|{sourceEventId}")).AsSpan(0, 16));
}

/// <summary>
/// AMAN feed values read strictly (ARV-048, CWE-501): camelCase members exactly as the contract names them, no unknown
/// or repeated member, numbers as numbers, enums by their exact member name, times with an explicit offset
/// (<see cref="StrictFeedConverters"/>), at most 8 levels. Anything else is unreadable (null), which the endpoint's
/// dead-letter pipe sends to the dead-letter topic with its raw bytes.
/// </summary>
public sealed class AmanFeedJson<T> : IDeserializer<T>
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false,
            NumberHandling = JsonNumberHandling.Strict,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            MaxDepth = 8
        };
        foreach (var converter in StrictFeedConverters.All())
            options.Converters.Add(converter);
        return options;
    }

    public T Deserialize(ReadOnlySpan<byte> data, bool isNull, SerializationContext context)
    {
        if (isNull)
            return default;
        try
        {
            return JsonSerializer.Deserialize<T>(data, Options);
        }
        catch (JsonException)
        {
            return default;
        }
        catch (NotSupportedException)
        {
            return default;
        }
    }
}
