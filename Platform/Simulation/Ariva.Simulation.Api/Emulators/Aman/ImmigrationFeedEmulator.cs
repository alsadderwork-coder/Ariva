using System.Globalization;
using System.Net.Http.Json;
using Ariva.Simulation.Api.Emulators.Integration;
using Ariva.Simulation.Api.Scenarios;

namespace Ariva.Simulation.Api.Emulators.Aman;

/// <summary>One transport of an emulated immigration system: records sent and refused, the last status and error.</summary>
public sealed record FeedTransportStatus(string Transport, bool Configured, long Sent, long Failures, int? LastStatus, string LastError);

/// <summary>An emulated immigration system: what it reports, the last minute it played and its transports.</summary>
public sealed record ImmigrationFeedStatus(string Name, string SiteCode, BorderSides Sides, int? LastMinute, long Records, IReadOnlyList<FeedTransportStatus> Transports);

/// <summary>
/// An emulated border system playing the demo day (ARV-029): AMAN (Kafka, REST with its own client, and its mock
/// Integration API's buffer) or a non-AMAN immigration system (REST only). Each demo minute, <see cref="AmanFeed"/>
/// gives the minute's records for the sides it covers; Kafka gets every record on its topic; the REST transport sends
/// each contract as a batch to Ariva's immigration endpoints
/// (<c>api/v1/integration/sites/{site}/immigration/{contract}</c>, <c>{ "items": [...] }</c>, at most 500 items, an
/// <c>Idempotency-Key</c> of the seed, date, minute and contract so a replayed minute is a replay for Ariva).
/// Failures are counted and logged, never fatal.
/// </summary>
public sealed class ImmigrationFeedEmulator : IDemoMinuteSink
{
    private const int MaxBatch = 500;
    private readonly ScenarioEngine _engine;
    private readonly FeedTime _time;
    private readonly Func<(string SiteCode, BorderSides Sides, int RetainMinutes)> _settings;
    private readonly Func<string> _arivaSite;
    private readonly ArivaIntegrationClient _rest;
    private readonly AmanKafka _kafka;
    private readonly AmanFeedBuffer _buffer;
    private readonly ILogger _logger;
    private readonly Counter _restCounter = new();
    private readonly Counter _kafkaCounter = new();
    private readonly SemaphoreSlim _play = new(1, 1);
    private int? _lastMinute;
    private long _records;

    private sealed class Counter
    {
        public long Sent;
        public long Failures;
        public int? LastStatus;
        public string LastError;
    }

    public ImmigrationFeedEmulator(string name, ScenarioEngine engine, FeedTime time, Func<(string SiteCode, BorderSides Sides, int RetainMinutes)> settings,
        Func<string> arivaSite, ArivaIntegrationClient rest, AmanKafka kafka, AmanFeedBuffer buffer, ILogger logger)
    {
        Name = name;
        _engine = engine;
        _time = time;
        _settings = settings;
        _arivaSite = arivaSite;
        _rest = rest;
        _kafka = kafka;
        _buffer = buffer;
        _logger = logger;
    }

    public string Name { get; }

    public ImmigrationFeedStatus Status()
    {
        var settings = _settings();
        var transports = new List<FeedTransportStatus> { Of("rest", _rest.IsConfigured, _restCounter) };
        if (_kafka is not null)
            transports.Add(Of("kafka", _kafka.IsConfigured, _kafkaCounter));
        return new ImmigrationFeedStatus(Name, settings.SiteCode, settings.Sides, _lastMinute, Interlocked.Read(ref _records), transports);

        static FeedTransportStatus Of(string name, bool configured, Counter c) =>
            new(name, configured, Interlocked.Read(ref c.Sent), Interlocked.Read(ref c.Failures), c.LastStatus, c.LastError);
    }

    /// <summary>Plays a minute; one at a time (the demo clock's pump and a manual play may meet).</summary>
    public async Task PlayAsync(int minute, Func<double, DateTime> wallOf, CancellationToken ct)
    {
        await _play.WaitAsync(ct);
        try
        {
            await PlayOneAsync(minute, wallOf, ct);
        }
        finally
        {
            _play.Release();
        }
    }

    private async Task PlayOneAsync(int minute, Func<double, DateTime> wallOf, CancellationToken ct)
    {
        var settings = _settings();
        var firstOfRun = _lastMinute is not { } last || minute != last + 1;
        var at = _time.Observe(minute, wallOf);
        var dayStart = at.AddMinutes(-minute);
        var day = _engine.CurrentDay;
        var records = _engine.Read(d => AmanFeed.Build(d, minute, m => dayStart.AddMinutes(m), settings.SiteCode, settings.Sides, firstOfRun,
            Name == "aman" ? "aman" : "imm"));
        _lastMinute = minute;
        Interlocked.Add(ref _records, records.Count);
        _buffer?.Add(records, settings.RetainMinutes);
        if (records.Count == 0)
            return;

        var tag = $"sim-{day.Seed.ToString(CultureInfo.InvariantCulture)}-{dayStart:yyyyMMdd}-{minute.ToString("D4", CultureInfo.InvariantCulture)}";
        await Task.WhenAll(KafkaAsync(records, ct), RestAsync(records, tag, ct));
    }

    private async Task KafkaAsync(AmanMinute records, CancellationToken ct)
    {
        if (_kafka?.IsConfigured != true)
            return;
        var (delivered, error) = await _kafka.PublishAsync(records, ct);
        Interlocked.Add(ref _kafkaCounter.Sent, delivered);
        if (error is not null)
        {
            Interlocked.Add(ref _kafkaCounter.Failures, records.Count - delivered);
            _kafkaCounter.LastError = error;
        }
        else
        {
            _kafkaCounter.LastError = null;
        }
    }

    private async Task RestAsync(AmanMinute records, string tag, CancellationToken ct)
    {
        if (!_rest.IsConfigured)
            return;
        var site = _arivaSite();
        foreach (var (kind, items) in AmanContracts.Of(records))
        {
            for (var start = 0; start < items.Count; start += MaxBatch)
            {
                var batch = items.Skip(start).Take(MaxBatch).Select(i => i.Record).ToList();
                var key = start == 0 ? $"{tag}-{kind}" : $"{tag}-{kind}-{(start / MaxBatch).ToString(CultureInfo.InvariantCulture)}";
                var result = await _rest.SendAsync(HttpMethod.Post, $"api/v1/integration/sites/{site}/immigration/{kind}",
                    JsonContent.Create(new { items = batch }, options: AmanContracts.Json), key, ct);
                _restCounter.LastStatus = result.Status;
                if (result.Succeeded)
                {
                    Interlocked.Add(ref _restCounter.Sent, batch.Count);
                    _restCounter.LastError = null;
                }
                else
                {
                    Interlocked.Add(ref _restCounter.Failures, batch.Count);
                    _restCounter.LastError = result.Error;
                    _logger.LogWarning("{Emulator}: Ariva did not take a {Contract} batch ({Error})", Name, kind, result.Error);
                }
            }
        }
    }
}
