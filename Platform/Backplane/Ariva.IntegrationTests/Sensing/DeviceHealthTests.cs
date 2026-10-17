using System.Globalization;
using Ariva.Core;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Sensing;
using Ariva.Core.Services;
using Ariva.Core.Services.Administration;
using Ariva.Core.Services.Sensing;
using Ariva.Core.Services.Topology;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.IntegrationTests.Sensing;

/// <summary>
/// ARV-025 against PostgreSQL with script 0016: a health report is the device's heartbeat (newer reports only); a
/// commissioned device not heard from for the timeout goes Offline in a sweep and its zone Degraded, one that reports
/// itself unwell goes Degraded, and a report brings it back Online and the zone Healthy; stale and out-of-bounds
/// reports change nothing; device and zone changes go to the outbox; reads are limited to the caller's sites.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DeviceHealthTests(PostgresFixture fixture) : IAsyncDisposable
{
    private readonly AccountsHost _host = new(fixture, database: TestDatabase.DeviceHealth);

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ISvcDeviceHealth Health(IServiceProvider s) => s.GetRequiredService<ISvcDeviceHealth>();

    private static string Rect(double x, double y, double w, double h) =>
        string.Create(CultureInfo.InvariantCulture, $"{x} {y},{x + w} {y},{x + w} {y + h},{x} {y + h}");

    /// <summary>A site with a published queue zone (by default "Snake A") and two devices over it, both calibrated and Online.</summary>
    private async Task<(Guid Admin, Guid First, Guid Second)> SiteAsync(string site, string zone = "Snake A")
    {
        var admin = await _host.CreateUserAsync($"it.health.{site.ToLowerInvariant()}", roles: [RoleCodes.SystemAdministrator]);
        await _host.ReadAsync<int>("UPDATE \"user\" SET all_sites = true WHERE id = @id RETURNING 1", admin);
        await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest(site, site), Ct));
        var topology = (IServiceProvider s) => s.GetRequiredService<ISvcTopology>();
        var airport = await _host.AsCallerAsync(admin, s => topology(s).CreateAirportAsync(new CreateAirportRequest(site, null, "Health", "Asia/Dubai"), Ct));
        var terminal = await _host.AsCallerAsync(admin, s => topology(s).CreateTerminalAsync(new CreateTerminalRequest(airport.Data.Id, "T1", "T1", site), Ct));
        var level = (await _host.AsCallerAsync(admin, s => topology(s).CreateLevelAsync(new CreateLevelRequest(terminal.Data.Id, "L0", "Arrivals", 0, 100, 50), Ct))).Data.Id;
        var profiles = (IServiceProvider s) => s.GetRequiredService<ISvcZoneProfiles>();
        var draft = (await _host.AsCallerAsync(admin, s => profiles(s).CreateDraftAsync(new CreateZoneProfileDraftRequest(site, "Arrivals"), Ct))).Data.Profile.Id;
        var queue = (await _host.AsCallerAsync(admin, s => profiles(s).AddZoneAsync(draft, new AddZoneRequest(zone, "Queue", level, Rect(10, 10, 24, 12)), Ct))).Data.Id;
        await _host.AsCallerAsync(admin, s => profiles(s).AddLineAsync(draft, new AddLineRequest("Entry A", "Entry", level, 10, 12, 10, 16, queue), Ct));
        await _host.AsCallerAsync(admin, s => profiles(s).AddLineAsync(draft, new AddLineRequest("Exit A", "Exit", level, 30, 22, 34, 22, queue), Ct));
        var hash = (await _host.AsCallerAsync(admin, s => profiles(s).ValidateAsync(draft, Ct))).Data.GeometryHash;
        (await _host.AsCallerAsync(admin, s => profiles(s).PublishAsync(draft, new PublishZoneProfileRequest(hash), Ct))).HasErrors.Should().BeFalse();

        async Task<Guid> Online(string code, double x)
        {
            var devices = (IServiceProvider s) => s.GetRequiredService<ISvcDevices>();
            var registered = await _host.AsCallerAsync(admin, s => devices(s).RegisterAsync(
                new RegisterDeviceRequest(code, "StereoVision", "PC2SE", "HttpsPush", "Xovis", "Ntp", new DevicePlacement(level, x, 16, 5, 0, zone)), Ct));
            registered.HasErrors.Should().BeFalse(string.Join(", ", registered.ErrorMessages ?? []));
            var id = registered.Data.Device.Id;
            var passed = await _host.AsCallerAsync(admin, s => devices(s).RecordCalibrationAsync(id, new RecordCalibrationRequest("ManualCountTally", 200, 97, 0.4), Ct));
            passed.Data.DeviceState.Should().Be("Online");
            return id;
        }

        return (admin, await Online("S-1", 18), await Online("S-2", 26));
    }

    private DeviceHealthReported Report(Guid device, string site, bool online = true, double secondsAgo = 0, double? temperature = 41) => new()
    {
        DeviceId = device,
        DeviceCode = "S-?",
        SiteCode = site,
        QueueZoneName = "Snake A",
        Status = new DeviceStatus(online, temperature, 12.5, null, _host.Clock.GetUtcNow().UtcDateTime.AddSeconds(-secondsAgo)),
        Clock = new ClockReading(-18, true, ClockState.Ok),
        ReceivedUtc = _host.Clock.GetUtcNow().UtcDateTime.AddSeconds(-secondsAgo)
    };

    private Task<DeviceHealthSweep> SweepAsync() => _host.AsCallerAsync(null, s => Health(s).SweepAsync(Ct));

    private Task RecordAsync(DeviceHealthReported report) => _host.AsCallerAsync<bool>(null, async s =>
    {
        await Health(s).RecordAsync(report, Ct);
        return true;
    });

    private Task<string> StateAsync(Guid device) => _host.ReadAsync<string>("SELECT state FROM device WHERE id = @id", device);

    private Task<string> ZoneAsync(string site) => _host.ReadAsync<string>(
        "SELECT coalesce((SELECT state FROM zone_health WHERE site_code = @secret AND queue_zone_name = 'Snake A'), 'none')", null, site);

    [Fact]
    public async Task Heartbeat_Should_MarkOfflineAndDegradeTheZone_Then_Recover()
    {
        var (admin, first, second) = await SiteAsync("HBA");
        await RecordAsync(Report(first, "HBA"));
        await RecordAsync(Report(second, "HBA"));
        (await SweepAsync()).Ran.Should().BeTrue();
        (await ZoneAsync("HBA")).Should().Be("Healthy");

        _host.Clock.Advance(TimeSpan.FromSeconds(100));
        await RecordAsync(Report(second, "HBA"));
        _host.Clock.Advance(TimeSpan.FromSeconds(100));
        var sweep = await SweepAsync();

        sweep.MarkedOffline.Should().BeGreaterThanOrEqualTo(1);
        (await StateAsync(first)).Should().Be("Offline", "the first device has been silent for more than 180 s");
        (await StateAsync(second)).Should().Be("Online");
        (await ZoneAsync("HBA")).Should().Be("Degraded");
        var overview = await _host.AsCallerAsync(admin, s => Health(s).OverviewAsync("HBA", Ct));
        overview.Data.HeartbeatTimeoutSeconds.Should().Be(180);
        overview.Data.Devices.Single(d => d.Id == first).SecondsSinceSeen.Should().BeApproximately(200, 0.01);
        overview.Data.Zones.Should().ContainSingle().Which.Should().Match<Ariva.Core.Domain.ViewModels.ZoneHealthViewModel>(z =>
            z.State == "Degraded" && z.Devices == 2 && z.DevicesOffline == 1 && z.DevicesDegraded == 0);

        await SweepAsync();
        await RecordAsync(Report(first, "HBA"));
        (await StateAsync(first)).Should().Be("Online", "a report brings it back");
        (await ZoneAsync("HBA")).Should().Be("Healthy");

        await RecordAsync(Report(second, "HBA", online: false));
        (await StateAsync(second)).Should().Be("Degraded", "a device that reports itself unwell");
        (await ZoneAsync("HBA")).Should().Be("Degraded");
        _host.Clock.Advance(TimeSpan.FromSeconds(1));
        await RecordAsync(Report(second, "HBA"));
        (await ZoneAsync("HBA")).Should().Be("Healthy");

        (await _host.ReadAsync<long>("SELECT count(*) FROM outbox_message WHERE topic = 'ariva.device.registry-changed.v1' AND message_key = @id::text AND payload->>'change' IN ('HealthOffline', 'HealthOnline')", first))
            .Should().Be(2, "Offline once (a second sweep does not mark it again), then Online");
        (await _host.ReadAsync<long>("SELECT count(*) FROM outbox_message WHERE topic = 'ariva.device.zone-health.v1' AND message_key = @secret", null, "HBA/Snake A"))
            .Should().Be(5, "Healthy, Degraded, Healthy, Degraded, Healthy");
        (await _host.ReadAsync<string>("SELECT payload->>'state' FROM outbox_message WHERE topic = 'ariva.device.zone-health.v1' AND message_key = @secret ORDER BY seq DESC LIMIT 1", null, "HBA/Snake A"))
            .Should().Be("Healthy");
    }

    [Fact]
    public async Task Reports_Should_ChangeNothing_When_StaleOutOfOrderOrOutOfBounds()
    {
        var (_, first, _) = await SiteAsync("HBB");
        await RecordAsync(Report(first, "HBB"));
        var seen = await _host.ReadAsync<DateTime>("SELECT last_seen_on FROM device_heartbeat WHERE device_id = @id", first);

        await RecordAsync(Report(first, "HBB", secondsAgo: 30));
        (await _host.ReadAsync<DateTime>("SELECT last_seen_on FROM device_heartbeat WHERE device_id = @id", first)).Should().Be(seen, "an older report does not replace a newer one");

        _host.Clock.Advance(TimeSpan.FromMinutes(5));
        (await SweepAsync()).MarkedOffline.Should().BeGreaterThanOrEqualTo(1);
        await RecordAsync(Report(first, "HBB", secondsAgo: 200));
        (await StateAsync(first)).Should().Be("Offline", "a report older than the timeout (a backlog) does not bring a device back");

        await RecordAsync(Report(first, "HBB", temperature: 900));
        (await StateAsync(first)).Should().Be("Offline", "a report out of the canonical bounds is ignored");
        await RecordAsync(Report(Guid.NewGuid(), "HBB"));
        (await _host.ReadAsync<long>("SELECT count(*) FROM device_heartbeat WHERE site_code = 'HBB'")).Should().Be(1, "unknown devices leave nothing");
    }

    [Fact]
    public async Task Health_Should_BeLimitedToTheCallersSites()
    {
        var (_, first, _) = await SiteAsync("HBC");
        await RecordAsync(Report(first, "HBC"));
        var other = await _host.CreateUserAsync("it.health.other", roles: [RoleCodes.BorderShiftSupervisor]);

        (await _host.AsCallerAsync(other, s => Health(s).OverviewAsync("HBC", Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(other, s => Health(s).GetAsync(first, Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        var all = await _host.AsCallerAsync(other, s => Health(s).OverviewAsync(null, Ct));
        all.Data.Devices.Should().BeEmpty();
        all.Data.Zones.Should().BeEmpty();
    }

    [Fact]
    public async Task Sweep_Should_RunOnOneReplicaAtATime()
    {
        await SiteAsync("HBD");
        await using var holder = _host.Provider.CreateAsyncScope();
        var unitOfWork = holder.ServiceProvider.GetRequiredService<IUnitOfWork>();
        (await Health(holder.ServiceProvider).SweepAsync(Ct)).Ran.Should().BeTrue();

        (await SweepAsync()).Ran.Should().BeFalse("the first sweep's transaction still holds the lock");
        await unitOfWork.EndAsync(Ct);
        (await SweepAsync()).Ran.Should().BeTrue();
    }

    [Fact]
    public async Task Health_Should_NeverChangeCommissioningOrRetiredDevices_And_DropMalformedReports()
    {
        var (admin, first, second) = await SiteAsync("HBE");
        await RecordAsync(Report(first, "HBE"));
        await RecordAsync(Report(second, "HBE"));
        await SweepAsync();
        (await ZoneAsync("HBE")).Should().Be("Healthy");
        _host.Clock.Advance(TimeSpan.FromSeconds(1));
        var devices = (IServiceProvider s) => s.GetRequiredService<ISvcDevices>();
        (await _host.AsCallerAsync(admin, s => devices(s).UpdateAsync(first, new UpdateDeviceRequest("PC2SE", "Mqtt", "Xovis", "Ntp"), Ct))).Data.State
            .Should().Be("Commissioning", "a new transport needs a new calibration");
        (await _host.AsCallerAsync(admin, s => devices(s).RetireAsync(second, Ct))).Data.State.Should().Be("Retired");

        await RecordAsync(Report(first, "HBE", online: false));
        (await StateAsync(first)).Should().Be("Commissioning", "health never changes a device in commissioning");
        (await _host.ReadAsync<bool>("SELECT reported_online FROM device_heartbeat WHERE device_id = @id", first)).Should().BeFalse("its heartbeat is still recorded");
        var retiredSeen = await _host.ReadAsync<DateTime>("SELECT last_seen_on FROM device_heartbeat WHERE device_id = @id", second);
        _host.Clock.Advance(TimeSpan.FromSeconds(1));
        await RecordAsync(Report(second, "HBE"));
        (await _host.ReadAsync<DateTime>("SELECT last_seen_on FROM device_heartbeat WHERE device_id = @id", second)).Should().Be(retiredSeen, "a retired device's reports are dropped");
        _host.Clock.Advance(TimeSpan.FromMinutes(10));
        await SweepAsync();
        (await StateAsync(first)).Should().Be("Commissioning");
        (await StateAsync(second)).Should().Be("Retired");
        (await ZoneAsync("HBE")).Should().Be("Unmonitored", "no commissioned device is left in the zone");

        var seen = await _host.ReadAsync<DateTime>("SELECT last_seen_on FROM device_heartbeat WHERE device_id = @id", first);
        var badClock = Report(first, "HBE");
        badClock.Clock = new ClockReading(-18, true, (ClockState)7);
        await RecordAsync(badClock);
        var otherSite = Report(first, "HBX");
        await RecordAsync(otherSite);
        (await _host.ReadAsync<DateTime>("SELECT last_seen_on FROM device_heartbeat WHERE device_id = @id", first)).Should().Be(seen,
            "a report with an undefined clock state or another site is dropped");
    }

    private Task<long> ZoneEventsAsync(string zoneKey) => _host.ReadAsync<long>(
        "SELECT count(*) FROM outbox_message WHERE topic = 'ariva.device.zone-health.v1' AND message_key = @secret", null, zoneKey);

    [Fact]
    public async Task ZoneHealthChanged_Should_ReachTheOutbox_When_TheZoneKeyIsTheLongestAllowed()
    {
        // ARV-114c: HBF, a slash and a 196-character queue zone name make a key of exactly 200 characters, the most the
        // zone profile accepts and the most outbox_message.message_key holds.
        var zone = "Snake " + new string('L', 190);
        var key = ZoneKeys.For("HBF", zone);
        key.Should().HaveLength(Ariva.Core.Messaging.MessageKeys.MaxLength);
        var (_, first, second) = await SiteAsync("HBF", zone);
        await RecordAsync(Report(first, "HBF"));
        await RecordAsync(Report(second, "HBF"));

        (await SweepAsync()).Ran.Should().BeTrue();
        _host.Clock.Advance(TimeSpan.FromSeconds(200));
        (await SweepAsync()).MarkedOffline.Should().BeGreaterThanOrEqualTo(2, "the sweep covers every site of the shared database");

        (await ZoneEventsAsync(key)).Should().Be(2, "Healthy, then Degraded when both devices went silent");
        (await _host.ReadAsync<int>("SELECT max(length(message_key)) FROM outbox_message WHERE topic = 'ariva.device.zone-health.v1' AND message_key LIKE 'HBF/%'"))
            .Should().Be(200);
        (await _host.ReadAsync<string>("SELECT state FROM zone_health WHERE site_code = 'HBF' AND queue_zone_name = @secret", null, zone)).Should().Be("Degraded");
    }

    [Fact]
    public async Task Sweep_Should_CommitTheOtherZonesAndTheDeviceChanges_When_OneZoneKeyIsTooLongForTheOutbox()
    {
        // ARV-114c: a zone whose name was stored before the zone profile refused it. The overlong name is written straight
        // into the device row (the profile would refuse it now): HBG/ and 200 characters make a key of 204.
        var (_, first, second) = await SiteAsync("HBG");
        var overlong = new string('X', 200);
        (await _host.ReadAsync<int>("UPDATE device SET queue_zone_name = @secret WHERE id = @id RETURNING 1", second, overlong)).Should().Be(1);
        await RecordAsync(Report(first, "HBG"));
        await RecordAsync(Report(second, "HBG"));

        var sweep = await SweepAsync();

        sweep.Ran.Should().BeTrue("the sweep completes and commits");
        (await ZoneAsync("HBG")).Should().Be("Healthy", "Snake A, now with one device, is assessed");
        (await ZoneEventsAsync("HBG/Snake A")).Should().Be(1);
        (await _host.ReadAsync<long>("SELECT count(*) FROM zone_health WHERE site_code = 'HBG' AND queue_zone_name = @secret", null, overlong))
            .Should().Be(0, "the overlong zone is skipped, not half written");
        (await _host.ReadAsync<long>("SELECT count(*) FROM outbox_message WHERE topic = 'ariva.device.zone-health.v1' AND message_key LIKE 'HBG/X%'")).Should().Be(0);

        // Both devices go silent: the sweep marks both Offline (the overlong zone's device too) and Snake A Degraded.
        _host.Clock.Advance(TimeSpan.FromSeconds(200));
        (await SweepAsync()).MarkedOffline.Should().BeGreaterThanOrEqualTo(2, "the sweep covers every site of the shared database");
        (await StateAsync(first)).Should().Be("Offline");
        (await StateAsync(second)).Should().Be("Offline", "the device change beside the skipped zone commits");
        (await ZoneAsync("HBG")).Should().Be("Degraded");
        (await ZoneEventsAsync("HBG/Snake A")).Should().Be(2);

        // A report of the overlong zone's device commits too (the consumer's path), and later sweeps keep going.
        await RecordAsync(Report(second, "HBG"));
        (await StateAsync(second)).Should().Be("Online");
        (await SweepAsync()).Ran.Should().BeTrue();
        (await _host.ReadAsync<long>("SELECT count(*) FROM outbox_message WHERE message_key LIKE 'HBG/X%'")).Should().Be(0);
    }
}
