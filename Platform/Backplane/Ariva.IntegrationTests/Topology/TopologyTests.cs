using Ariva.Core;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Services.Administration;
using Ariva.Core.Services.Topology;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.IntegrationTests.Topology;

/// <summary>
/// ARV-014 against PostgreSQL with script 0008: the topology tree is created through the service, site codes flow down,
/// another site's records answer NotFound, soft-deleted codes can be reused, deletes refuse live children, and every
/// change is audited.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TopologyTests(PostgresFixture fixture) : IAsyncDisposable
{
    private readonly AccountsHost _host = new(fixture, database: TestDatabase.Administration);

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ISvcTopology Topology(IServiceProvider s) => s.GetRequiredService<ISvcTopology>();

    private async Task<Guid> AdminAsync(string userName)
    {
        var admin = await _host.CreateUserAsync(userName, roles: [RoleCodes.SystemAdministrator]);
        await _host.ReadAsync<int>("UPDATE \"user\" SET all_sites = true WHERE id = @id RETURNING 1", admin);
        return admin;
    }

    [Fact]
    public async Task Tree_Should_BeCreatedScopedAndAudited_When_AnAdministratorBuildsIt()
    {
        var admin = await AdminAsync("it.topo.admin");
        foreach (var code in new[] { "TPA", "TPB" })
            await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest(code, code), Ct));
        var officer = await _host.CreateUserAsync("it.topo.officer", roles: [RoleCodes.BorderShiftSupervisor]);
        await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcUsers>().SetSitesAsync(officer, new SiteAccessRequest(false, ["TPA"]), Ct));

        var airport = await _host.AsCallerAsync(admin, s => Topology(s).CreateAirportAsync(new CreateAirportRequest("TPX", null, "Topology Test", "Asia/Amman"), Ct));
        airport.HasErrors.Should().BeFalse(string.Join(", ", airport.ErrorMessages ?? []));
        var t1 = await _host.AsCallerAsync(admin, s => Topology(s).CreateTerminalAsync(new CreateTerminalRequest(airport.Data.Id, "T1", "Terminal 1", "TPA"), Ct));
        var t2 = await _host.AsCallerAsync(admin, s => Topology(s).CreateTerminalAsync(new CreateTerminalRequest(airport.Data.Id, "T2", "Terminal 2", "TPB"), Ct));
        var level = await _host.AsCallerAsync(admin, s => Topology(s).CreateLevelAsync(new CreateLevelRequest(t1.Data.Id, "L0", "Arrivals", 0, 300, 120), Ct));
        var checkpoint = await _host.AsCallerAsync(admin, s => Topology(s).CreateCheckpointAsync(new CreateCheckpointRequest(level.Data.Id, "IMM", "Immigration", "Immigration"), Ct));
        var desk = await _host.AsCallerAsync(admin, s => Topology(s).CreateDeskAsync(new CreateDeskRequest(checkpoint.Data.Id, "D01", null, "Desk", ["CIT"]), Ct));

        desk.HasErrors.Should().BeFalse(string.Join(", ", desk.ErrorMessages ?? []));
        desk.Data.SiteCode.Should().Be("TPA");
        (await _host.ReadAsync<string>("SELECT site_code FROM desk WHERE id = @id", desk.Data.Id)).Should().Be("TPA");

        (await _host.AsCallerAsync(officer, s => Topology(s).GetDeskAsync(desk.Data.Id, Ct))).HasErrors.Should().BeFalse("inside its site");
        (await _host.AsCallerAsync(officer, s => Topology(s).GetTerminalAsync(t2.Data.Id, Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(officer, s => Topology(s).SearchTerminalsAsync(new TopologyCriteria { ParentId = airport.Data.Id }, Ct)))
            .Data.Data.Select(t => t.Code).Should().Equal("T1");
        (await _host.AsCallerAsync(officer, s => Topology(s).CreateLevelAsync(new CreateLevelRequest(t2.Data.Id, "L0", "x", 0, 10, 10), Ct)))
            .ErrorMessages.Should().Equal(new[] { TopologyErrors.NotFound }, "a parent in another site answers like an unknown one");
        (await _host.AsCallerAsync(officer, s => Topology(s).CreateAirportAsync(new CreateAirportRequest("TPY", null, "x", "Asia/Amman"), Ct)))
            .ErrorMessages.Should().Equal(TopologyErrors.DeploymentWide);

        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE action IN ('Airport.Created', 'Terminal.Created', 'Level.Created', 'Checkpoint.Created', 'Desk.Created') AND target_name LIKE 'TPX%'"))
            .Should().Be(6, "airport, two terminals, level, checkpoint and desk, named by their path from the airport");

        // A checkpoint code is unique across the site, not only the level (ARV-055): desks are known by site/checkpoint/desk.
        var upper = await _host.AsCallerAsync(admin, s => Topology(s).CreateLevelAsync(new CreateLevelRequest(t1.Data.Id, "L1", "Departures", 1, 300, 120), Ct));
        (await _host.AsCallerAsync(admin, s => Topology(s).CreateCheckpointAsync(new CreateCheckpointRequest(upper.Data.Id, "IMM", "Check-in", "CheckIn"), Ct)))
            .ErrorMessages.Should().Equal(TopologyErrors.Duplicate);
        var otherSite = await _host.AsCallerAsync(admin, s => Topology(s).CreateLevelAsync(new CreateLevelRequest(t2.Data.Id, "L0", "Arrivals", 0, 300, 120), Ct));
        (await _host.AsCallerAsync(admin, s => Topology(s).CreateCheckpointAsync(new CreateCheckpointRequest(otherSite.Data.Id, "IMM", "Immigration", "Immigration"), Ct)))
            .HasErrors.Should().BeFalse("another site may use the code");
    }

    [Fact]
    public async Task Delete_Should_RefuseLiveChildrenAndFreeTheCode_When_Deleted()
    {
        var admin = await AdminAsync("it.topo.deleter");
        await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest("TPC", "TPC"), Ct));
        var airport = await _host.AsCallerAsync(admin, s => Topology(s).CreateAirportAsync(new CreateAirportRequest("TPZ", null, "Delete Test", "Asia/Dubai"), Ct));
        var terminal = await _host.AsCallerAsync(admin, s => Topology(s).CreateTerminalAsync(new CreateTerminalRequest(airport.Data.Id, "T1", "T1", "TPC"), Ct));
        var level = await _host.AsCallerAsync(admin, s => Topology(s).CreateLevelAsync(new CreateLevelRequest(terminal.Data.Id, "L0", "Ground", 0, 50, 50), Ct));

        (await _host.AsCallerAsync(admin, s => Topology(s).DeleteTerminalAsync(terminal.Data.Id, Ct))).ErrorMessages.Should().Equal(TopologyErrors.HasChildren);
        (await _host.AsCallerAsync(admin, s => Topology(s).DeleteLevelAsync(level.Data.Id, Ct))).HasErrors.Should().BeFalse();
        (await _host.AsCallerAsync(admin, s => Topology(s).GetLevelAsync(level.Data.Id, Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.ReadAsync<DateTime?>("SELECT deleted_on FROM level WHERE id = @id", level.Data.Id)).Should().NotBeNull("deletes are soft");

        var again = await _host.AsCallerAsync(admin, s => Topology(s).CreateLevelAsync(new CreateLevelRequest(terminal.Data.Id, "L0", "Ground again", 0, 50, 50), Ct));
        again.HasErrors.Should().BeFalse("the code and floor of a deleted level are free");
        (await _host.AsCallerAsync(admin, s => Topology(s).CreateLevelAsync(new CreateLevelRequest(terminal.Data.Id, "L0", "Twice", 1, 50, 50), Ct)))
            .ErrorMessages.Should().Equal(TopologyErrors.Duplicate);
    }

    [Fact]
    public async Task RangesAndMappings_Should_BeAtomicUniqueAndResolvable_When_Created()
    {
        var admin = await AdminAsync("it.topo.mapper");
        await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest("TPM", "TPM"), Ct));
        var airport = await _host.AsCallerAsync(admin, s => Topology(s).CreateAirportAsync(new CreateAirportRequest("TPM", null, "Mapping Test", "Asia/Amman"), Ct));
        var terminal = await _host.AsCallerAsync(admin, s => Topology(s).CreateTerminalAsync(new CreateTerminalRequest(airport.Data.Id, "T1", "T1", "TPM"), Ct));
        var level = await _host.AsCallerAsync(admin, s => Topology(s).CreateLevelAsync(new CreateLevelRequest(terminal.Data.Id, "L0", "L0", 0, 50, 50), Ct));
        var checkpoint = await _host.AsCallerAsync(admin, s => Topology(s).CreateCheckpointAsync(new CreateCheckpointRequest(level.Data.Id, "IMM", "Immigration", "Immigration"), Ct));

        var range = await _host.AsCallerAsync(admin, s => Topology(s).CreateDeskRangeAsync(new CreateDeskRangeRequest(checkpoint.Data.Id, "D", 1, 22, 2, "Desk", ["CIT"]), Ct));
        var overlap = await _host.AsCallerAsync(admin, s => Topology(s).CreateDeskRangeAsync(new CreateDeskRangeRequest(checkpoint.Data.Id, "D", 20, 30, 2, "Desk", ["CIT"]), Ct));

        range.Data.Should().HaveCount(22);
        overlap.ErrorMessages.Should().Equal(TopologyErrors.Duplicate);
        (await _host.ReadAsync<long>("SELECT count(*) FROM desk WHERE checkpoint_id = @id", checkpoint.Data.Id)).Should().Be(22, "the refused range wrote nothing");

        var mappings = (IServiceProvider s) => s.GetRequiredService<ISvcDeskCodeMappings>();
        var mapped = await _host.AsCallerAsync(admin, s => mappings(s).CreateAsync(new CreateDeskCodeMappingRequest("Aman", "a-07", range.Data[6].Id), Ct));
        mapped.HasErrors.Should().BeFalse(string.Join(", ", mapped.ErrorMessages ?? []));
        (await _host.AsCallerAsync(admin, s => mappings(s).CreateAsync(new CreateDeskCodeMappingRequest("Aman", "A-07", range.Data[7].Id), Ct)))
            .ErrorMessages.Should().Equal(TopologyErrors.Duplicate);
        (await _host.AsCallerAsync(null, s => mappings(s).ResolveAsync(Ariva.Core.Domain.Enums.ExternalSystem.Aman, "TPM", "A-07", Ct))).Should().Be(range.Data[6].Id);
        (await _host.AsCallerAsync(null, s => mappings(s).ResolveAsync(Ariva.Core.Domain.Enums.ExternalSystem.Aodb, "TPM", "A-07", Ct))).Should().BeNull();
    }
}

