using System.Text.Json;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Queueing;
using Ariva.Core.Sensing;
using Ariva.Infra.Messaging;
using FluentAssertions;

namespace Ariva.UnitTests.Queueing;

/// <summary>
/// ARV-036: a zone's device liveness from its own records. A device heard before and silent for longer than the silence
/// limit is out from the minute it was last heard in, until it is heard again; a health report may say it is offline;
/// the zone is degraded live meanwhile and its bins are marked; a device silent beyond the forget limit is dropped. The
/// liveness survives a snapshot, and the queue engine accepts track ids as Ingest and the archive write them.
/// </summary>
public sealed class DeviceLivenessTests
{
    private const string ZoneKey = "DMO/A-VIS";
    private static readonly DateTime Start = new(2026, 9, 28, 18, 0, 0, DateTimeKind.Utc);
    private static readonly QueueZoneGeometry Geometry = new("A-VIS", new HashSet<string> { "A-VIS entry" }, new HashSet<string> { "A-VIS exit" },
        new HashSet<string>(), new HashSet<string>());

    private static T Stamp<T>(T batch, string device, DateTime received) where T : SensingBatch
    {
        batch.Id = Guid.NewGuid();
        batch.DeviceId = Guid.NewGuid();
        batch.DeviceCode = device;
        batch.SiteCode = "DMO";
        batch.QueueZoneName = "A-VIS";
        batch.Commissioned = true;
        batch.ReceivedUtc = received;
        return batch;
    }

    private static DeviceStatusBatch Status(string device, DateTime received, bool online = true) =>
        Stamp(new DeviceStatusBatch { Online = online }, device, received);

    private static ZoneOccupancyBatch Occupancy(string device, DateTime received, int count = 3) =>
        Stamp(new ZoneOccupancyBatch { Occupancy = [new Sensed<ZoneOccupancy>(new ZoneOccupancy("A-VIS", count, received), received, SensedFlags.None)] }, device, received);

    /// <summary>S-15 reports every minute throughout; S-17 every minute except while <paramref name="silent"/> says so.</summary>
    private static ZoneProcessor Play(Func<int, bool> silent, int minutes = 40, ZoneProcessorSettings settings = null)
    {
        var zone = new ZoneProcessor(ZoneKey, Geometry, 12, settings);
        for (var m = 0; m < minutes; m++)
        {
            var at = Start.AddMinutes(m);
            zone.Offer(Occupancy("S-15", at), DateTime.MaxValue);
            if (!silent(m))
                zone.Offer(Status("S-17", at), DateTime.MaxValue);
        }

        zone.Finish(Start.AddMinutes(minutes), Start.AddMinutes(minutes + 200));
        return zone;
    }

    [Fact]
    public void Zone_Should_RecordAnOutage_When_ADeviceIsSilentBeyondTheLimit()
    {
        // S-17 last heard at 18:10, heard again at 18:21.
        var outputs = Play(m => m is > 10 and < 21).Drain();

        outputs.Outages.Should().Equal(new DeviceOutage(ZoneKey, "S-17", Start.AddMinutes(10), Start.AddMinutes(21)));
        var degraded = outputs.Live.Where(l => l.LengthDegraded).Select(l => (int)(l.MinuteUtc - Start).TotalMinutes).ToList();
        degraded.Should().NotBeEmpty().And.OnlyContain(m => m >= 10 && m < 21, "degraded live only while S-17 is out (once its silence passes the limit)");
        degraded.Should().Contain(20);
        outputs.Live.Where(l => l.LengthDegraded).Should().OnlyContain(l => l.NowcastDegraded || l.NowcastMinutes == null);
        outputs.Bins.Where(b => b.Status == BinStatus.Final && b.StartUtc >= Start && b.StartUtc < Start.AddMinutes(30)).GroupBy(b => b.StartUtc).Select(g => g.Last().Quality)
            .Should().Equal(BinQuality.Degraded, BinQuality.Degraded);
        outputs.Bins.Where(b => b.Status == BinStatus.Final && b.StartUtc == Start.AddMinutes(30)).Should().OnlyContain(b => b.Quality == BinQuality.Good);
    }

    [Fact]
    public void Zone_Should_NotRecordAnOutage_When_ASilenceIsWithinTheLimit()
    {
        var outputs = Play(m => m is > 10 and < 13).Drain();

        outputs.Outages.Should().BeEmpty("two silent minutes are within 180 seconds");
        outputs.Live.Should().OnlyContain(l => !l.LengthDegraded);
        outputs.Bins.Should().OnlyContain(b => b.Quality == BinQuality.Good);
    }

    [Fact]
    public void Zone_Should_RecordAnOutageAtOnce_When_ADeviceReportsItselfOffline()
    {
        var zone = new ZoneProcessor(ZoneKey, Geometry, 12);
        zone.Offer(Status("S-17", Start), DateTime.MaxValue);
        zone.Offer(Status("S-17", Start.AddSeconds(30), online: false), DateTime.MaxValue);
        zone.Offer(Status("S-17", Start.AddMinutes(1).AddSeconds(30), online: false), DateTime.MaxValue);
        zone.Offer(Status("S-17", Start.AddMinutes(4)), DateTime.MaxValue);

        zone.Drain().Outages.Should().Equal(new DeviceOutage(ZoneKey, "S-17", Start, Start.AddMinutes(4)));
    }

    [Fact]
    public void Replay_Should_ReportAnOpenOutage_When_TheRangeEndsWhileADeviceIsOut()
    {
        var outputs = Play(m => m > 30).Drain();

        outputs.Outages.Should().Equal(new DeviceOutage(ZoneKey, "S-17", Start.AddMinutes(30), Start.AddMinutes(40), Closed: false));
        outputs.Live.Where(l => l.MinuteUtc >= Start.AddMinutes(41)).Should().OnlyContain(l => !l.LengthDegraded, "devices are not watched after the end");
    }

    [Fact]
    public void Zone_Should_ForgetADevice_When_ItIsSilentBeyondTheForgetLimit()
    {
        var settings = new ZoneProcessorSettings { DeviceForgetHours = 1 };
        var zone = new ZoneProcessor(ZoneKey, Geometry, 12, settings);
        zone.Offer(Status("S-17", Start), DateTime.MaxValue);
        for (var m = 1; m <= 120; m++)
            zone.Offer(Occupancy("S-15", Start.AddMinutes(m)), DateTime.MaxValue);

        var outputs = zone.Drain();

        outputs.Outages.Should().Equal(new DeviceOutage(ZoneKey, "S-17", Start, Start.AddHours(1)));
        outputs.Live.Where(l => l.MinuteUtc >= Start.AddMinutes(62)).Should().OnlyContain(l => !l.LengthDegraded, "a forgotten device no longer degrades its zone");
        zone.Capture().Devices.Select(d => d.DeviceCode).Should().Equal("S-15");
    }

    [Fact]
    public void Zone_Should_IgnoreUncommissionedDevices_When_TheyFallSilent()
    {
        var zone = new ZoneProcessor(ZoneKey, Geometry, 12);
        var commissioning = Status("S-99", Start);
        commissioning.Commissioned = false;
        zone.Offer(commissioning, DateTime.MaxValue);
        for (var m = 1; m <= 10; m++)
            zone.Offer(Occupancy("S-15", Start.AddMinutes(m)), DateTime.MaxValue);

        zone.Drain().Live.Should().OnlyContain(l => !l.LengthDegraded);
    }

    [Fact]
    public void Liveness_Should_ContinueIdentically_When_RestoredFromASnapshotDuringAnOutage()
    {
        string Json(object value) => JsonSerializer.Serialize(value, EventCatalog.Json);
        var straight = Play(m => m is > 10 and < 21).Drain();

        var zone = new ZoneProcessor(ZoneKey, Geometry, 12);
        var outputs = new List<ZoneOutputs>();
        for (var m = 0; m < 40; m++)
        {
            var at = Start.AddMinutes(m);
            zone.Offer(Occupancy("S-15", at), DateTime.MaxValue);
            if (m is <= 10 or >= 21)
                zone.Offer(Status("S-17", at), DateTime.MaxValue);
            if (m is 15 or 21)
            {
                outputs.Add(zone.Drain());
                var state = JsonSerializer.Deserialize<ZoneProcessorState>(Json(zone.Capture()), EventCatalog.Json);
                state!.Devices.Should().NotBeEmpty();
                zone = ZoneProcessor.Restore(ZoneKey, Geometry, 12, new ZoneProcessorSettings(), state, Start.AddDays(1));
            }
        }

        zone.Finish(Start.AddMinutes(40), Start.AddMinutes(240));
        outputs.Add(zone.Drain());

        Json(outputs.SelectMany(o => o.Outages)).Should().Be(Json(straight.Outages));
        Json(outputs.SelectMany(o => o.Live)).Should().Be(Json(straight.Live));
        Json(outputs.SelectMany(o => o.Bins)).Should().Be(Json(straight.Bins));
    }

    [Fact]
    public void Restore_Should_AcceptAVersionOneSnapshot_And_RefuseHostileDeviceStates()
    {
        var zone = new ZoneProcessor(ZoneKey, Geometry, 12);
        zone.Offer(Status("S-17", Start), DateTime.MaxValue);
        var state = zone.Capture();
        var notAfter = Start.AddDays(1);

        ZoneProcessor.Restore(ZoneKey, Geometry, 12, new ZoneProcessorSettings(), state with { Version = 1, Devices = [], RecentOutages = [] }, notAfter)
            .Capture().Devices.Should().BeEmpty("a version 1 snapshot has heard no devices yet");
        var device = state.Devices[0];
        foreach (var bad in new[]
                 {
                     state with { Version = 3 },
                     state with { Devices = [device with { DeviceCode = "S/17" }] },
                     state with { Devices = [device with { DeviceCode = new string('S', 17) }] },
                     state with { Devices = [device, device] },
                     state with { Devices = [device with { OutSinceUtc = device.LastHeardUtc.AddMinutes(5) }] },
                     state with { Devices = [device with { LastHeardUtc = notAfter.AddDays(2) }] },
                     state with { Devices = [.. Enumerable.Range(0, 5_000).Select(i => device with { DeviceCode = $"S-{i}" })] },
                     state with { RecentOutages = [new RecentOutageState("S-17", Start, Start)] },
                     state with { Devices = [device with { DeviceCode = "S 17" }] },
                     state with { Devices = [device with { LastHeardUtc = DateTime.MinValue, OutSinceUtc = DateTime.MinValue }] },
                     state with { RecentOutages = [new RecentOutageState("S-17", DateTime.MinValue, DateTime.MinValue.AddDays(1))] }
                 })
        {
            var restore = () => ZoneProcessor.Restore(ZoneKey, Geometry, 12, new ZoneProcessorSettings(), bad, notAfter);
            restore.Should().Throw<InvalidDataException>();
        }
    }

    [Theory]
    [InlineData("A B")]
    [InlineData("A\tB")]
    [InlineData("A\u0001B")]
    [InlineData("A\u007fB")]
    [InlineData("s-17")]
    [InlineData("-S17")]
    [InlineData("S--17")]
    [InlineData("S-17-LONG-DEVICE1")]
    public void Zone_Should_CountABatchInvalid_When_ItsDeviceCodeIsNotARegistryCode(string code)
    {
        var zone = new ZoneProcessor(ZoneKey, Geometry, 12);

        zone.Offer(Status(code, Start), DateTime.MaxValue);
        zone.Offer(Occupancy(code, Start.AddSeconds(5)), DateTime.MaxValue);

        zone.Counters.Invalid.Should().Be(2);
        var state = zone.Capture();
        state.Devices.Should().BeEmpty();
        ZoneProcessor.Restore(ZoneKey, Geometry, 12, new ZoneProcessorSettings(), state, Start.AddDays(1)).Should().NotBeNull("one record cannot spoil the snapshot");
    }

    [Fact]
    public void Zone_Should_NotRepeatRecomputations_When_ADeviceStaysOutPastFinalBins()
    {
        // S-17 silent for two hours after 18:05: bins become final while it is out (S-15 keeps the clock moving), so the
        // outage's progressive marks ask for each final bin at most once.
        var outputs = Play(m => m is > 5 and < 125, minutes: 140).Drain();

        outputs.Outages.Should().ContainSingle();
        var asked = outputs.Recomputations.SelectMany(r => Enumerable.Range(0, (int)((r.ToUtc - r.FromUtc).TotalMinutes / 15)).Select(k => r.FromUtc.AddMinutes(15 * k))).ToList();
        asked.Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData("S-15/A-VIS.7", true)]
    [InlineData("S-15/~AbCdEfGhIjKlMnOpQrStUv", true)]
    [InlineData("A-VIS.7", true)]
    [InlineData("S-16/A-VIS.7", false)]
    [InlineData("S-15/~short", false)]
    [InlineData("S-15/a b", false)]
    public void Zone_Should_TakeTrackIds_When_IngestOrTheArchiveWroteThem(string trackId, bool accepted)
    {
        var zone = new ZoneProcessor(ZoneKey, Geometry, 12);
        var batch = Stamp(new VendorLineCrossingBatch
        {
            Crossings = [new Sensed<LineCrossing>(new LineCrossing("A-VIS entry", CrossingDirection.In, trackId, Start), Start, SensedFlags.None)]
        }, "S-15", Start.AddSeconds(5));

        zone.Offer(batch, DateTime.MaxValue);

        zone.Counters.Invalid.Should().Be(accepted ? 0 : 1);
    }

    [Fact]
    public void Settings_Should_RefuseDeviceLimits_When_TheyAreOutOfRange()
    {
        new ZoneProcessorSettings { DeviceSilenceSeconds = 10 }.Problems().Should().NotBeEmpty();
        new ZoneProcessorSettings { DeviceForgetHours = 0 }.Problems().Should().NotBeEmpty();
        new ZoneProcessorSettings { DeviceSilenceSeconds = 3_600, DeviceForgetHours = 1 }.Problems().Should().NotBeEmpty();
        new ZoneProcessorSettings { MaxDevices = 0 }.Problems().Should().NotBeEmpty();
        new ZoneProcessorSettings().Problems().Should().BeEmpty();
    }
}
