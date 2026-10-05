using System.Globalization;
using Ariva.Core;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Services.Administration;
using Ariva.Core.Services.Sensing;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Security;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ariva.IntegrationTests.Sensing;

/// <summary>
/// ARV-021 against PostgreSQL with script 0013: a device is registered over a published queue zone with a credential
/// returned once and stored only as prefix and SHA-256; a passed calibration sets it Online, a move sends it back to
/// Commissioning; calibrations cannot be changed or deleted, even by SQL; rotation replaces the credential; retiring
/// revokes it; registry changes go to the outbox without credential material; another site's caller sees nothing.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DeviceRegistryTests(PostgresFixture fixture) : IAsyncDisposable
{
    private readonly AccountsHost _host = new(fixture, database: TestDatabase.Administration);

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ISvcDevices Devices(IServiceProvider s) => s.GetRequiredService<ISvcDevices>();

    private static string Rect(double x, double y, double w, double h) =>
        string.Create(CultureInfo.InvariantCulture, $"{x} {y},{x + w} {y},{x + w} {y + h},{x} {y + h}");

    /// <summary>A site with one level and a published profile: queue "Snake A" (10..34 x 10..22) with an overflow band above it.</summary>
    private async Task<(Guid Admin, Guid Level)> SiteAsync(string site)
    {
        var admin = await _host.CreateUserAsync($"it.dev.{site.ToLowerInvariant()}", roles: [RoleCodes.SystemAdministrator]);
        await _host.ReadAsync<int>("UPDATE \"user\" SET all_sites = true WHERE id = @id RETURNING 1", admin);
        await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest(site, site), Ct));
        var topology = (IServiceProvider s) => s.GetRequiredService<ISvcTopology>();
        var airport = await _host.AsCallerAsync(admin, s => topology(s).CreateAirportAsync(new CreateAirportRequest(site, null, "Devices", "Asia/Dubai"), Ct));
        var terminal = await _host.AsCallerAsync(admin, s => topology(s).CreateTerminalAsync(new CreateTerminalRequest(airport.Data.Id, "T1", "T1", site), Ct));
        var level = (await _host.AsCallerAsync(admin, s => topology(s).CreateLevelAsync(new CreateLevelRequest(terminal.Data.Id, "L0", "Arrivals", 0, 100, 50), Ct))).Data.Id;

        var profiles = (IServiceProvider s) => s.GetRequiredService<ISvcZoneProfiles>();
        var draft = (await _host.AsCallerAsync(admin, s => profiles(s).CreateDraftAsync(new CreateZoneProfileDraftRequest(site, "Arrivals"), Ct))).Data.Profile.Id;
        var queue = (await _host.AsCallerAsync(admin, s => profiles(s).AddZoneAsync(draft, new AddZoneRequest("Snake A", "Queue", level, Rect(10, 10, 24, 12)), Ct))).Data.Id;
        var overflow = (await _host.AsCallerAsync(admin, s => profiles(s).AddZoneAsync(draft, new AddZoneRequest("Overflow A", "Overflow", level, Rect(10, 4, 24, 6), queue), Ct))).Data.Id;
        await _host.AsCallerAsync(admin, s => profiles(s).AddLineAsync(draft, new AddLineRequest("Overflow entry A", "OverflowEntry", level, 10, 5, 10, 9, overflow), Ct));
        await _host.AsCallerAsync(admin, s => profiles(s).AddLineAsync(draft, new AddLineRequest("Entry A", "Entry", level, 10, 12, 10, 16, queue), Ct));
        await _host.AsCallerAsync(admin, s => profiles(s).AddLineAsync(draft, new AddLineRequest("Exit A", "Exit", level, 30, 22, 34, 22, queue), Ct));
        var hash = (await _host.AsCallerAsync(admin, s => profiles(s).ValidateAsync(draft, Ct))).Data.GeometryHash;
        var published = await _host.AsCallerAsync(admin, s => profiles(s).PublishAsync(draft, new PublishZoneProfileRequest(hash), Ct));
        published.HasErrors.Should().BeFalse(string.Join(", ", published.ErrorMessages ?? []));
        return (admin, level);
    }

    private static RegisterDeviceRequest Register(string code, Guid level, double x = 20, double y = 16, string zone = "Snake A", double height = 5) =>
        new(code, "StereoVision", "PC2SE", "HttpsPush", "Xovis", "Ntp", new DevicePlacement(level, x, y, height, 0, zone));

    private static RecordCalibrationRequest Calibration(double accuracy) => new("ManualCountTally", 200, accuracy, 0.4, null, "Morning peak");

    [Fact]
    public async Task Lifecycle_Should_RegisterCalibrateMoveRotateAndRetire_When_RunEndToEnd()
    {
        var (admin, level) = await SiteAsync("DVA");

        var registered = await _host.AsCallerAsync(admin, s => Devices(s).RegisterAsync(Register("S-1", level), Ct));
        registered.HasErrors.Should().BeFalse(string.Join(", ", registered.ErrorMessages ?? []));
        var device = registered.Data.Device;
        var credential = registered.Data.Credential;
        device.State.Should().Be("Commissioning");
        device.Footprint.Text.Should().Be("10 x 10 m");
        device.Footprint.Source.Should().Be("AssumedFromBoq");
        credential.Should().StartWith("ardk_").And.HaveLength(48);
        (await _host.ReadAsync<string>("SELECT credential_hash FROM device WHERE id = @id", device.Id)).Should().Be(DeviceCredentials.Hash(credential));
        (await _host.ReadAsync<string>("SELECT credential_prefix FROM device WHERE id = @id", device.Id)).Should().Be(credential[..13]);
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE position(@secret IN coalesce(before_summary, '') || coalesce(after_summary, '')) > 0", null, credential[13..]))
            .Should().Be(0, "the credential is never audited");

        (await _host.AsCallerAsync(admin, s => Devices(s).RegisterAsync(Register("S-1", level), Ct))).ErrorMessages.Should().Equal(TopologyErrors.Duplicate);
        (await _host.AsCallerAsync(admin, s => Devices(s).RegisterAsync(Register("S-2", level, 80, 40), Ct))).ErrorMessages.Should().Equal(DeviceErrors.OutOfReach);
        (await _host.AsCallerAsync(admin, s => Devices(s).RegisterAsync(Register("S-3", level, zone: "Nowhere"), Ct))).ErrorMessages.Should().Equal(DeviceErrors.ZoneNotFound);
        (await _host.AsCallerAsync(admin, s => Devices(s).RegisterAsync(Register("S-4", level, zone: "Overflow A"), Ct))).ErrorMessages.Should().ContainSingle()
            .Which.Should().Be(DeviceErrors.ZoneNotFound, "the owning zone is a queue zone; an overflow band hangs off one");
        (await _host.AsCallerAsync(admin, s => Devices(s).RegisterAsync(Register("S-5", level, 20, 2), Ct))).HasErrors.Should().BeFalse("over the overflow band of Snake A");

        var failed = await _host.AsCallerAsync(admin, s => Devices(s).RecordCalibrationAsync(device.Id, Calibration(91.5), Ct));
        failed.Data.Passed.Should().BeFalse();
        failed.Data.DeviceState.Should().Be("Commissioning");
        var passed = await _host.AsCallerAsync(admin, s => Devices(s).RecordCalibrationAsync(device.Id, Calibration(96.2), Ct));
        passed.Data.Passed.Should().BeTrue();
        passed.Data.ThresholdPercent.Should().Be(95);
        passed.Data.PerformedBy.Should().Be("it-admin");
        (await _host.AsCallerAsync(admin, s => Devices(s).GetAsync(device.Id, Ct))).Data.State.Should().Be("Online");

        var tamper = () => _host.ReadAsync<int>("UPDATE device_calibration SET passed = true, counting_accuracy_percent = 99 WHERE device_id = @id RETURNING 1", device.Id);
        (await tamper.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23001");
        var erase = () => _host.ReadAsync<int>("DELETE FROM device_calibration WHERE device_id = @id RETURNING 1", device.Id);
        (await erase.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23001");
        var forged = () => _host.ReadAsync<int>(
            "INSERT INTO device_calibration (id, device_id, site_code, method, sample_size, counting_accuracy_percent, wait_time_error_minutes, threshold_percent, passed, performed_on) " +
            "SELECT gen_random_uuid(), id, site_code, 'ManualCountTally', 200, 50, 1, 95, true, now() FROM device WHERE id = @id RETURNING 1", device.Id);
        (await forged.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514", "a pass below the threshold cannot be written");

        var moved = await _host.AsCallerAsync(admin, s => Devices(s).MoveAsync(device.Id, new DevicePlacement(level, 25, 16, 5, 90, "Snake A"), Ct));
        moved.Data.State.Should().Be("Commissioning", "a moved sensor is recalibrated before it counts again");
        moved.Data.OrientationDegrees.Should().Be(90);
        var unchanged = await _host.AsCallerAsync(admin, s => Devices(s).MoveAsync(device.Id, new DevicePlacement(level, 90, 45, 5, 90, "Snake A"), Ct));
        unchanged.ErrorMessages.Should().Equal(DeviceErrors.OutOfReach);
        (await _host.ReadAsync<double>("SELECT x FROM device WHERE id = @id", device.Id)).Should().Be(25, "a refused move changes nothing");

        var rotated = await _host.AsCallerAsync(admin, s => Devices(s).RotateCredentialAsync(device.Id, Ct));
        rotated.Data.Credential.Should().NotBe(credential);
        var storedHash = await _host.ReadAsync<string>("SELECT credential_hash FROM device WHERE id = @id", device.Id);
        DeviceCredentials.Matches(rotated.Data.Credential, storedHash).Should().BeTrue();
        DeviceCredentials.Matches(credential, storedHash).Should().BeFalse("the old credential stops working at once");

        (await _host.AsCallerAsync(admin, s => Devices(s).RemoveAsync(device.Id, Ct))).ErrorMessages.Should().Equal(DeviceErrors.NotRemovable);
        var retired = await _host.AsCallerAsync(admin, s => Devices(s).RetireAsync(device.Id, Ct));
        retired.Data.State.Should().Be("Retired");
        (await _host.ReadAsync<string>("SELECT credential_hash FROM device WHERE id = @id", device.Id)).Should().BeNull();
        (await _host.AsCallerAsync(admin, s => Devices(s).RotateCredentialAsync(device.Id, Ct))).ErrorMessages.Should().Equal(DeviceErrors.Retired);
        (await _host.AsCallerAsync(admin, s => Devices(s).CalibrationsAsync(device.Id, Ct))).Data.Should().HaveCount(2, "history stays");

        (await _host.ReadAsync<long>("SELECT count(*) FROM outbox_message WHERE topic = 'ariva.device.registry-changed.v1' AND message_key = @id::text AND payload->>'deviceId' = @id::text", device.Id))
            .Should().Be(6, "Registered, CalibrationFailed, CalibrationPassed, Moved, CredentialRotated and Retired; nothing for the refused move");
        (await _host.ReadAsync<long>("SELECT count(*) FROM outbox_message WHERE position(@secret IN payload::text) > 0", null, credential[13..])).Should().Be(0);
    }

    [Fact]
    public async Task Calibration_Should_NotSetOnline_When_TheZoneIsOnlyInTheDraft()
    {
        var (admin, level) = await SiteAsync("DVB");
        var profiles = (IServiceProvider s) => s.GetRequiredService<ISvcZoneProfiles>();
        var draft = await _host.AsCallerAsync(admin, s => profiles(s).CreateDraftAsync(new CreateZoneProfileDraftRequest("DVB", null), Ct));
        (await _host.AsCallerAsync(admin, s => profiles(s).AddZoneAsync(draft.Data.Profile.Id, new AddZoneRequest("Snake B", "Queue", level, Rect(50, 10, 20, 10)), Ct)))
            .HasErrors.Should().BeFalse();

        var registered = await _host.AsCallerAsync(admin, s => Devices(s).RegisterAsync(Register("S-9", level, 60, 15, "Snake B"), Ct));
        registered.HasErrors.Should().BeFalse("a device can be registered while its zone is still being drawn");
        var calibration = await _host.AsCallerAsync(admin, s => Devices(s).RecordCalibrationAsync(registered.Data.Device.Id, Calibration(99), Ct));

        calibration.ErrorMessages.Single().Should().Contain("published zone profile");
        (await _host.ReadAsync<long>("SELECT count(*) FROM device_calibration WHERE device_id = @id", registered.Data.Device.Id)).Should().Be(0);
        (await _host.AsCallerAsync(admin, s => Devices(s).RemoveAsync(registered.Data.Device.Id, Ct))).Data.Should().BeTrue("never calibrated, so it can be removed");
        (await _host.AsCallerAsync(admin, s => Devices(s).GetAsync(registered.Data.Device.Id, Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(admin, s => Devices(s).RegisterAsync(Register("S-9", level, 60, 15, "Snake B"), Ct))).HasErrors.Should().BeFalse("a removed code is free again");
    }

    [Fact]
    public async Task Registry_Should_ShowNothingAndRegisterNothing_When_TheCallerHasNoSiteAccess()
    {
        var (admin, level) = await SiteAsync("DVC");
        var device = (await _host.AsCallerAsync(admin, s => Devices(s).RegisterAsync(Register("S-1", level), Ct))).Data.Device;
        var officer = await _host.CreateUserAsync("it.dev.officer", roles: [RoleCodes.BorderShiftSupervisor]);

        (await _host.AsCallerAsync(officer, s => Devices(s).GetAsync(device.Id, Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(officer, s => Devices(s).SearchAsync(new Ariva.Core.Domain.Criteria.DeviceCriteria { SiteCode = "DVC" }, Ct))).Data.TotalCount.Should().Be(0);
        (await _host.AsCallerAsync(officer, s => Devices(s).RegisterAsync(Register("S-2", level), Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(officer, s => Devices(s).RotateCredentialAsync(device.Id, Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(admin, s => Devices(s).SearchAsync(new Ariva.Core.Domain.Criteria.DeviceCriteria { SiteCode = "DVC", State = "Commissioning" }, Ct))).Data.TotalCount.Should().Be(1);
        (await _host.AsCallerAsync(admin, s => Devices(s).SearchAsync(new Ariva.Core.Domain.Criteria.DeviceCriteria { State = "1" }, Ct))).ErrorMessages.Should().Equal(DeviceErrors.UnknownState);

        var (_, otherLevel) = await SiteAsync("DVE");
        (await _host.AsCallerAsync(admin, s => Devices(s).MoveAsync(device.Id, new DevicePlacement(otherLevel, 20, 16, 5, 0, "Snake A"), Ct)))
            .ErrorMessages.Should().ContainSingle().Which.Should().Be(TopologyErrors.NotFound, "a device stays in its site, even for an all-sites administrator");
        (await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcTopology>().DeleteLevelAsync(level, Ct)))
            .ErrorMessages.Should().ContainSingle().Which.Should().Be(TopologyErrors.HasChildren, "a level with a live device is not deleted");
        (await _host.AsCallerAsync(admin, s => Devices(s).RegisterAsync(Register("S-1\n", level), Ct))).HasErrors.Should().BeTrue("a code with a trailing line break is not a code");
    }

    [Fact]
    public async Task Register_Should_GiveOneDeviceAndOneDuplicate_When_TwoRegisterTheSameCodeAtOnce()
    {
        var (admin, level) = await SiteAsync("DVD");

        var results = await Task.WhenAll(
            _host.AsCallerAsync(admin, s => Devices(s).RegisterAsync(Register("S-7", level), Ct)),
            _host.AsCallerAsync(admin, s => Devices(s).RegisterAsync(Register("S-7", level, 22), Ct)));

        results.Count(r => !r.HasErrors).Should().Be(1);
        results.Single(r => r.HasErrors).ErrorMessages.Should().Equal(TopologyErrors.Duplicate);
        (await _host.ReadAsync<long>("SELECT count(*) FROM device WHERE site_code = 'DVD' AND code = 'S-7'")).Should().Be(1);
    }

    [Fact]
    public async Task Register_Should_RefuseATransportIngestDoesNotSupport()
    {
        // A device on a transport Ingest has no listener for could never send data: refused at registration and on update.
        var (admin, level) = await SiteAsync("DVT");
        RegisterDeviceRequest On(string code, string transport) =>
            new(code, "StereoVision", "PC2SE", transport, "Xovis", "Ntp", new DevicePlacement(level, 20, 16, 5, 0, "Snake A"));

        foreach (var transport in new[] { "RestPull", "WebSocket", "TcpOrUdp", "FileDrop", "OnvifProfileM" })
        {
            var refused = await _host.AsCallerAsync(admin, s => Devices(s).RegisterAsync(On("T-" + transport, transport), Ct));
            refused.ErrorMessages.Should().Equal(DeviceErrors.UnsupportedTransport);
        }

        var push = await _host.AsCallerAsync(admin, s => Devices(s).RegisterAsync(On("T-1", "HttpsPush"), Ct));
        push.HasErrors.Should().BeFalse(string.Join(", ", push.ErrorMessages ?? []));
        var moved = await _host.AsCallerAsync(admin, s => Devices(s).UpdateAsync(push.Data.Device.Id, new UpdateDeviceRequest("PC2SE", "OnvifProfileM", "Xovis", "Ntp"), Ct));
        moved.ErrorMessages.Should().Equal(DeviceErrors.UnsupportedTransport);
        (await _host.ReadAsync<long>("SELECT count(*) FROM device WHERE site_code = 'DVT'")).Should().Be(1);
    }

    [Fact]
    public async Task Register_Should_TakeAShippedMappingForTheDeclarativeDialectOnly()
    {
        var (admin, level) = await SiteAsync("DVM");
        RegisterDeviceRequest Lidar(string code, string dialect, string mapping) =>
            new(code, "Lidar", "Gemini", "Mqtt", dialect, "Ntp", new DevicePlacement(level, 20, 16, 6, 0, "Snake A"), mapping);

        var unknown = await _host.AsCallerAsync(admin, s => Devices(s).RegisterAsync(Lidar("L-1", "Declarative", "made-up-v1"), Ct));
        var missing = await _host.AsCallerAsync(admin, s => Devices(s).RegisterAsync(Lidar("L-2", "Declarative", null), Ct));
        var stray = await _host.AsCallerAsync(admin, s => Devices(s).RegisterAsync(Lidar("L-3", "Canonical", "ouster-detect-v1"), Ct));
        var ok = await _host.AsCallerAsync(admin, s => Devices(s).RegisterAsync(Lidar("L-4", "Declarative", "ouster-detect-v1"), Ct));

        new[] { unknown, missing, stray }.Should().OnlyContain(r => r.ErrorMessages.SequenceEqual(new[] { DeviceErrors.UnknownMapping }));
        ok.HasErrors.Should().BeFalse(string.Join(", ", ok.ErrorMessages ?? []));
        ok.Data.Device.MappingName.Should().Be("ouster-detect-v1");
        (await _host.ReadAsync<string>("SELECT mapping_name FROM device WHERE site_code = 'DVM' AND code = 'L-4'")).Should().Be("ouster-detect-v1");
        var record = await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcDeviceGateway>().FindByPrefixAsync(ok.Data.Device.CredentialPrefix, Ct));
        (record.Dialect, record.MappingName, record.Transport).Should().Be(("Declarative", "ouster-detect-v1", "Mqtt"));

        var back = await _host.AsCallerAsync(admin, s => Devices(s).UpdateAsync(ok.Data.Device.Id, new UpdateDeviceRequest("Gemini", "Mqtt", "Canonical", "Ntp"), Ct));
        back.Data.MappingName.Should().BeNull();
        var sql = async () => await _host.ReadAsync<int>("UPDATE device SET mapping_name = 'ouster-detect-v1' WHERE site_code = 'DVM' AND code = 'L-4' RETURNING 1");
        await sql.Should().ThrowAsync<PostgresException>("the database keeps a mapping only with the declarative dialect");
    }

    [Fact]
    public async Task Gateway_Should_FindTheDeviceByPrefixAndForgetItAtOnce_When_TheCredentialChanges()
    {
        var (admin, level) = await SiteAsync("DVF");
        var gateway = (IServiceProvider s) => s.GetRequiredService<ISvcDeviceGateway>();
        var registered = (await _host.AsCallerAsync(admin, s => Devices(s).RegisterAsync(Register("S-1", level), Ct))).Data;
        var prefix = registered.Credential[..DeviceCredentials.PrefixLength];

        var found = await _host.AsCallerAsync(null, s => gateway(s).FindByPrefixAsync(prefix, Ct));
        found.Code.Should().Be("S-1");
        found.QueueZoneName.Should().Be("Snake A");
        DeviceCredentials.Matches(registered.Credential, found.CredentialHash).Should().BeTrue();

        var access = await _host.AsCallerAsync(admin, s => Devices(s).SetAccessAsync(registered.Device.Id, new SetDeviceAccessRequest(["10.20.0.0/24"], ""), Ct));
        access.Data.AllowedSources.Should().Equal("10.20.0.0/24");
        (await _host.AsCallerAsync(null, s => gateway(s).FindByPrefixAsync(prefix, Ct))).AllowedSources.Should().Equal(["10.20.0.0/24"], "the change evicted the cached copy");
        (await _host.AsCallerAsync(admin, s => Devices(s).SetAccessAsync(registered.Device.Id, new SetDeviceAccessRequest(["10.20.0.1/24"], ""), Ct)))
            .HasErrors.Should().BeTrue("host bits set");

        var rotated = (await _host.AsCallerAsync(admin, s => Devices(s).RotateCredentialAsync(registered.Device.Id, Ct))).Data;
        (await _host.AsCallerAsync(null, s => gateway(s).FindByPrefixAsync(prefix, Ct))).Should().BeNull("the old credential is forgotten after the rotation commits");
        (await _host.AsCallerAsync(null, s => gateway(s).FindByPrefixAsync(rotated.Credential[..DeviceCredentials.PrefixLength], Ct))).Code.Should().Be("S-1");

        var zone = await _host.AsCallerAsync(null, s => gateway(s).PublishedZoneAsync("DVF", "Snake A", Ct));
        zone.Data.Version.Should().Be(1);
        zone.Data.Zones.Select(z => z.Name).Should().Equal("Overflow A", "Snake A");
        zone.Data.Lines.Select(l => l.Name).Should().Equal("Entry A", "Exit A", "Overflow entry A");
        (await _host.AsCallerAsync(null, s => gateway(s).PublishedZoneAsync("DVF", "Snake Z", Ct))).HasErrors.Should().BeTrue();

        await _host.AsCallerAsync(admin, s => Devices(s).RetireAsync(registered.Device.Id, Ct));
        (await _host.AsCallerAsync(null, s => gateway(s).FindByPrefixAsync(rotated.Credential[..DeviceCredentials.PrefixLength], Ct))).Should().BeNull("retired");
    }
}
