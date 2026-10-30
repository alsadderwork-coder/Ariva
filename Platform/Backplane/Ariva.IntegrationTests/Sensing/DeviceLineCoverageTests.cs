using System.Globalization;
using Ariva.Core;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services;
using Ariva.Core.Services.Administration;
using Ariva.Core.Services.Sensing;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Services.Sensing;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Fluentx;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ariva.IntegrationTests.Sensing;

/// <summary>
/// ARV-259e4 (ADR-0027) against PostgreSQL with script 0066: a device declares the entry and exit lines of its queue zone it
/// counts (All by default, Listed, None), never recommissioned, audited, raising CoverageChanged; only entry and exit lines of
/// its zone are taken, a line its footprint does not reach is a warning; another site's device answers NotFound and a retired
/// one Conflict; the device health answer lists each published zone's lines no commissioned device declares; a publish that
/// drops a listed line warns, and the device list names the line.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DeviceLineCoverageTests(PostgresFixture fixture) : IAsyncDisposable
{
    private readonly AccountsHost _host = new(fixture, database: TestDatabase.Administration);

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ISvcDevices Devices(IServiceProvider s) => s.GetRequiredService<ISvcDevices>();

    private static ISvcZoneProfiles Profiles(IServiceProvider s) => s.GetRequiredService<ISvcZoneProfiles>();

    private static string Rect(double x, double y, double w, double h) =>
        string.Create(CultureInfo.InvariantCulture, $"{x} {y},{x + w} {y},{x + w} {y + h},{x} {y + h}");

    /// <summary>
    /// A site with one level and a published profile: queue "Snake A" (10..34 x 10..22) with Entry A on its west edge
    /// (x 10, y 12..16), Exit A on its north edge (x 30..34, y 22), and an overflow band below it with its own entry line.
    /// </summary>
    private async Task<(Guid Admin, Guid Level)> SiteAsync(string site)
    {
        var admin = await _host.CreateUserAsync($"it.cov.{site.ToLowerInvariant()}", roles: [RoleCodes.SystemAdministrator]);
        await _host.ReadAsync<int>("UPDATE \"user\" SET all_sites = true WHERE id = @id RETURNING 1", admin);
        await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest(site, site), Ct));
        var topology = (IServiceProvider s) => s.GetRequiredService<ISvcTopology>();
        var airport = await _host.AsCallerAsync(admin, s => topology(s).CreateAirportAsync(new CreateAirportRequest(site, null, "Coverage", "Asia/Dubai"), Ct));
        var terminal = await _host.AsCallerAsync(admin, s => topology(s).CreateTerminalAsync(new CreateTerminalRequest(airport.Data.Id, "T1", "T1", site), Ct));
        var level = (await _host.AsCallerAsync(admin, s => topology(s).CreateLevelAsync(new CreateLevelRequest(terminal.Data.Id, "L0", "Arrivals", 0, 100, 50), Ct))).Data.Id;

        var draft = (await _host.AsCallerAsync(admin, s => Profiles(s).CreateDraftAsync(new CreateZoneProfileDraftRequest(site, "Arrivals"), Ct))).Data.Profile.Id;
        var queue = (await _host.AsCallerAsync(admin, s => Profiles(s).AddZoneAsync(draft, new AddZoneRequest("Snake A", "Queue", level, Rect(10, 10, 24, 12)), Ct))).Data.Id;
        var overflow = (await _host.AsCallerAsync(admin, s => Profiles(s).AddZoneAsync(draft, new AddZoneRequest("Overflow A", "Overflow", level, Rect(10, 4, 24, 6), queue), Ct))).Data.Id;
        await _host.AsCallerAsync(admin, s => Profiles(s).AddLineAsync(draft, new AddLineRequest("Overflow entry A", "OverflowEntry", level, 10, 5, 10, 9, overflow), Ct));
        await _host.AsCallerAsync(admin, s => Profiles(s).AddLineAsync(draft, new AddLineRequest("Entry A", "Entry", level, 10, 12, 10, 16, queue), Ct));
        await _host.AsCallerAsync(admin, s => Profiles(s).AddLineAsync(draft, new AddLineRequest("Exit A", "Exit", level, 30, 22, 34, 22, queue), Ct));
        (await PublishAsync(admin, draft)).HasErrors.Should().BeFalse();
        return (admin, level);
    }

    private async Task<Result<ZoneProfileSummaryViewModel>> PublishAsync(Guid admin, Guid draft)
    {
        var hash = (await _host.AsCallerAsync(admin, s => Profiles(s).ValidateAsync(draft, Ct))).Data.GeometryHash;
        return await _host.AsCallerAsync(admin, s => Profiles(s).PublishAsync(draft, new PublishZoneProfileRequest(hash), Ct));
    }

    /// <summary>A StereoVision device 5 m up (a 10 x 10 m footprint) at (x, y), calibrated, so commissioned.</summary>
    private async Task<Guid> OnlineDeviceAsync(Guid admin, Guid level, string code, double x, double y)
    {
        var registered = await _host.AsCallerAsync(admin, s => Devices(s).RegisterAsync(
            new RegisterDeviceRequest(code, "StereoVision", "PC2SE", "HttpsPush", "Xovis", "Ntp", new DevicePlacement(level, x, y, 5, 0, "Snake A")), Ct));
        registered.HasErrors.Should().BeFalse(string.Join(", ", registered.ErrorMessages ?? []));
        var id = registered.Data.Device.Id;
        (await _host.AsCallerAsync(admin, s => Devices(s).RecordCalibrationAsync(id, new RecordCalibrationRequest("ManualCountTally", 200, 97, 0.4), Ct)))
            .Data.DeviceState.Should().Be("Online");
        return id;
    }

    private Task<Result<DeviceCoverageViewModel>> CoverAsync(Guid admin, Guid id, string mode, params string[] lines) =>
        _host.AsCallerAsync(admin, s => Devices(s).SetCoverageAsync(id, new SetDeviceCoverageRequest(mode, lines), Ct));

    [Fact]
    public async Task Coverage_Should_KeepTheZonesLinesAndWarnOfOneOutOfReach_When_Declared()
    {
        var (admin, level) = await SiteAsync("DLA");
        // At (28, 18) the footprint is 23..33 x 13..23: it reaches Exit A, not Entry A.
        var id = await OnlineDeviceAsync(admin, level, "S-1", 28, 18);
        (await _host.AsCallerAsync(admin, s => Devices(s).GetAsync(id, Ct))).Data.LineCoverage.Should().Be("All", "a device registered without a declaration counts every line");

        var listed = await CoverAsync(admin, id, "Listed", "Exit A", " Entry A ");

        listed.HasErrors.Should().BeFalse(string.Join(", ", listed.ErrorMessages ?? []));
        listed.Data.Device.LineCoverage.Should().Be("Listed");
        listed.Data.Device.CoveredLines.Should().Equal("Entry A", "Exit A");
        listed.Data.Device.LinesNotInZone.Should().BeEmpty();
        listed.Data.Device.State.Should().Be("Online", "a declaration never recommissions the device");
        listed.Data.Warnings.Should().ContainSingle().Which.Should().Contain("does not reach line Entry A");
        (await _host.ReadAsync<string>("SELECT line_coverage || ':' || covered_lines FROM device WHERE id = @id", id)).Should().Be("Listed:Entry A\nExit A");
        (await _host.ReadAsync<string>("SELECT after_summary FROM audit_entry WHERE action = 'Device.CoverageChanged' AND target_id = @id", id))
            .Should().Contain("coverage=Listed; lines=Entry A,Exit A");
        (await _host.ReadAsync<long>("SELECT count(*) FROM outbox_message WHERE topic = 'ariva.device.registry-changed.v1' AND message_key = @id::text AND payload->>'change' = 'CoverageChanged'", id))
            .Should().Be(1);

        (await CoverAsync(admin, id, "Listed", "Overflow entry A")).ErrorMessages.Should().Equal([DeviceErrors.NotAZoneLine], "an overflow entry line is not an entry or exit line");
        (await CoverAsync(admin, id, "Listed", "entry a")).ErrorMessages.Should().Equal([DeviceErrors.NotAZoneLine], "names compare ordinally");
        (await CoverAsync(admin, id, "Listed", "Nowhere'; DROP TABLE device; --")).ErrorMessages.Should().Equal([DeviceErrors.NotAZoneLine]);
        (await CoverAsync(admin, id, "Listed", [.. Enumerable.Range(0, 17).Select(i => $"Line {i}")])).ErrorMessages.Should().ContainSingle().Which.Should().Contain("1 to 16");
        (await CoverAsync(admin, id, "Listed", "Entry\u202EA")).ErrorMessages.Should().ContainSingle().Which.Should().NotContain("\u202E", "the name is never repeated");
        (await CoverAsync(admin, id, "1")).ErrorMessages.Should().Equal(DeviceErrors.UnknownCoverage);
        (await CoverAsync(admin, id, "listed", "Exit A")).ErrorMessages.Should().Equal(DeviceErrors.UnknownCoverage);
        (await CoverAsync(admin, id, "None", "Exit A")).HasErrors.Should().BeTrue("None names no line");
        (await _host.ReadAsync<string>("SELECT covered_lines FROM device WHERE id = @id", id)).Should().Be("Entry A\nExit A", "a refused declaration changes nothing");

        (await CoverAsync(admin, id, "None")).Data.Device.LineCoverage.Should().Be("None");
        (await CoverAsync(admin, id, "None")).Data.Warnings.Should().BeEmpty();
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE action = 'Device.CoverageChanged' AND target_id = @id", id))
            .Should().Be(2, "the same declaration again changes and audits nothing");

        var listedWithoutLines = () => _host.ReadAsync<int>("UPDATE device SET line_coverage = 'Listed' WHERE id = @id RETURNING 1", id);
        (await listedWithoutLines.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
        var emptyName = () => _host.ReadAsync<int>("UPDATE device SET line_coverage = 'Listed', covered_lines = E'Entry A\\n' WHERE id = @id RETURNING 1", id);
        (await emptyName.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
        var unknownKind = () => _host.ReadAsync<int>("UPDATE device SET line_coverage = 'Some' WHERE id = @id RETURNING 1", id);
        (await unknownKind.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
    }

    [Fact]
    public async Task Coverage_Should_AnswerNotFoundOutsideTheCallersSitesAndConflictWhenRetired()
    {
        var (admin, level) = await SiteAsync("DLB");
        var id = await OnlineDeviceAsync(admin, level, "S-1", 28, 18);
        var officer = await _host.CreateUserAsync("it.cov.officer", roles: [RoleCodes.BorderShiftSupervisor]);

        (await CoverAsync(officer, id, "Listed", "Exit A")).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await CoverAsync(admin, Guid.NewGuid(), "None")).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.ReadAsync<string>("SELECT line_coverage FROM device WHERE id = @id", id)).Should().Be("All");

        await _host.AsCallerAsync(admin, s => Devices(s).RetireAsync(id, Ct));
        (await CoverAsync(admin, id, "None")).ErrorMessages.Should().Equal(DeviceErrors.Retired);
    }

    [Fact]
    public async Task Health_Should_ListTheLinesNoCommissionedDeviceDeclares()
    {
        var (admin, level) = await SiteAsync("DLC");
        async Task<IReadOnlyList<string>> Uncovered() =>
            (await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcDeviceHealth>().OverviewAsync("DLC", Ct))).Data.UncoveredLines
            .SingleOrDefault(z => z.QueueZoneName == "Snake A")?.Lines ?? [];

        (await Uncovered()).Should().Equal(["Entry A", "Exit A"], "no device: every line of the published zone");

        var registered = await _host.AsCallerAsync(admin, s => Devices(s).RegisterAsync(
            new RegisterDeviceRequest("S-1", "StereoVision", "PC2SE", "HttpsPush", "Xovis", "Ntp", new DevicePlacement(level, 28, 18, 5, 0, "Snake A")), Ct));
        (await CoverAsync(admin, registered.Data.Device.Id, "Listed", "Exit A")).HasErrors.Should().BeFalse();
        (await Uncovered()).Should().Equal(["Entry A", "Exit A"], "a device still commissioning covers nothing yet");
        (await _host.AsCallerAsync(admin, s => Devices(s).RecordCalibrationAsync(registered.Data.Device.Id, new RecordCalibrationRequest("ManualCountTally", 200, 97, 0.4), Ct)))
            .Data.Passed.Should().BeTrue();
        (await Uncovered()).Should().Equal(["Entry A"]);

        var none = await OnlineDeviceAsync(admin, level, "S-2", 15, 14);
        (await CoverAsync(admin, none, "None")).HasErrors.Should().BeFalse();
        (await Uncovered()).Should().Equal(["Entry A"], "a device that counts no queue line covers none");

        await OnlineDeviceAsync(admin, level, "S-3", 15, 14);
        (await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcDeviceHealth>().OverviewAsync("DLC", Ct))).Data.UncoveredLines
            .Should().BeEmpty("a device that declares nothing counts every line");
    }

    [Fact]
    public async Task Publish_Should_WarnOfADeviceListingALineItDropped_When_TheProfileChanges()
    {
        var (admin, level) = await SiteAsync("DLD");
        var id = await OnlineDeviceAsync(admin, level, "S-1", 28, 18);
        (await CoverAsync(admin, id, "Listed", "Entry A", "Exit A")).HasErrors.Should().BeFalse();

        // The next version renames the exit line: Exit A goes, Exit B takes its place.
        var draft = (await _host.AsCallerAsync(admin, s => Profiles(s).CreateDraftAsync(new CreateZoneProfileDraftRequest("DLD", null), Ct))).Data;
        var exit = draft.Lines.Single(l => l.Name == "Exit A");
        (await _host.AsCallerAsync(admin, s => Profiles(s).RemoveLineAsync(draft.Profile.Id, exit.Id, Ct))).Data.Should().BeTrue();
        (await _host.AsCallerAsync(admin, s => Profiles(s).AddLineAsync(draft.Profile.Id, new AddLineRequest("Exit B", "Exit", level, 30, 22, 34, 22, exit.ZoneId), Ct)))
            .HasErrors.Should().BeFalse();

        var published = await PublishAsync(admin, draft.Profile.Id);

        published.HasErrors.Should().BeFalse("a dropped line is a warning, never a refusal");
        published.Data.Warnings.Should().ContainSingle().Which.Should().Contain("Device S-1").And.Contain("(Exit A)");
        var listed = (await _host.AsCallerAsync(admin, s => Devices(s).SearchAsync(new DeviceCriteria { SiteCode = "DLD" }, Ct))).Data.Data.Single();
        listed.CoveredLines.Should().Equal("Entry A", "Exit A");
        listed.LinesNotInZone.Should().Equal(["Exit A"], "the device list names the line the publish dropped");
        (await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcDeviceHealth>().OverviewAsync("DLD", Ct))).Data.UncoveredLines
            .Single().Lines.Should().Equal(["Exit B"]);
        (await CoverAsync(admin, id, "Listed", "Exit A")).ErrorMessages.Should().Equal([DeviceErrors.NotAZoneLine], "a declaration now names the published lines");
        (await CoverAsync(admin, id, "Listed", "Entry A", "Exit B")).Data.Device.LinesNotInZone.Should().BeEmpty();
    }

    [Fact]
    public async Task Reads_Should_BeCutAtTheirBounds_When_ASiteHoldsMoreThanOneAnswerTakes()
    {
        // CWE-400: the published lines and the declarations are read with a bound in SQL (Take(bound + 1)); a cut read leaves out
        // the zone it reached part way, and the overview answers no uncovered line rather than one a device left out may cover.
        var (admin, level) = await SiteAsync("DLE");
        var first = await OnlineDeviceAsync(admin, level, "S-1", 28, 18);
        await OnlineDeviceAsync(admin, level, "S-2", 15, 14);
        (await CoverAsync(admin, first, "Listed", "Exit A")).HasErrors.Should().BeFalse();

        await _host.AsCallerAsync(admin, async s =>
        {
            var storage = s.GetRequiredService<IStorageProvider>();
            var profiles = () => storage.Query<ZoneProfile>().Where(p => p.SiteCode == "DLE");
            var whole = await PublishedZoneLines.LoadAsync(profiles(), storage.Query<Line>(), null, Ct, maxLines: 2);
            whole.Truncated.Should().BeFalse();
            whole.Zones.Single().Lines.Should().BeEquivalentTo(["Entry A", "Exit A"]);
            var cut = await PublishedZoneLines.LoadAsync(profiles(), storage.Query<Line>(), null, Ct, maxLines: 1);
            cut.Truncated.Should().BeTrue();
            cut.Zones.Should().BeEmpty("a zone the read reached part way is left out, never shown with fewer lines");
            (await PublishedZoneLines.LoadAsync(profiles(), storage.Query<Line>(), ["Nowhere"], Ct)).Zones.Should().BeEmpty("only the zones named are read");
            return 0;
        });

        var health = (IServiceProvider s) => (SvcDeviceHealth)s.GetRequiredService<ISvcDeviceHealth>();
        var bounded = await _host.AsCallerAsync(admin, s => health(s).OverviewAsync("DLE", 2, Ct));
        (bounded.Data.Truncated, bounded.Data.UncoveredLines.Count).Should().Be((false, 0), "S-2 declares every line");
        var over = await _host.AsCallerAsync(admin, s => health(s).OverviewAsync("DLE", 1, Ct));
        over.Data.Truncated.Should().BeTrue("two declarations, one read at most");
        over.Data.UncoveredLines.Should().BeEmpty("no line is called uncovered from a cut read");
    }
}
