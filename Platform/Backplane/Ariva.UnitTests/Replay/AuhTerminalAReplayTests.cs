using System.Text;
using Ariva.Core.Domain.Contracts;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Queueing;
using Ariva.Core.Queueing.Replay;
using Ariva.Core.Sensing;
using Ariva.Core.Services.Sensing;
using Ariva.Infra.Sensing;
using Ariva.Infra.Services.Seed;
using Ariva.Simulation.Api.Emulators.Sensors;
using Ariva.Simulation.Api.Scenarios.Engine;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using ZiggyCreatures.Caching.Fusion;

namespace Ariva.UnitTests.Replay;

/// <summary>
/// ARV-139b: the AUH-TA evening (seed 9304) through Ariva's own pipeline, as the demo plays it: the sensor emulator's
/// pushes of the seeded AUH-TA sensors through Ingest (mappers, clock estimates, health reports) and the stream's zone
/// engine, replayed from the archive's form. Ariva itself sees the three scripted events: the arrivals Visitors nowcast
/// passes 15 minutes at 18:12, the minute the scenario's own nowcast (which divides by the staffed counters' rate) does;
/// Q-RES-04 is out from 18:25 until it is heard again at 18:36 and the residents' zone is degraded meanwhile; the smart
/// gates' band A-EG-OV is occupied from 19:13 to 19:26 (the scenario's queue is above its snake at the end of 19:12 to
/// 19:24). The same records give the same output hash, and so do the same pushes from devices with other ids.
/// </summary>
public sealed class AuhTerminalAReplayTests
{
    private const string Site = "AUH-TA";
    private const int ProfileVersion = 1;
    private static readonly DateTime Midnight = new(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime From = WallOf(1020);
    private static readonly DateTime To = WallOf(1200);

    /// <summary>
    /// The devices played: each zone's lead, its band's lead where it has one, and a heartbeat sensor (Q-RES-04 for the
    /// outage). Fixed ids, as the reference replay's, so the archive (whose batch ids derive from the device id) is the
    /// same on every run.
    /// </summary>
    private static readonly (string Sensor, string Zone, Guid Id)[] Devices =
    [
        ("Q-VIS-01", "A-VIS", Guid.Parse("0199a000-0000-7000-8000-0000000a0101")), ("O-VIS-01", "A-VIS", Guid.Parse("0199a000-0000-7000-8000-0000000a0102")),
        ("Q-VIS-02", "A-VIS", Guid.Parse("0199a000-0000-7000-8000-0000000a0103")),
        ("Q-RES-01", "A-RES", Guid.Parse("0199a000-0000-7000-8000-0000000a0201")), ("Q-RES-04", "A-RES", Guid.Parse("0199a000-0000-7000-8000-0000000a0204")),
        ("Q-EG-01", "A-EG", Guid.Parse("0199a000-0000-7000-8000-0000000a0301")), ("O-EG-01", "A-EG", Guid.Parse("0199a000-0000-7000-8000-0000000a0302"))
    ];

    private static readonly IReadOnlyList<string> Zones = ["A-VIS", "A-RES", "A-EG"];

    private static readonly Lazy<(List<SensingBatch> Batches, List<DeviceHealthReported> Health)> Ingested = new(() => Ingest(Devices));

    private static readonly Lazy<(ReplayHashes Hashes, Dictionary<string, ZoneOutputs> Outputs)> Replayed = new(() => Run(Ingested.Value));

    private static DateTime WallOf(double minute) => Midnight.AddMinutes(minute);

    private static string Clock(DateTime t) => t.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    private static AuhTerminalALayout.Lane LaneOf(string zone) => AuhTerminalALayout.Lanes.Single(l => AuhTerminalALayout.QueueName(l.Code) == zone);

    private static string BandOf(string zone) => LaneOf(zone).HasOverflow ? AuhTerminalALayout.OverflowName(LaneOf(zone).Code) : null;

    /// <summary>The zone as the seed's profile version 1 has it: entry and exit lines, its band and the band's entry line, the area capacity.</summary>
    private static QueueZoneGeometry GeometryOf(string zone)
    {
        var band = BandOf(zone);
        var bands = new HashSet<string>(StringComparer.Ordinal);
        var bandLines = new HashSet<string>(StringComparer.Ordinal);
        if (band is not null)
        {
            bands.Add(band);
            bandLines.Add(band + " entry");
        }

        return new QueueZoneGeometry(zone, new HashSet<string>(StringComparer.Ordinal) { zone + " entry" }, new HashSet<string>(StringComparer.Ordinal) { zone + " exit" },
            bandLines, bands)
        {
            Capacities = new Dictionary<string, int>(StringComparer.Ordinal) { [zone] = AuhTerminalALayout.CapacityOf(AuhTerminalALayout.QueueRect(LaneOf(zone))) }
        };
    }

    private sealed class Gateway : ISvcDeviceGateway
    {
        public Task<DeviceCredentialRecord> FindByPrefixAsync(string prefix, CancellationToken ct = default) => Task.FromResult<DeviceCredentialRecord>(null);

        public Task<Fluentx.Result<DeviceZoneViewModel>> PublishedZoneAsync(string siteCode, string queueZoneName, CancellationToken ct = default)
        {
            var band = BandOf(queueZoneName);
            List<ZoneViewModel> zones = [new(Guid.NewGuid(), queueZoneName, "Queue", Guid.NewGuid(), null, null, "84 54,112 54,112 60,84 60", 100)];
            if (band is not null)
                zones.Add(new(Guid.NewGuid(), band, "Overflow", Guid.NewGuid(), null, null, "64 54,82 54,82 60,64 60", 100));
            return Task.FromResult(new Fluentx.Result<DeviceZoneViewModel>(new DeviceZoneViewModel(siteCode, queueZoneName, ProfileVersion, new string('c', 64), zones,
            [
                new(Guid.NewGuid(), queueZoneName + " entry", "Entry", null, Guid.NewGuid(), 84, 54, 84, 60, 6),
                new(Guid.NewGuid(), queueZoneName + " exit", "Exit", null, Guid.NewGuid(), 112, 54, 112, 60, 6)
            ])));
        }
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

    private static (List<SensingBatch>, List<DeviceHealthReported>) Ingest((string Sensor, string Zone, Guid Id)[] devices)
    {
        var day = ScenarioDay.Run(ScenarioConfig.AuhTerminalA() with { Lite = true });
        var sink = new Sink();
        var cache = new FusionCache(new FusionCacheOptions(), new MemoryCache(new MemoryCacheOptions()));
        var ingest = new SensingIngest(new Gateway(), sink, new DeviceClockStore(), cache, Options.Create(new IngestSettings { MaxEventsPerMessage = 3_000 }),
            Ariva.Infra.Sensing.Declarative.DeclarativeMappingCatalog.Embedded);
        var ids = devices.ToDictionary(d => d.Sensor, d => d.Id, StringComparer.Ordinal);
        var package = 0L;
        for (var minute = 1020; minute < 1200; minute++)
        {
            foreach (var (sensor, zone, _) in devices)
            {
                var received = WallOf(minute + 1);
                var push = SensorTraffic.Build(day, SensorTraffic.Sensor(Site, sensor), EmulatedDialect.Canonical, minute, ++package, WallOf, received);
                if (push is null)
                    continue;
                var device = new DeviceCredentialRecord(ids[sensor], sensor, Site, zone, "Online", new string('a', 64), [], null);
                var result = ingest.IngestAsync(device, DeviceDialect.Canonical, Encoding.UTF8.GetBytes(push.Json), received, CancellationToken.None).GetAwaiter().GetResult();
                if (result.HasErrors || result.Data.Rejected > 0)
                    throw new InvalidOperationException($"{sensor} at minute {minute}: {string.Join(", ", result.ErrorMessages ?? [])}");
            }
        }

        return ([.. sink.Published.OfType<SensingBatch>()], [.. sink.Published.OfType<DeviceHealthReported>()]);
    }

    private static (ReplayHashes Hashes, Dictionary<string, ZoneOutputs> Outputs) Run((List<SensingBatch> Batches, List<DeviceHealthReported> Health) source)
    {
        var settings = new ZoneProcessorSettings();
        var (batches, health) = source;
        var outputs = new Dictionary<string, ZoneOutputs>(StringComparer.Ordinal);
        var ledger = new ReplayLedger(new ReplayManifest(ReplayManifest.CurrentFormat, Site, Zones, From, To, ProfileVersion, "test", ZoneReplay.SettingsHash(settings)));
        foreach (var zone in Zones)
        {
            var inputs = ReplayInput.InOrder(
                ArchivedBatch.Group(batches.Where(b => b.QueueZoneName == zone).SelectMany(ReferenceReplay.Rows).Where(r => r.TimeUtc >= From && r.TimeUtc < To)),
                health.Where(h => h.QueueZoneName == zone && h.ReceivedUtc >= From && h.ReceivedUtc < To).Select(ReferenceReplay.Archived));
            var processor = new ZoneProcessor(ZoneKeys.For(Site, zone), GeometryOf(zone), ProfileVersion, settings);
            foreach (var input in inputs)
                processor.Offer(input.ToBatch(), To);
            processor.Finish(To, To + ZoneReplay.Settle(settings));
            outputs[zone] = processor.Drain();
            ZoneReplay.Run(ledger, ZoneKeys.For(Site, zone), GeometryOf(zone), ProfileVersion, settings, inputs, From, To);
        }

        return (ledger.End(), outputs);
    }

    [Fact]
    public void Replay_Should_PassFifteenMinutesAt1812_When_TheAuhVisitorWaveArrives()
    {
        var live = Replayed.Value.Outputs["A-VIS"].Live;

        var first = live.First(l => l.NowcastMinutes > 15);

        Clock(first.MinuteUtc).Should().Be("18:12", "Ariva's nowcast from the exits breaches in the minute the scenario's own nowcast does");
        first.LengthMeasured.Should().BeTrue("the queue zone and its band both report");
        live.Where(l => l.MinuteUtc >= WallOf(1092) && l.MinuteUtc < WallOf(1112)).Should().OnlyContain(l => l.NowcastMinutes > 15,
            "from the scenario's breach at 18:12 the wait stays above target for twenty minutes");
    }

    [Fact]
    public void Replay_Should_DegradeTheResidentsZone_When_QRes04IsOfflineFrom1825To1835()
    {
        var residents = Replayed.Value.Outputs["A-RES"];

        residents.Outages.Should().ContainSingle().Which.Should().Be(new DeviceOutage("AUH-TA/A-RES", "Q-RES-04", WallOf(1105), WallOf(1116)),
            "Q-RES-04 last reported for 18:24 at 18:25 and next for 18:35 at 18:36");
        var degraded = residents.Live.Where(l => l.LengthDegraded).Select(l => Clock(l.MinuteUtc)).ToList();
        degraded.Should().NotBeEmpty().And.OnlyContain(m => string.CompareOrdinal(m, "18:25") >= 0 && string.CompareOrdinal(m, "18:35") <= 0);
        Replayed.Value.Outputs["A-VIS"].Outages.Should().BeEmpty();
        Replayed.Value.Outputs["A-EG"].Outages.Should().BeEmpty();
    }

    [Fact]
    public void Replay_Should_OccupyTheSmartGatesBand_When_TheGateFaultMeetsTheHubWave()
    {
        var gates = Replayed.Value.Outputs["A-EG"];

        gates.OverflowChanges.Select(c => (c.Kind, Clock(c.MinuteUtc))).Should().Equal((OverflowChangeKind.Occupied, "19:13"), (OverflowChangeKind.Emptied, "19:26"));
        gates.Overflow.Should().OnlyContain(m => m.BandName == "A-EG-OV");
        Replayed.Value.Outputs["A-VIS"].OverflowChanges.Should().BeEmpty("the visitors' queue stays within its 300-person snake");
    }

    [Fact]
    public void Replay_Should_GiveTheSameOutputHash_When_RunAgain()
    {
        Run(Ingested.Value).Hashes.OutputHead.Should().Be(Replayed.Value.Hashes.OutputHead);
    }

    [Fact]
    public void Replay_Should_GiveTheSameOutputs_When_TheDevicesHaveOtherIds()
    {
        // The zone and band readings of one instant come from two devices, and the replay orders such ties by batch id,
        // which derives from the device id. Ariva's outputs must not depend on that order (they did before ARV-139b: the
        // first breach moved between 18:09 and 18:12 with the ids drawn for the run).
        var others = Devices.Select((d, i) => (d.Sensor, d.Zone, Guid.Parse($"0199a000-0000-7000-8000-{(i * 7919 % 10_007):x12}"))).ToArray();
        var reordered = Ingest(others);
        static List<string> Order((List<SensingBatch> Batches, List<DeviceHealthReported> Health) source) =>
        [
            .. ReplayInput.InOrder(ArchivedBatch.Group(source.Batches.Where(b => b.QueueZoneName == "A-VIS").SelectMany(ReferenceReplay.Rows)),
                source.Health.Where(h => h.QueueZoneName == "A-VIS").Select(ReferenceReplay.Archived)).Select(i => i.Batch?.DeviceCode ?? "health " + i.Health.DeviceCode)
        ];
        Order((reordered.Item1, reordered.Item2)).Should().NotEqual(Order(Ingested.Value), "the other ids break the ties between devices another way");

        var again = Run((reordered.Item1, reordered.Item2));

        again.Hashes.OutputHead.Should().Be(Replayed.Value.Hashes.OutputHead, "the order of a tie between devices never changes an output");
        again.Hashes.Outputs.Should().Be(Replayed.Value.Hashes.Outputs);
    }
}
