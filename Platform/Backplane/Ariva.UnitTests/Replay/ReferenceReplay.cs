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

    private static readonly Lazy<(List<SensingBatch> Batches, List<DeviceHealthReported> Health)> Published = new(() => Ingest(ScenarioConfig.Reference() with { Lite = true }, Devices));

    /// <summary>
    /// The overflow scenario case (ARV-115, <see cref="ScenarioConfig.OverflowEvening"/>): the same evening with the Visitors
    /// snake holding 60 people, played by S-15 and S-17 (A-VIS) and S-25, the lead sensor of the A-VIS overflow band A-OV.
    /// </summary>
    public static readonly IReadOnlyList<string> OverflowZones = ["A-VIS"];

    private static readonly (string Sensor, string Zone, Guid Id)[] OverflowDevices =
    [
        ("S-15", "A-VIS", Guid.Parse("0199a000-0000-7000-8000-000000000015")),
        ("S-17", "A-VIS", Guid.Parse("0199a000-0000-7000-8000-000000000017")),
        ("S-25", "A-VIS", Guid.Parse("0199a000-0000-7000-8000-000000000025"))
    ];

    private static readonly Lazy<(List<SensingBatch> Batches, List<DeviceHealthReported> Health)> OverflowPublished =
        new(() => Ingest(ScenarioConfig.OverflowEvening() with { Lite = true }, OverflowDevices));

    /// <summary>
    /// The desk zone case (ARV-116): the reference evening (seed 9303, scripted events unchanged) with the Visitors hall's
    /// S-18 to S-20 reporting the staff and service zones of desks AR-08 to AR-22 (five each), next to S-15 and S-17, under
    /// a profile whose A-VIS has those zones, each naming its desk.
    /// </summary>
    private static readonly (string Sensor, string Zone, Guid Id)[] DeskDevices =
    [
        ("S-15", "A-VIS", Guid.Parse("0199a000-0000-7000-8000-000000000015")),
        ("S-17", "A-VIS", Guid.Parse("0199a000-0000-7000-8000-000000000017")),
        ("S-18", "A-VIS", Guid.Parse("0199a000-0000-7000-8000-000000000018")),
        ("S-19", "A-VIS", Guid.Parse("0199a000-0000-7000-8000-000000000019")),
        ("S-20", "A-VIS", Guid.Parse("0199a000-0000-7000-8000-000000000020"))
    ];

    private static readonly Lazy<(List<SensingBatch> Batches, List<DeviceHealthReported> Health)> DeskPublished =
        new(() => Ingest(ScenarioConfig.Reference() with { Lite = true }, DeskDevices, deskZones: true));

    /// <summary>What Ingest published for the desk zone case, in push order.</summary>
    public static (IReadOnlyList<SensingBatch> Batches, IReadOnlyList<DeviceHealthReported> Health) DeskIngested => DeskPublished.Value;

    /// <summary>The Visitors desks with staff and service zones in the desk zone case (AR-08 to AR-22).</summary>
    public static IReadOnlyList<string> VisitorDesks => ScenarioModel.Queues[ScenarioModel.Q("A-VIS")].Servers;

    /// <summary>A desk's key as the stream and the desk feed build it (arrival immigration is checkpoint IMM in the demo).</summary>
    public static string DeskKeyOf(string desk) => Ariva.Core.Desks.DeskKeys.For(Site, "IMM", desk);

    /// <summary>The Visitors zone of the desk zone case: <see cref="GeometryOf"/> with its desks' staff and service zones.</summary>
    public static QueueZoneGeometry DeskGeometryOf(string zone) => GeometryOf(zone) with
    {
        DeskZones = VisitorDesks.SelectMany(d => new[]
            {
                (Zone: SensorTraffic.StaffZoneOf(d), Link: new Ariva.Core.Desks.DeskZoneLink(DeskKeyOf(d), Ariva.Core.Desks.DeskSource.StaffZone)),
                (Zone: SensorTraffic.ServiceZoneOf(d), Link: new Ariva.Core.Desks.DeskZoneLink(DeskKeyOf(d), Ariva.Core.Desks.DeskSource.ServiceZone))
            })
            .ToDictionary(z => z.Zone, z => z.Link, StringComparer.Ordinal)
    };

    /// <summary>What Ingest published for the overflow scenario case, in push order.</summary>
    public static (IReadOnlyList<SensingBatch> Batches, IReadOnlyList<DeviceHealthReported> Health) OverflowIngested => OverflowPublished.Value;

    public static DateTime WallOf(double minute) => Midnight.AddMinutes(minute);

    /// <summary>
    /// The zone as the demo seed's profile version 12 has it: entry and exit lines, an overflow band with its entry line,
    /// and the queue zone's physical capacity (ARV-114a: A-VIS 190, CI-C 70, the scenario's snake capacities; bands none).
    /// </summary>
    public static QueueZoneGeometry GeometryOf(string zone)
    {
        var band = BandOf(zone);
        return new QueueZoneGeometry(zone,
            new HashSet<string>(StringComparer.Ordinal) { zone + " entry" }, new HashSet<string>(StringComparer.Ordinal) { zone + " exit" },
            new HashSet<string>(StringComparer.Ordinal) { band + " entry" }, new HashSet<string>(StringComparer.Ordinal) { band })
        {
            Capacities = new Dictionary<string, int>(StringComparer.Ordinal) { [zone] = Ariva.Infra.Services.Seed.DemoTopologySeed.CapacityOf(zone)!.Value }
        };
    }

    /// <summary>The overflow band of a zone in the demo profile (A-OV for A-VIS; a band named after the zone otherwise).</summary>
    public static string BandOf(string zone) => zone == "A-VIS" ? "A-OV" : zone + "-OV";

    /// <summary>What Ingest published for the evening: the sensing batches and the health reports, in push order.</summary>
    public static (IReadOnlyList<SensingBatch> Batches, IReadOnlyList<DeviceHealthReported> Health) Ingested => Published.Value;

    private sealed class Gateway(bool deskZones = false) : ISvcDeviceGateway
    {
        public Task<DeviceCredentialRecord> FindByPrefixAsync(string prefix, CancellationToken ct = default) => Task.FromResult<DeviceCredentialRecord>(null);

        public Task<Fluentx.Result<DeviceZoneViewModel>> PublishedZoneAsync(string siteCode, string queueZoneName, CancellationToken ct = default) =>
            Task.FromResult(new Fluentx.Result<DeviceZoneViewModel>(new DeviceZoneViewModel(siteCode, queueZoneName, ProfileVersion, new string('b', 64),
                [
                    new(Guid.NewGuid(), queueZoneName, "Queue", Guid.NewGuid(), null, null, "10 10,34 10,34 22,10 22", 288),
                    // ARV-115: the zone's overflow band, so that its lead sensor's readings pass Ingest's name check.
                    new(Guid.NewGuid(), BandOf(queueZoneName), "Overflow", Guid.NewGuid(), null, null, "6 10,10 10,10 22,6 22", 48),
                    // ARV-116: the Visitors desks' staff and service zones, each naming its desk.
                    .. deskZones && queueZoneName == "A-VIS"
                        ? VisitorDesks.SelectMany(d => new ZoneViewModel[]
                        {
                            new(Guid.NewGuid(), SensorTraffic.StaffZoneOf(d), "Staff", Guid.NewGuid(), null, Guid.NewGuid(), "34 10,35 10,35 11,34 11", 1),
                            new(Guid.NewGuid(), SensorTraffic.ServiceZoneOf(d), "Service", Guid.NewGuid(), null, Guid.NewGuid(), "35 10,36 10,36 11,35 11", 1)
                        })
                        : Enumerable.Empty<ZoneViewModel>()
                ],
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

    private static (List<SensingBatch>, List<DeviceHealthReported>) Ingest(ScenarioConfig config, (string Sensor, string Zone, Guid Id)[] devices, bool deskZones = false)
    {
        var day = ScenarioDay.Run(config);
        var sink = new Sink();
        var cache = new FusionCache(new FusionCacheOptions(), new MemoryCache(new MemoryCacheOptions()));
        var ingest = new SensingIngest(new Gateway(deskZones), sink, new DeviceClockStore(), cache, Options.Create(new IngestSettings { MaxEventsPerMessage = 3_000 }),
            Ariva.Infra.Sensing.Declarative.DeclarativeMappingCatalog.Embedded);
        var package = 0L;
        for (var minute = 1020; minute < 1230; minute++)
        {
            foreach (var (sensor, zone, id) in devices)
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

    /// <summary>
    /// A zone's inputs in the stream's order, from the evening as the archive holds it and as the replay command reads it
    /// back: sensing rows whose event time and health reports whose receive time lie in [From, To). (ARV-114a: the
    /// occupancy reading stamped exactly 20:30:00 is outside the range; it changed no output before the health checks
    /// counted occupancy minutes, so the in-memory and the archived replay now read the same records.)
    /// </summary>
    public static IReadOnlyList<ReplayInput> InputsOf(string zone, (IReadOnlyList<SensingBatch> Batches, IReadOnlyList<DeviceHealthReported> Health)? source = null)
    {
        var (batches, health) = source ?? Ingested;
        return ReplayInput.InOrder(
            ArchivedBatch.Group(batches.Where(b => b.QueueZoneName == zone).SelectMany(Rows).Where(r => r.TimeUtc >= From && r.TimeUtc < To)),
            health.Where(h => h.QueueZoneName == zone && h.ReceivedUtc >= From && h.ReceivedUtc < To).Select(Archived));
    }

    public static ReplayManifest Manifest(ZoneProcessorSettings settings, IReadOnlyList<string> zones = null) =>
        new(ReplayManifest.CurrentFormat, Site, zones ?? Zones, From, To, ProfileVersion, "test", ZoneReplay.SettingsHash(settings));

    /// <summary>Replays both zones into a ledger (and an export when given); returns the hashes and every output by zone.</summary>
    public static (ReplayHashes Hashes, Dictionary<string, List<ZoneOutputs>> Outputs) Run(TextWriter export = null, ZoneProcessorSettings settings = null,
        (IReadOnlyList<SensingBatch> Batches, IReadOnlyList<DeviceHealthReported> Health)? source = null, IReadOnlyList<string> zones = null)
    {
        settings ??= new ZoneProcessorSettings();
        zones ??= Zones;
        var outputs = new Dictionary<string, List<ZoneOutputs>>(StringComparer.Ordinal);
        var ledger = new CapturingLedger(Manifest(settings, zones), export, outputs);
        foreach (var zone in zones)
            ZoneReplay.Run(ledger.Ledger, ZoneKeys.For(Site, zone), GeometryOf(zone), ProfileVersion, settings, InputsOf(zone, source ?? Ingested), From, To);
        return (ledger.Ledger.End(), outputs);
    }

    /// <summary>The evening ingested again from scratch (health reports get new ids).</summary>
    public static (IReadOnlyList<SensingBatch> Batches, IReadOnlyList<DeviceHealthReported> Health) IngestAgain() => Ingest(ScenarioConfig.Reference() with { Lite = true }, Devices);

    /// <summary>
    /// Replays the desk zone case (ARV-116): the Visitors zone with S-18 to S-20 reporting its desks' zones, under the
    /// geometry with those zones (<paramref name="deskGeometry"/>) or without them (the readings then go nowhere).
    /// </summary>
    public static (ReplayHashes Hashes, Dictionary<string, List<ZoneOutputs>> Outputs) RunDesks(bool deskGeometry = true, TextWriter export = null)
    {
        var settings = new ZoneProcessorSettings();
        var outputs = new Dictionary<string, List<ZoneOutputs>>(StringComparer.Ordinal);
        var ledger = new CapturingLedger(Manifest(settings, OverflowZones), export, outputs);
        ZoneReplay.Run(ledger.Ledger, ZoneKeys.For(Site, "A-VIS"), deskGeometry ? DeskGeometryOf("A-VIS") : GeometryOf("A-VIS"), ProfileVersion, settings,
            InputsOf("A-VIS", DeskIngested), From, To);
        return (ledger.Ledger.End(), outputs);
    }

    /// <summary>Replays the overflow scenario case (ARV-115): the Visitors zone with its band reporting.</summary>
    public static (ReplayHashes Hashes, Dictionary<string, List<ZoneOutputs>> Outputs) RunOverflow(TextWriter export = null, ZoneProcessorSettings settings = null) =>
        Run(export, settings, OverflowIngested, OverflowZones);

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
                outputs[zone] = list = [new ZoneOutputs(zone, new List<MinuteResult>(), new List<BinResult>(), new List<QueueLiveMinute>(), new List<RecomputationRequest>(), new List<DeviceOutage>(), new List<LineMinute>(), new List<ZoneHealthBin>(), new List<OverflowMinute>(), new List<OverflowChange>(), new List<Ariva.Core.Desks.DeskZoneSample>())];
            var o = list[0];
            switch (root.GetProperty("type").GetString())
            {
                case "minute": ((List<MinuteResult>)o.Minutes).Add(Read<MinuteResult>()); break;
                case "bin": ((List<BinResult>)o.Bins).Add(Read<BinResult>()); break;
                case "live": ((List<QueueLiveMinute>)o.Live).Add(Read<QueueLiveMinute>()); break;
                case "recomputation": ((List<RecomputationRequest>)o.Recomputations).Add(Read<RecomputationRequest>()); break;
                case "outage": ((List<DeviceOutage>)o.Outages).Add(Read<DeviceOutage>()); break;
                case "line": ((List<LineMinute>)o.Lines).Add(Read<LineMinute>()); break;
                case "health": ((List<ZoneHealthBin>)o.Health).Add(Read<ZoneHealthBin>()); break;
                case "overflow": ((List<OverflowMinute>)o.Overflow).Add(Read<OverflowMinute>()); break;
                case "overflow-change": ((List<OverflowChange>)o.OverflowChanges).Add(Read<OverflowChange>()); break;
                case "desk-reading": ((List<Ariva.Core.Desks.DeskZoneSample>)o.DeskReadings).Add(Read<Ariva.Core.Desks.DeskZoneSample>()); break;
            }
        }

        public override void Flush() => inner?.Flush();
    }
}
