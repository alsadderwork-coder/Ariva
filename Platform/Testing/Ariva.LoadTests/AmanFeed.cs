using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Confluent.Kafka;

namespace Ariva.LoadTests;

/// <summary>
/// AMAN's feed for the pilot hall (ARV-124a), produced to the run's Kafka as AMAN publishes it (contracts V1 in
/// Ariva.Business.Contracts, keyed by the desk, gate or flight code; wiki 08): each desk opens once, then every desk and
/// every e-gate sends its one-minute statistics a few seconds after each minute ends, and three inbound flights announce
/// their lane demand. Ariva.Api.Integration consumes them into the border feed tables and Ariva.Api.Stream's desk feed
/// reads those. Topic names are KafkaTopics' (Ariva.Core), written out here: the harness references no Ariva assembly.
/// </summary>
public sealed class AmanFeed(PilotSettings settings) : IDisposable
{
    public const string SessionsTopic = "aman.feed.desk-session-changed.v1";
    public const string DeskIntervalsTopic = "aman.feed.desk-interval-stats.v1";
    public const string GateIntervalsTopic = "aman.feed.egate-interval-stats.v1";
    public const string LaneDemandTopic = "aman.feed.inbound-flight-lane-demand.v1";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _run = DateTime.UtcNow.ToString("HHmmss", CultureInfo.InvariantCulture);
    private readonly IProducer<string, string> _producer = new ProducerBuilder<string, string>(new ProducerConfig
    {
        BootstrapServers = settings.KafkaBootstrap,
        ClientId = "ariva-load-aman",
        Acks = Acks.All,
        EnableIdempotence = true,
        MessageTimeoutMs = 15_000
    }).Build();

    private long _sequence;

    /// <summary>Opens the desks, announces the flights, then sends every minute's statistics until <paramref name="ct"/> ends.</summary>
    public async Task<Measure> RunAsync(CancellationToken ct)
    {
        var measure = new Measure("AMAN feed records");
        measure.Start();
        var now = DateTimeOffset.UtcNow;
        foreach (var desk in settings.Site.Desks)
            await SendAsync(measure, SessionsTopic, desk.Code, new
            {
                siteCode = settings.Site.Site, deskCode = desk.Code, state = "Opened", laneCategory = desk.Lane, occurredAtUtc = now.AddSeconds(-30), sourceEventId = Id("ds")
            }, ct);
        for (var f = 1; f <= 3; f++)
        {
            var flight = LoadSettings.Invariant($"LD{f}{_run}");
            await SendAsync(measure, LaneDemandTopic, flight, new
            {
                siteCode = settings.Site.Site, flightKey = flight, scheduledArrivalUtc = Minute(now).AddMinutes(20 * f), boardedTotal = 180,
                passengersByLane = new Dictionary<string, int> { ["CIT"] = 40, ["RES"] = 30, ["VIS"] = 50, ["CRW"] = 10 }, eGateEligible = 50, computedAtUtc = now,
                sourceEventId = Id("ld")
            }, ct);
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // A few seconds after the next minute ends, as AMAN closes a minute.
                var next = Minute(DateTimeOffset.UtcNow).AddMinutes(1).AddSeconds(3);
                await Task.Delay(next - DateTimeOffset.UtcNow, ct);
                var minute = Minute(next).AddMinutes(-1);
                var k = (int)(minute.ToUnixTimeSeconds() / 60 % 4);
                foreach (var desk in settings.Site.Desks)
                    await SendAsync(measure, DeskIntervalsTopic, desk.Code, new
                    {
                        siteCode = settings.Site.Site, deskCode = desk.Code, intervalStartUtc = minute, intervalSeconds = 60, transactionsProcessed = 1 + (k % 2),
                        documentsProcessed = 2 + (k % 2), meanServiceSeconds = 38.0 + k, p90ServiceSeconds = 61.0 + k, meanCycleSeconds = 30.0 + k, laneCategory = desk.Lane,
                        sourceEventId = Id("dk")
                    }, ct);
                foreach (var gate in settings.Site.Gates)
                    await SendAsync(measure, GateIntervalsTopic, gate, new
                    {
                        siteCode = settings.Site.Site, gateCode = gate, intervalStartUtc = minute, intervalSeconds = 60, attempts = 6 + k, accepted = 2 + k, rejected = 4,
                        rejectsByCategory = new Dictionary<string, int> { ["DocumentRead"] = 3, ["Other"] = 1 }, meanCycleSeconds = 14.0 + k, sourceEventId = Id("eg")
                    }, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }

        _producer.Flush(TimeSpan.FromSeconds(10));
        measure.Stop();
        return measure;
    }

    private string Id(string kind) => LoadSettings.Invariant($"load-{kind}-{_run}-{Interlocked.Increment(ref _sequence)}");

    private static DateTimeOffset Minute(DateTimeOffset at) => new(at.Year, at.Month, at.Day, at.Hour, at.Minute, 0, TimeSpan.Zero);

    private async Task SendAsync(Measure measure, string topic, string key, object record, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            await _producer.ProduceAsync(topic, new Message<string, string> { Key = key, Value = JsonSerializer.Serialize(record, Json) }, ct);
            measure.Record(clock.Elapsed.TotalMilliseconds, "produced");
        }
        catch (ProduceException<string, string> e)
        {
            // The error code only: the record is the run's own, but the message may quote the broker's address.
            measure.Record(clock.Elapsed.TotalMilliseconds, "error " + e.Error.Code);
        }
    }

    public void Dispose() => _producer.Dispose();
}
