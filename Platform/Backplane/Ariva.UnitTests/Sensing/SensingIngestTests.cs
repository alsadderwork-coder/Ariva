using System.Text;
using Ariva.Core.Domain.Contracts;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Sensing;
using Ariva.Core.Services.Sensing;
using Ariva.Infra.Sensing;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using ZiggyCreatures.Caching.Fusion;

namespace Ariva.UnitTests.Sensing;

/// <summary>
/// ARV-023: the clock estimate (F19 test cases), and the ingest of a push: the device's dialect only, names checked
/// against the published geometry of its zone, bounds, clock correction and skew flags, far-future and too-old events
/// refused, track ids namespaced, batches keyed by zone with ids that repeat for a resent package, and health at most
/// every 10 seconds.
/// </summary>
public sealed class SensingIngestTests
{
    private static readonly DateTime Received = new(2026, 10, 2, 14, 5, 1, DateTimeKind.Utc);

    private static DeviceCredentialRecord Device(string dialect = "Canonical", string state = "Online") =>
        new(Guid.Parse("0199a000-0000-7000-8000-0000000d0017"), "S-17", "DMO", "Snake A", state, new string('a', 64), [], null, dialect, 20, 16, 0);

    private sealed class Gateway(bool published = true) : ISvcDeviceGateway
    {
        public Task<DeviceCredentialRecord> FindByPrefixAsync(string prefix, CancellationToken ct = default) => Task.FromResult<DeviceCredentialRecord>(null);

        public Task<Fluentx.Result<DeviceZoneViewModel>> PublishedZoneAsync(string siteCode, string queueZoneName, CancellationToken ct = default) =>
            Task.FromResult(published
                ? new Fluentx.Result<DeviceZoneViewModel>(new DeviceZoneViewModel(siteCode, queueZoneName, 12, new string('b', 64),
                    [new(Guid.NewGuid(), "Snake A", "Queue", Guid.NewGuid(), null, null, "10 10,34 10,34 22,10 22", 288), new(Guid.NewGuid(), "Overflow A", "Overflow", Guid.NewGuid(), null, null, "10 4,34 4,34 10,10 10", 144)],
                    [new(Guid.NewGuid(), "Entry A", "Entry", null, Guid.NewGuid(), 10, 12, 10, 16, 4), new(Guid.NewGuid(), "Exit A", "Exit", null, Guid.NewGuid(), 30, 22, 34, 22, 4)]))
                : Fluentx.Result.Error<DeviceZoneViewModel>("not found"));
    }

    private sealed class Sink : ISensingSink
    {
        public List<IEvent> Published { get; } = [];
        public bool Fail { get; set; }

        public Task PublishAsync(IReadOnlyList<IEvent> events, CancellationToken ct = default)
        {
            if (Fail)
                throw new InvalidOperationException("broker down");
            Published.AddRange(events);
            return Task.CompletedTask;
        }
    }

    private static (SensingIngest Ingest, Sink Sink, DeviceClockStore Clocks) Create(bool published = true, int max = 2_000)
    {
        var sink = new Sink();
        var clocks = new DeviceClockStore();
        var cache = new FusionCache(new FusionCacheOptions(), new MemoryCache(new MemoryCacheOptions()));
        return (new SensingIngest(new Gateway(published), sink, clocks, cache, Options.Create(new IngestSettings { MaxEventsPerMessage = max }), Ariva.Infra.Sensing.Declarative.DeclarativeMappingCatalog.Embedded), sink, clocks);
    }

    private static ReadOnlyMemory<byte> Json(string json) => Encoding.UTF8.GetBytes(json);

    private static string Push(string sent = "2026-10-02T14:05:00.900Z", string extra = "", long? package = 4711) => $$"""
        { "sentUtc": "{{sent}}" {{(package is null ? "" : $", \"packageId\": {package}")}},
          "tracks": [{ "trackId": "7", "x": 12.5, "y": 8.25, "timeUtc": "2026-10-02T14:05:00.500Z" }],
          "crossings": [{ "lineName": "Entry A", "direction": "In", "trackId": "7", "timeUtc": "2026-10-02T14:05:00.600Z" }],
          "occupancy": [{ "zoneName": "Overflow A", "count": 3, "timeUtc": "2026-10-02T14:05:00Z" }],
          "intervals": [{ "lineName": "Exit A", "in": 0, "out": 5, "fromUtc": "2026-10-02T14:00:00Z", "toUtc": "2026-10-02T14:05:00Z" }] {{extra}} }
        """;

    [Fact]
    public void Clock_Should_FollowTheF19Cases()
    {
        var stable = new[] { 620d, 610, 630 }.Aggregate(ClockOffset.None, (c, ms) => c.Next(Received.AddMilliseconds(ms), Received));
        var unstable = new[] { 100d, 900, 300 }.Aggregate(ClockOffset.None, (c, ms) => c.Next(Received.AddMilliseconds(ms), Received));
        var fine = new[] { 20d, -15, 5, 30 }.Aggregate(ClockOffset.None, (c, ms) => c.Next(Received.AddMilliseconds(ms), Received));

        stable.State.Should().Be(ClockState.Corrected);
        stable.Correct(Received.AddMilliseconds(620)).Should().BeCloseTo(Received, TimeSpan.FromMilliseconds(5));
        unstable.State.Should().Be(ClockState.Unreliable);
        unstable.Correct(Received).Should().Be(Received, "an unreliable clock is not corrected");
        fine.State.Should().Be(ClockState.Ok);
        fine.Recent.Should().HaveCount(4);
        Enumerable.Range(0, 25).Aggregate(ClockOffset.None, (c, _) => c.Next(Received, Received)).Recent.Should().HaveCount(ClockOffset.Window);
    }

    [Fact]
    public async Task Ingest_Should_PublishOneBatchPerKindKeyedByZone()
    {
        var (ingest, sink, _) = Create();

        var result = await ingest.IngestAsync(Device(), DeviceDialect.Canonical, Json(Push()), Received, CancellationToken.None);

        result.HasErrors.Should().BeFalse(string.Join(", ", result.ErrorMessages ?? []));
        result.Data.Accepted.Should().Be(4);
        result.Data.Rejected.Should().Be(0);
        result.Data.Clock.State.Should().Be(ClockState.Ok);
        var batches = sink.Published.OfType<SensingBatch>().ToList();
        batches.Select(b => b.GetType()).Should().BeEquivalentTo([typeof(TrackSampleBatch), typeof(VendorLineCrossingBatch), typeof(ZoneOccupancyBatch), typeof(IntervalCountBatch)]);
        batches.Should().OnlyContain(b => b.GetPartitionKey() == "DMO/Snake A" && b.Commissioned && b.PackageId == 4711 && b.DeviceCode == "S-17");
        sink.Published.OfType<TrackSampleBatch>().Single().Samples.Single().Event.TrackId.Should().Be("S-17/7", "track ids are namespaced by device");
        sink.Published.OfType<VendorLineCrossingBatch>().Single().Crossings.Single().Event.TrackId.Should().Be("S-17/7");
        sink.Published.OfType<DeviceHealthReported>().Should().ContainSingle(h => h.GetPartitionKey() == Device().DeviceId.ToString());
    }

    [Fact]
    public async Task Ingest_Should_GiveAResentPackageTheSameBatchIds()
    {
        var (ingest, sink, _) = Create();

        await ingest.IngestAsync(Device(), DeviceDialect.Canonical, Json(Push()), Received, CancellationToken.None);
        var first = sink.Published.OfType<SensingBatch>().Select(b => b.Id).ToList();
        sink.Published.Clear();
        await ingest.IngestAsync(Device(), DeviceDialect.Canonical, Json(Push()), Received.AddSeconds(30), CancellationToken.None);

        sink.Published.OfType<SensingBatch>().Select(b => b.Id).Should().Equal(first);
        first.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Ingest_Should_RefuseAnotherDialectAndMalformedJson()
    {
        var (ingest, sink, _) = Create();

        (await ingest.IngestAsync(Device("Xovis"), DeviceDialect.Canonical, Json(Push()), Received, CancellationToken.None)).ErrorMessages.Single().Should().Contain("Xovis dialect");
        (await ingest.IngestAsync(Device(), DeviceDialect.Canonical, Json("{\"tracks\":[{"), Received, CancellationToken.None)).ErrorMessages.Single().Should().Contain("well-formed JSON");
        (await ingest.IngestAsync(Device(), DeviceDialect.Canonical, Json(new string('[', 40) + new string(']', 40)), Received, CancellationToken.None)).HasErrors.Should().BeTrue("too deep");
        sink.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Ingest_Should_RejectNamesThatAreNotInThePublishedGeometryAndEventsOutOfBounds()
    {
        var (ingest, sink, _) = Create();
        var extra = """
            , "status": { "online": true, "frameRate": -3, "timeUtc": "2026-10-02T14:05:00Z" }
            """;
        var json = Push(extra: extra).Replace("\"Entry A\", \"direction\"", "\"Entry Z\", \"direction\"", StringComparison.Ordinal)
            .Replace("\"x\": 12.5", "\"x\": 99999", StringComparison.Ordinal);

        var result = await ingest.IngestAsync(Device(), DeviceDialect.Canonical, Json(json), Received, CancellationToken.None);

        result.Data.Rejected.Should().Be(3);
        result.Data.Accepted.Should().Be(2);
        result.Data.Problems.Should().Contain(p => p.StartsWith("tracks[0]:", StringComparison.Ordinal) && p.Contains("x coordinate", StringComparison.Ordinal))
            .And.Contain(p => p.StartsWith("crossings[0]:", StringComparison.Ordinal) && p.Contains("Entry Z", StringComparison.Ordinal))
            .And.Contain(p => p.StartsWith("status:", StringComparison.Ordinal));
        sink.Published.OfType<TrackSampleBatch>().Should().BeEmpty();
    }

    [Fact]
    public async Task Ingest_Should_RejectNamedEvents_When_TheZoneIsNotPublished()
    {
        var (ingest, sink, _) = Create(published: false);

        var result = await ingest.IngestAsync(Device(state: "Commissioning"), DeviceDialect.Canonical, Json(Push()), Received, CancellationToken.None);

        result.Data.Accepted.Should().Be(1, "tracks need no names and help commissioning");
        result.Data.Rejected.Should().Be(3);
        sink.Published.OfType<TrackSampleBatch>().Single().Commissioned.Should().BeFalse();
    }

    [Fact]
    public async Task Ingest_Should_CorrectAStableOffsetAndFlagSkewedEvents()
    {
        var (ingest, sink, _) = Create();
        TrackSampleBatch last = null;
        // The device's clock runs 620 ms ahead, steadily: its send times and event times both carry the offset.
        for (var i = 0; i < 4; i++)
        {
            sink.Published.Clear();
            var at = Received.AddSeconds(i);
            var json = $$"""
                { "sentUtc": "{{at.AddMilliseconds(620):O}}", "packageId": {{i}},
                  "tracks": [{ "trackId": "7", "x": 12.5, "y": 8.25, "timeUtc": "{{at.AddMilliseconds(520):O}}" }] }
                """;
            await ingest.IngestAsync(Device(), DeviceDialect.Canonical, Json(json), at, CancellationToken.None);
            last = sink.Published.OfType<TrackSampleBatch>().Single();
        }

        last.Clock.State.Should().Be(ClockState.Corrected);
        var sample = last.Samples.Single();
        sample.Flags.Should().HaveFlag(SensedFlags.Corrected).And.NotHaveFlag(SensedFlags.Skewed);
        sample.TimeUtc.Should().BeCloseTo(Received.AddSeconds(3).AddMilliseconds(-100), TimeSpan.FromMilliseconds(20));
        sample.Event.TimeUtc.Should().Be(Received.AddSeconds(3).AddMilliseconds(520), "the event keeps the device's own time next to the corrected one");

        sink.Published.Clear();
        var skewed = $$"""
            { "tracks": [{ "trackId": "8", "x": 1, "y": 1, "timeUtc": "{{Received.AddSeconds(90):O}}" },
                         { "trackId": "9", "x": 1, "y": 1, "timeUtc": "{{Received.AddMinutes(-30):O}}" },
                         { "trackId": "10", "x": 1, "y": 1, "timeUtc": "{{Received.AddHours(2):O}}" },
                         { "trackId": "11", "x": 1, "y": 1, "timeUtc": "{{Received.AddDays(-4):O}}" }] }
            """;
        var outcome = await ingest.IngestAsync(Device(), DeviceDialect.Canonical, Json(skewed), Received.AddSeconds(4), CancellationToken.None);

        outcome.Data.Flagged.Should().Be(2, "90 s ahead and 30 min late are flagged");
        outcome.Data.Rejected.Should().Be(2, "2 h ahead and 4 days old are refused");
        sink.Published.OfType<TrackSampleBatch>().Single().Samples.Should().OnlyContain(s => s.Flags.HasFlag(SensedFlags.Skewed));
    }

    [Fact]
    public async Task Ingest_Should_ReportHealthAtMostEveryTenSecondsWithoutAStatus()
    {
        var (ingest, sink, _) = Create();
        var tracksOnly = """{ "tracks": [{ "trackId": "7", "x": 1, "y": 1, "timeUtc": "2026-10-02T14:05:00.500Z" }] }""";

        for (var s = 0; s < 25; s += 5)
            await ingest.IngestAsync(Device(), DeviceDialect.Canonical, Json(tracksOnly), Received.AddSeconds(s), CancellationToken.None);

        sink.Published.OfType<DeviceHealthReported>().Should().HaveCount(3, "at 0, 10 and 20 seconds");
    }

    [Fact]
    public async Task Ingest_Should_MapXovisWithTheDevicePose()
    {
        var (ingest, sink, _) = Create();
        var json = File.ReadAllText(Ariva.UnitTests.Setup.RepositoryPaths.Resolve("Platform/Backplane/Ariva.UnitTests/Sensing/Samples/xovis-logics-fw5.json"))
            .Replace("\"Exit A\"", "\"Exit A\"", StringComparison.Ordinal);

        var result = await ingest.IngestAsync(Device("Xovis"), DeviceDialect.Xovis, Json(json), new DateTime(2026, 10, 2, 14, 5, 1, DateTimeKind.Utc), CancellationToken.None);

        result.Data.Accepted.Should().Be(3);
        result.Data.Ignored.Should().Be(1);
        result.Data.Problems.Should().Contain(p => p.Contains("dwell_time", StringComparison.Ordinal));
        sink.Published.OfType<IntervalCountBatch>().Single().Intervals.Select(i => i.Event.LineName).Should().Equal("Entry A", "Exit A");
        sink.Published.OfType<ZoneOccupancyBatch>().Single().Occupancy.Single().Event.Count.Should().Be(41);
    }

    [Fact]
    public async Task Ingest_Should_Throw_When_TheSinkFails()
    {
        var (ingest, sink, _) = Create();
        sink.Fail = true;

        var act = () => ingest.IngestAsync(Device(), DeviceDialect.Canonical, Json(Push()), Received, CancellationToken.None);

        await act.Should().ThrowAsync<SensingSinkUnavailableException>();
    }

    [Fact]
    public async Task Ingest_Should_RefuseExtremeTimesWithoutThrowing_When_TheClockIsCorrected()
    {
        var (ingest, _, clocks) = Create();
        var device = Device();
        for (var i = 0; i < 4; i++)
            clocks.Observe(device.DeviceId, Received.AddSeconds(i).AddMilliseconds(620), Received.AddSeconds(i));
        var json = """
            { "tracks": [{ "trackId": "1", "x": 1, "y": 1, "timeUtc": "0001-01-01T00:00:00Z" },
                         { "trackId": "2", "x": 1, "y": 1, "timeUtc": "9999-12-31T23:59:59Z" }],
              "status": { "online": true, "timeUtc": "0001-01-01T00:00:00Z" } }
            """;

        var result = await ingest.IngestAsync(device, DeviceDialect.Canonical, Json(json), Received.AddSeconds(4), CancellationToken.None);

        result.HasErrors.Should().BeFalse();
        result.Data.Rejected.Should().Be(3);
        result.Data.Clock.State.Should().Be(ClockState.Corrected);
    }

    [Fact]
    public void Clock_Should_IgnoreForgedReadingsAndResends()
    {
        var clocks = new DeviceClockStore();
        var device = Guid.NewGuid();

        clocks.Observe(device, Received.AddMilliseconds(20), Received);
        clocks.Observe(device, Received.AddYears(3), Received.AddSeconds(1)).EstimateMilliseconds.Should().Be(20, "a reading beyond a day is ignored");
        clocks.Observe(device, Received.AddMilliseconds(20), Received.AddMinutes(2)).Recent.Should().HaveCount(1, "a resend with an old send time is not a reading");
        clocks.Observe(device, Received.AddSeconds(5).AddMilliseconds(30), Received.AddSeconds(5)).Recent.Should().HaveCount(2);
    }

    [Fact]
    public async Task Ingest_Should_GiveANewBatchId_When_TheVendorCounterRestarts()
    {
        var (ingest, sink, _) = Create();
        await ingest.IngestAsync(Device(), DeviceDialect.Canonical, Json(Push()), Received, CancellationToken.None);
        var first = sink.Published.OfType<IntervalCountBatch>().Single().Id;
        sink.Published.Clear();

        var restarted = Push(sent: "2026-10-02T14:05:10.900Z").Replace("\"out\": 5", "\"out\": 6", StringComparison.Ordinal);
        await ingest.IngestAsync(Device(), DeviceDialect.Canonical, Json(restarted), Received.AddSeconds(10), CancellationToken.None);

        sink.Published.OfType<IntervalCountBatch>().Single().Id.Should().NotBe(first, "same package id, other content");
    }

    [Fact]
    public async Task Ingest_Should_FlagLateEventsOfEveryKind()
    {
        var (ingest, sink, _) = Create();
        var json = """
            { "occupancy": [{ "zoneName": "Snake A", "count": 3, "timeUtc": "2026-10-02T13:30:00Z" }],
              "crossings": [{ "lineName": "Entry A", "direction": "In", "timeUtc": "2026-10-02T13:30:00Z" }] }
            """;

        var result = await ingest.IngestAsync(Device(), DeviceDialect.Canonical, Json(json), Received, CancellationToken.None);

        result.Data.Flagged.Should().Be(2);
        sink.Published.OfType<ZoneOccupancyBatch>().Single().Occupancy.Single().Flags.Should().HaveFlag(SensedFlags.Skewed);
    }

    [Fact]
    public void Batch_Should_FitInAKafkaMessage_When_ItHoldsTheMostEventsAllowed()
    {
        var samples = Enumerable.Range(0, IngestSettings.MaxEventsLimit)
            .Select(i => new Sensed<TrackPosition>(new TrackPosition("S-17-LONG-DEVICE/" + new string('x', 64), 1234.5678901234567 + i, 987.6543210987654, 1.7234567890123, Received),
                Received, SensedFlags.Corrected | SensedFlags.Skewed))
            .ToList();
        var batch = new TrackSampleBatch
        {
            DeviceId = Guid.NewGuid(), DeviceCode = "S-17-LONG-DEVICE", SiteCode = "AAAAAAAA-BBBBBBBB", QueueZoneName = new string('z', 200), Dialect = "Xovis",
            Clock = new ClockReading(123.4, true, ClockState.Corrected), Samples = samples
        };

        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(batch, Ariva.Infra.Messaging.EventCatalog.Json).Length.Should().BeLessThan(1_000_000);
    }

    private static DeviceCredentialRecord Ouster(string mapping = "ouster-detect-v1") =>
        new(Guid.Parse("0199a000-0000-7000-8000-0000000d0024"), "L-24", "DMO", "Snake A", "Online", new string('a', 64), [], null, "Declarative", 20, 16, 90, mapping);

    [Fact]
    public async Task Ingest_Should_MapThroughTheDevicesMapping_And_NotCorrectReceiptTimes()
    {
        var (ingest, sink, clocks) = Create();
        // The device's clock is known to be 2 seconds ahead and stable; occupancy stamped at receipt is Ariva's own time.
        var id = Guid.Parse("0199a000-0000-7000-8000-0000000d0024");
        for (var k = 0; k < 10; k++)
            clocks.Observe(id, Received.AddSeconds(-60 + (k * 5)).AddMilliseconds(2_000), Received.AddSeconds(-60 + (k * 5)));
        clocks.Current(id).Reading.State.Should().Be(ClockState.Corrected, "the setup is a device whose clock would be corrected");
        var occupations = File.ReadAllText(RepositoryPaths.Resolve("Platform/Backplane/Ariva.UnitTests/Sensing/Samples/declarative/ouster-detect-v1/occupations.json"));

        var result = await ingest.IngestAsync(Ouster(), DeviceDialect.Declarative, Json(occupations), Received, CancellationToken.None);

        result.HasErrors.Should().BeFalse(string.Join("; ", result.ErrorMessages));
        result.Data.Accepted.Should().Be(2);
        var batch = sink.Published.OfType<ZoneOccupancyBatch>().Single();
        batch.Dialect.Should().Be("Declarative:ouster-detect-v1");
        batch.Occupancy.Select(o => (o.Event.ZoneName, o.Event.Count, o.TimeUtc, o.Flags)).Should().Equal(
            ("Snake A", 41, Received, SensedFlags.None), ("Overflow A", 3, Received, SensedFlags.None));

        var tracks = File.ReadAllText(RepositoryPaths.Resolve("Platform/Backplane/Ariva.UnitTests/Sensing/Samples/declarative/ouster-detect-v1/object-list.json"));
        var tracked = await ingest.IngestAsync(Ouster(), DeviceDialect.Declarative, Json(tracks), Received, CancellationToken.None);
        tracked.Data.Accepted.Should().Be(3);
        tracked.Data.Ignored.Should().Be(2, "the vehicle and the bicycle are left out by the mapping");
        sink.Published.OfType<TrackSampleBatch>().Single().Samples.Select(t => t.Event.TrackId).Should().Equal("L-24/1094", "L-24/1095", "L-24/1094");
    }

    [Fact]
    public async Task Ingest_Should_Refuse_When_TheMappingIsNotShippedOrTheDialectDiffers()
    {
        var (ingest, sink, _) = Create();

        var unknown = await ingest.IngestAsync(Ouster("gone-v9"), DeviceDialect.Declarative, Json("{}"), Received, CancellationToken.None);
        var other = await ingest.IngestAsync(Ouster(), DeviceDialect.Canonical, Json(Push()), Received, CancellationToken.None);

        unknown.ErrorMessages.Should().ContainSingle().Which.Should().Contain("not in this version of Ariva");
        other.ErrorMessages.Should().ContainSingle().Which.Should().Contain("Declarative dialect");
        sink.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task BatchIds_Should_DifferForTheSameReceiptTimedBodyAtAnotherTime()
    {
        var (ingest, sink, _) = Create();
        var occupations = File.ReadAllText(RepositoryPaths.Resolve("Platform/Backplane/Ariva.UnitTests/Sensing/Samples/declarative/ouster-detect-v1/occupations.json"));

        await ingest.IngestAsync(Ouster(), DeviceDialect.Declarative, Json(occupations), Received, CancellationToken.None);
        await ingest.IngestAsync(Ouster(), DeviceDialect.Declarative, Json(occupations), Received.AddSeconds(5), CancellationToken.None);
        await ingest.IngestAsync(Ouster(), DeviceDialect.Declarative, Json(occupations), Received.AddSeconds(5), CancellationToken.None);

        var ids = sink.Published.OfType<ZoneOccupancyBatch>().Select(b => b.Id).ToList();
        ids.Should().HaveCount(3);
        ids[0].Should().NotBe(ids[1], "an identical count read 5 seconds later is a new reading, stamped at its receipt");
        ids[1].Should().Be(ids[2], "the same body received at the same instant is the same batch");
    }
}
