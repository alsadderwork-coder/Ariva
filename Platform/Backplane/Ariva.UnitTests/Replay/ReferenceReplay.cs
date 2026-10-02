using System.Text;
using Ariva.Core.Domain.Contracts;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Queueing;
using Ariva.Core.Queueing.Replay;
using Ariva.Core.Sensing;
using Ariva.Core.Services.Sensing;
using Ariva.Infra.Sensing;
using Ariva.Simulation.Api.Emulators.Sensors;
using Ariva.Simulation.Api.Scenarios.Engine;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using ZiggyCreatures.Caching.Fusion;

namespace Ariva.UnitTests.Replay;

/// <summary>
/// The reference evening (seed 9303) as the archive holds it (ARV-036): the sensor emulator's pushes of S-15 and S-17
/// (arrivals Visitors, A-VIS; S-17 only sends heartbeats and is offline 18:20 to 18:30) and S-50 (Handler B's check-in
/// island C, CI-C) from 17:00 to 20:30, through Ingest's own ingest (mappers, clock estimates, health reports), then
/// in the archive's form: the rows of each sensing batch and every health report.
/// </summary>
internal static class ReferenceReplay
{
    public const string Site = "DMO";
    public const int ProfileVersion = 12;
    public static readonly DateTime Midnight = new(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime From = WallOf(1020);
    public static readonly DateTime To = WallOf(1230);
    public static readonly IReadOnlyList<string> Zones = ["A-VIS", "CI-C"];

    private static readonly (string Sensor, string Zone, Guid Id)[] Devices =
    [
        ("S-15", "A-VIS", Guid.Parse("0199a000-0000-7000-8000-000000000015")),
        ("S-17", "A-VIS", Guid.Parse("0199a000-0000-7000-8000-000000000017")),
        ("S-50", "CI-C", Guid.Parse("0199a000-0000-7000-8000-000000000050"))
    ];

    private static readonly Lazy<(List<SensingBatch> Batches, List<DeviceHealthReported> Health)> Published = new(Ingest);

    public static DateTime WallOf(double minute) => Midnight.AddMinutes(minute);

    /// <summary>The zone as the demo seed's profile version 12 has it: entry and exit lines and an overflow band with its entry line.</summary>
    public static QueueZoneGeometry GeometryOf(string zone)
    {
        var band = zone == "A-VIS" ? "A-OV" : zone + "-OV";
        return new QueueZoneGeometry(zone,
            new HashSet<string>(StringComparer.Ordinal) { zone + " entry" }, new HashSet<string>(StringComparer.Ordinal) { zone + " exit" },
            new HashSet<string>(StringComparer.Ordinal) { band + " entry" }, new HashSet<string>(StringComparer.Ordinal) { band });
    }

    /// <summary>What Ingest published for the evening: the sensing batches and the health reports, in push order.</summary>
    public static (IReadOnlyList<SensingBatch> Batches, IReadOnlyList<DeviceHealthReported> Health) Ingested => Published.Value;

    private sealed class Gateway : ISvcDeviceGateway
    {
        public Task<DeviceCredentialRecord> FindByPrefixAsync(string prefix, CancellationToken ct = default) => Task.FromResult<DeviceCredentialRecord>(null);

        public Task<Fluentx.Result<DeviceZoneViewModel>> PublishedZoneAsync(string siteCode, string queueZoneName, CancellationToken ct = default) =>
            Task.FromResult(new Fluentx.Result<DeviceZoneViewModel>(new DeviceZoneViewModel(siteCode, queueZoneName, ProfileVersion, new string('b', 64),
                [new(Guid.NewGuid(), queueZoneName, "Queue", Guid.NewGuid(), null, null, "10 10,34 10,34 22,10 22", 288)],
                [
                    new(Guid.NewGuid(), queueZoneName + " entry", "Entry", null, Guid.NewGuid(), 10, 12, 10, 16, 4),
                    new(Guid.NewGuid(), queueZoneName + " exit", "Exit", null, Guid.NewGuid(), 30, 22, 34, 22, 4)
                ])));
    }

    private sealed class Sink : ISensingSink
    {
        public List<IEvent> Published { get; } = [];

        public Task PublishAsync(IReadOnlyList<IEvent> events, CancellationToken ct = default)
        {
            Published.AddRange(events);
            return Task.CompletedTask;
        }
    }

    private static (List<SensingBatch>, List<DeviceHealthReported>) Ingest()
    {
        var day = ScenarioDay.Run(ScenarioConfig.Reference() with { Lite = true });
        var sink = new Sink();
        var cache = new FusionCache(new FusionCacheOptions(), new MemoryCache(new MemoryCacheOptions()));
        var ingest = new SensingIngest(new Gateway(), sink, new DeviceClockStore(), cache, Options.Create(new IngestSettings { MaxEventsPerMessage = 3_000 }),
            Ariva.Infra.Sensing.Declarative.DeclarativeMappingCatalog.Embedded);
        var package = 0L;
        for (var minute = 1020; minute < 1230; minute++)
        {
            foreach (var (sensor, zone, id) in Devices)
            {
                var received = WallOf(minute + 1);
                var push = SensorTraffic.Build(day, SensorTraffic.Sensor(sensor), EmulatedDialect.Canonical, minute, ++package, WallOf, received);
                if (push is null)
                    continue;
                var device = new DeviceCredentialRecord(id, sensor, Site, zone, "Online", new string('a', 64), [], null);
                var result = ingest.IngestAsync(device, DeviceDialect.Canonical, Encoding.UTF8.GetBytes(push.Json), received, CancellationToken.None).GetAwaiter().GetResult();
                if (result.HasErrors || result.Data.Rejected > 0)
                    throw new InvalidOperationException($"{sensor} at minute {minute}: {string.Join(", ", result.ErrorMessages ?? [])}");
            }
        }

        return ([.. sink.Published.OfType<SensingBatch>()], [.. sink.Published.OfType<DeviceHealthReported>()]);
    }

    /// <summary>A batch as the archive stores it: one row per event, the flags kept, track ids as published.</summary>
    public static IEnumerable<ArchivedSensingEvent> Rows(SensingBatch batch)
    {
        ArchivedSensingEvent Row(int ordinal, SensingKind kind, DateTime time, SensedFlags flags) =>
            new(time, batch.SiteCode, batch.QueueZoneName, kind, batch.Id, ordinal, batch.DeviceId, batch.DeviceCode, batch.ReceivedUtc, flags, batch.Commissioned);
        return batch switch
        {
            TrackSampleBatch t => t.Samples.Select((e, i) => Row(i, SensingKind.Track, e.TimeUtc, e.Flags) with
            {
                TrackId = e.Event.TrackId, X = e.Event.X, Y = e.Event.Y, HeightMetres = e.Event.HeightMetres
            }),
            VendorLineCrossingBatch c => c.Crossings.Select((e, i) => Row(i, SensingKind.Crossing, e.TimeUtc, e.Flags) with
            {
                TrackId = e.Event.TrackId, Name = e.Event.LineName, Direction = e.Event.Direction
            }),
            ZoneOccupancyBatch o => o.Occupancy.Select((e, i) => Row(i, SensingKind.Occupancy, e.TimeUtc, e.Flags) with { Name = e.Event.ZoneName, Count = e.Event.Count }),
            IntervalCountBatch n => n.Intervals.Select((e, i) => Row(i, SensingKind.Interval, e.TimeUtc, e.Flags) with
            {
                Name = e.Event.LineName, In = e.Event.In, Out = e.Event.Out, FromUtc = e.Event.FromUtc
            }),
            _ => []
        };
    }

    public static ArchivedDeviceHealth Archived(DeviceHealthReported r) =>
        new(r.ReceivedUtc, r.SiteCode, r.QueueZoneName, r.DeviceId, r.DeviceCode, r.Id, r.Status?.Online ?? true, r.Commissioned, r.Status?.TimeUtc, r.Clock?.State);

    /// <summary>A zone's inputs in the stream's order, from the evening as the archive holds it.</summary>
    public static IReadOnlyList<ReplayInput> InputsOf(string zone, (IReadOnlyList<SensingBatch> Batches, IReadOnlyList<DeviceHealthReported> Health)? source = null)
    {
        var (batches, health) = source ?? Ingested;
        return ReplayInput.InOrder(
            ArchivedBatch.Group(batches.Where(b => b.QueueZoneName == zone).SelectMany(Rows)),
            health.Where(h => h.QueueZoneName == zone).Select(Archived));
    }

    public static ReplayManifest Manifest(ZoneProcessorSettings settings) =>
        new(ReplayManifest.CurrentFormat, Site, Zones, From, To, ProfileVersion, "test", ZoneReplay.SettingsHash(settings));

    /// <summary>Replays both zones into a ledger (and an export when given); returns the hashes and every output by zone.</summary>
    public static (ReplayHashes Hashes, Dictionary<string, List<ZoneOutputs>> Outputs) Run(TextWriter export = null, ZoneProcessorSettings settings = null,
        (IReadOnlyList<SensingBatch> Batches, IReadOnlyList<DeviceHealthReported> Health)? source = null)
    {
        settings ??= new ZoneProcessorSettings();
        var outputs = new Dictionary<string, List<ZoneOutputs>>(StringComparer.Ordinal);
        var ledger = new CapturingLedger(Manifest(settings), export, outputs);
        foreach (var zone in Zones)
            ZoneReplay.Run(ledger.Ledger, ZoneKeys.For(Site, zone), GeometryOf(zone), ProfileVersion, settings, InputsOf(zone, source ?? Ingested), From, To);
        return (ledger.Ledger.End(), outputs);
    }

    /// <summary>The evening ingested again from scratch (health reports get new ids).</summary>
    public static (IReadOnlyList<SensingBatch> Batches, IReadOnlyList<DeviceHealthReported> Health) IngestAgain() => Ingest();

    /// <summary>Keeps the outputs the replay writes, by zone, by reading the export back.</summary>
    private sealed class CapturingLedger
    {
        public CapturingLedger(ReplayManifest manifest, TextWriter export, Dictionary<string, List<ZoneOutputs>> outputs)
        {
            Ledger = new ReplayLedger(manifest, new Tee(export, outputs));
        }

        public ReplayLedger Ledger { get; }
    }

    /// <summary>Writes the export through and parses each output line back into typed rows for the assertions.</summary>
    private sealed class Tee(TextWriter inner, Dictionary<string, List<ZoneOutputs>> outputs) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(string value)
        {
            inner?.Write(value);
            using var document = System.Text.Json.JsonDocument.Parse(value);
            var root = document.RootElement;
            if (root.GetProperty("kind").GetString() != "out")
                return;
            var zone = root.GetProperty("zone").GetString()!;
            var record = root.GetProperty("record").GetRawText();
            T Read<T>() => System.Text.Json.JsonSerializer.Deserialize<T>(record, ReplayLedger.Json)!;
            if (!outputs.TryGetValue(zone, out var list))
                outputs[zone] = list = [new ZoneOutputs(zone, new List<MinuteResult>(), new List<BinResult>(), new List<QueueLiveMinute>(), new List<RecomputationRequest>(), new List<DeviceOutage>())];
            var o = list[0];
            switch (root.GetProperty("type").GetString())
            {
                case "minute": ((List<MinuteResult>)o.Minutes).Add(Read<MinuteResult>()); break;
                case "bin": ((List<BinResult>)o.Bins).Add(Read<BinResult>()); break;
                case "live": ((List<QueueLiveMinute>)o.Live).Add(Read<QueueLiveMinute>()); break;
                case "recomputation": ((List<RecomputationRequest>)o.Recomputations).Add(Read<RecomputationRequest>()); break;
                case "outage": ((List<DeviceOutage>)o.Outages).Add(Read<DeviceOutage>()); break;
            }
        }

        public override void Flush() => inner?.Flush();
    }
}
