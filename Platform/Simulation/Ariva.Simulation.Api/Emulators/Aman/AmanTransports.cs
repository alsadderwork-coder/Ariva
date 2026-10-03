using System.Text.Json;
using System.Text.Json.Serialization;
using Ariva.Business.Contracts.Aman.V1;
using Confluent.Kafka;

namespace Ariva.Simulation.Api.Emulators.Aman;

/// <summary>The four AMAN feed contracts: their Kafka topic, Integration API path segment and record key.</summary>
public static class AmanContracts
{
    public const string DeskSessions = "desk-sessions";
    public const string DeskIntervalStats = "desk-interval-stats";
    public const string EgateIntervalStats = "egate-interval-stats";
    public const string InboundLaneDemand = "inbound-lane-demand";

    public static readonly IReadOnlyList<string> All = [DeskSessions, DeskIntervalStats, EgateIntervalStats, InboundLaneDemand];

    /// <summary>The topic AMAN publishes the contract on (ADR-0019).</summary>
    public static string Topic(string kind) => kind switch
    {
        DeskSessions => "aman.feed.desk-session-changed.v1",
        DeskIntervalStats => "aman.feed.desk-interval-stats.v1",
        EgateIntervalStats => "aman.feed.egate-interval-stats.v1",
        InboundLaneDemand => "aman.feed.inbound-flight-lane-demand.v1",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>JSON on the wire: camelCase, enums by name (also as dictionary keys), as Ariva reads its Kafka topics.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>The records of a minute by contract, each with its Kafka key (desk, gate or flight).</summary>
    public static IEnumerable<(string Kind, IReadOnlyList<(string Key, object Record)> Records)> Of(AmanMinute minute)
    {
        ArgumentNullException.ThrowIfNull(minute);
        yield return (DeskSessions, [.. minute.Sessions.Select(r => (r.DeskCode, (object)r))]);
        yield return (DeskIntervalStats, [.. minute.Desks.Select(r => (r.DeskCode, (object)r))]);
        yield return (EgateIntervalStats, [.. minute.Gates.Select(r => (r.GateCode, (object)r))]);
        yield return (InboundLaneDemand, [.. minute.Demand.Select(r => (r.FlightKey, (object)r))]);
    }
}

/// <summary>
/// The records the emulated AMAN published, kept for its mock Integration API (ARV-029, for Ariva's outbound AMAN
/// connector, ARV-050): per contract, in order, each with a sequence number, for <see cref="AmanEmulatorSettings.RetainMinutes"/>
/// demo minutes and at most 100,000 records per contract. Thread safe.
/// </summary>
public sealed class AmanFeedBuffer
{
    private const int MaxRecords = 100_000;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<(long Sequence, int Minute, object Record)>> _records =
        AmanContracts.All.ToDictionary(k => k, _ => new List<(long, int, object)>(), StringComparer.Ordinal);
    private long _sequence;

    public void Add(AmanMinute minute, int retainMinutes)
    {
        ArgumentNullException.ThrowIfNull(minute);
        lock (_gate)
        {
            foreach (var (kind, records) in AmanContracts.Of(minute))
            {
                var list = _records[kind];
                foreach (var (_, record) in records)
                    list.Add((++_sequence, minute.Minute, record));
                list.RemoveAll(r => r.Minute < minute.Minute - retainMinutes || r.Minute > minute.Minute);
                if (list.Count > MaxRecords)
                    list.RemoveRange(0, list.Count - MaxRecords);
            }
        }
    }

    /// <summary>Records of a contract after a sequence number, oldest first, at most <paramref name="limit"/>, and the last sequence returned.</summary>
    public (IReadOnlyList<object> Items, long Next) After(string kind, long after, int limit)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(kind, out var list))
                return ([], after);
            var page = list.Where(r => r.Sequence > after).Take(limit).ToList();
            return ([.. page.Select(r => r.Record)], page.Count == 0 ? after : page[^1].Sequence);
        }
    }
}

/// <summary>
/// AMAN's Kafka producer for the feed topics (ARV-029): string keys (desk, gate or flight), JSON values as Ariva reads
/// them, acks from all replicas, idempotent. Built on first use from <see cref="AmanKafkaSettings"/>; null settings mean
/// no Kafka. The SASL password is never logged.
/// </summary>
public sealed class AmanKafka(Func<AmanKafkaSettings> settings, ILogger<AmanKafka> logger) : IDisposable
{
    private readonly Lock _gate = new();
    private IProducer<string, string> _producer;
    private string _bootstrap;

    public bool IsConfigured => settings()?.IsConfigured == true;

    /// <summary>Publishes every record of the minute; the number delivered and the first error.</summary>
    public async Task<(int Delivered, string Error)> PublishAsync(AmanMinute minute, CancellationToken ct)
    {
        var producer = Producer();
        if (producer is null)
            return (0, null);
        var deliveries = new List<Task<DeliveryResult<string, string>>>();
        foreach (var (kind, records) in AmanContracts.Of(minute))
        {
            var topic = AmanContracts.Topic(kind);
            foreach (var (key, record) in records)
                deliveries.Add(producer.ProduceAsync(topic, new Message<string, string> { Key = key, Value = JsonSerializer.Serialize(record, record.GetType(), AmanContracts.Json) }, ct));
        }

        var delivered = 0;
        string error = null;
        foreach (var delivery in deliveries)
        {
            try
            {
                await delivery;
                delivered++;
            }
            catch (ProduceException<string, string> e)
            {
                error ??= e.Error.Code.ToString();
            }
        }

        if (error is not null)
            logger.LogWarning("AMAN emulator: Kafka refused {Failed} of {Count} records ({Error})", deliveries.Count - delivered, deliveries.Count, error);
        return (delivered, error);
    }

    private IProducer<string, string> Producer()
    {
        var current = settings();
        if (current?.IsConfigured != true)
            return null;
        lock (_gate)
        {
            if (_producer is not null && _bootstrap == current.BootstrapServers)
                return _producer;
            _producer?.Dispose();
            var config = new ProducerConfig
            {
                BootstrapServers = current.BootstrapServers,
                ClientId = "aman-emulator",
                Acks = Acks.All,
                EnableIdempotence = true,
                MessageTimeoutMs = 15_000,
                SecurityProtocol = Enum.Parse<SecurityProtocol>(current.SecurityProtocol, ignoreCase: true)
            };
            if (!string.IsNullOrEmpty(current.SaslMechanism))
            {
                config.SaslMechanism = Enum.Parse<SaslMechanism>(current.SaslMechanism, ignoreCase: true);
                config.SaslUsername = current.SaslUsername;
                config.SaslPassword = current.SaslPassword;
            }

            if (!string.IsNullOrEmpty(current.SslCaLocation))
                config.SslCaLocation = current.SslCaLocation;
            _producer = new ProducerBuilder<string, string>(config).SetLogHandler((_, message) => logger.LogDebug("AMAN emulator Kafka: {Message}", message.Message)).Build();
            _bootstrap = current.BootstrapServers;
            return _producer;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _producer?.Flush(TimeSpan.FromSeconds(5));
            _producer?.Dispose();
            _producer = null;
        }
    }
}
