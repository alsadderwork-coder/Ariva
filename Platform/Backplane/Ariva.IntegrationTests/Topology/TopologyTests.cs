using Ariva.Core;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
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

    /// <summary>
    /// ARV-281: two creates of one code (or a level's floor) at the same moment, one row per unique index of the topology
    /// (scripts 0008 and 0033). The first sends its row inside the request and holds its transaction open; the second passes
    /// the checks before its insert, which waits on the first one's index entry; once the first commits, the second answers
    /// the index's own error (409 at the API: Duplicate, or FloorTaken for a level's floor) and keeps nothing, its audit entry
    /// included. Before ARV-281 the rows were sent at the commit only, after the service had returned, so the second never
    /// waited and failed at the commit (500). Each row builds a site of its own. The desk range row overlaps two ranges of 30
    /// desks, more than one batch of inserts (25): the index refuses the second's first batch while its second batch is still
    /// queued, and only the promise not to commit keeps that queue from being sent again at the end (500).
    /// </summary>
    [Theory]
    [InlineData("TQA", "airport")]
    [InlineData("TQB", "terminal")]
    [InlineData("TQC", "level code")]
    [InlineData("TQE", "level floor")]
    [InlineData("TQF", "checkpoint code")]
    [InlineData("TQG", "checkpoint code across the site")]
    [InlineData("TQH", "desk code")]
    [InlineData("TQI", "desk range")]
    public async Task Create_Should_AnswerTheIndexErrorToTheSecond_When_TwoCreatesOfOneCodeMeet(string site, string index)
    {
        var admin = await AdminAsync($"it.topo.race.{site.ToLowerInvariant()}");
        await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest(site, site), Ct));
        var make = (Func<IServiceProvider, Task<Fluentx.Result<AirportViewModel>>>)(s => Topology(s).CreateAirportAsync(new CreateAirportRequest(site, null, "Race", "Asia/Amman"), Ct));
        if (index == "airport")
        {
            await RaceAsync(admin, site, "Airport.Created", make);
            (await _host.ReadAsync<long>("SELECT count(*) FROM airport WHERE iata_code = @secret", secret: site)).Should().Be(1);
            return;
        }

        var airport = (await _host.AsCallerAsync(admin, make)).Data;
        if (index == "terminal")
        {
            await RaceAsync(admin, site, "Terminal.Created", s => Topology(s).CreateTerminalAsync(new CreateTerminalRequest(airport.Id, "T1", "Terminal 1", site), Ct));
            (await _host.ReadAsync<long>("SELECT count(*) FROM terminal WHERE site_code = @secret", secret: site)).Should().Be(1);
            return;
        }

        var terminal = (await _host.AsCallerAsync(admin, s => Topology(s).CreateTerminalAsync(new CreateTerminalRequest(airport.Id, "T1", "Terminal 1", site), Ct))).Data;
        var attempt = 0;
        if (index is "level code" or "level floor")
        {
            // The same code on two floors, or two codes on one floor: only the index named refuses the second, with its own error.
            await RaceAsync(admin, site, "Level.Created", s => Topology(s).CreateLevelAsync(index == "level code"
                ? new CreateLevelRequest(terminal.Id, "L0", "Arrivals", attempt++, 300, 120)
                : new CreateLevelRequest(terminal.Id, $"L{attempt++}", "Arrivals", 0, 300, 120), Ct),
                index == "level code" ? TopologyErrors.Duplicate : TopologyErrors.FloorTaken);
            (await _host.ReadAsync<long>("SELECT count(*) FROM level WHERE site_code = @secret", secret: site)).Should().Be(1);
            return;
        }

        var level = (await _host.AsCallerAsync(admin, s => Topology(s).CreateLevelAsync(new CreateLevelRequest(terminal.Id, "L0", "Arrivals", 0, 300, 120), Ct))).Data;
        var upper = (await _host.AsCallerAsync(admin, s => Topology(s).CreateLevelAsync(new CreateLevelRequest(terminal.Id, "L1", "Departures", 1, 300, 120), Ct))).Data;
        if (index.StartsWith("checkpoint", StringComparison.Ordinal))
        {
            // Across the site: the same code on two levels, which only ux_checkpoint_site_code refuses.
            var levels = index == "checkpoint code" ? new[] { level.Id, level.Id } : [level.Id, upper.Id];
            await RaceAsync(admin, site, "Checkpoint.Created",
                s => Topology(s).CreateCheckpointAsync(new CreateCheckpointRequest(levels[attempt++], "IMM", "Immigration", "Immigration"), Ct));
            (await _host.ReadAsync<long>("SELECT count(*) FROM checkpoint WHERE site_code = @secret", secret: site)).Should().Be(1);
            return;
        }

        var checkpoint = (await _host.AsCallerAsync(admin, s => Topology(s).CreateCheckpointAsync(new CreateCheckpointRequest(level.Id, "IMM", "Immigration", "Immigration"), Ct))).Data;
        if (index == "desk range")
        {
            // R01 to R30, then R05 to R34: the second's first batch holds R05, which the first one's index entry refuses.
            var ranges = new[] { (From: 1, To: 30), (From: 5, To: 34) };
            var created = await RaceAsync(admin, site, "Desk.RangeCreated", s =>
            {
                var (from, to) = ranges[attempt++];
                return Topology(s).CreateDeskRangeAsync(new CreateDeskRangeRequest(checkpoint.Id, "R", from, to, 2, "Desk", ["CIT"]), Ct);
            });
            created.Should().HaveCount(30);
            (await _host.ReadAsync<long>("SELECT count(*) FROM desk WHERE site_code = @secret", secret: site)).Should().Be(30, "the refused range kept none of its desks");
            return;
        }

        await RaceAsync(admin, site, "Desk.Created", s => Topology(s).CreateDeskAsync(new CreateDeskRequest(checkpoint.Id, "D01", null, "Desk", ["CIT"]), Ct));
        (await _host.ReadAsync<long>("SELECT count(*) FROM desk WHERE site_code = @secret", secret: site)).Should().Be(1);
    }

    /// <summary>
    /// ARV-281: a level's floor number taken is its own refusal, not a code taken, whichever path finds it: the entity's check
    /// on a create, the service's check on an update (both 409 at the API), and the index under a race (the theory above).
    /// </summary>
    [Fact]
    public async Task Level_Should_AnswerFloorTaken_When_AnotherLiveLevelOfTheTerminalHasTheFloor()
    {
        var admin = await AdminAsync("it.topo.floors");
        await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest("TPF", "TPF"), Ct));
        var airport = (await _host.AsCallerAsync(admin, s => Topology(s).CreateAirportAsync(new CreateAirportRequest("TPF", null, "Floors", "Asia/Amman"), Ct))).Data;
        var terminal = (await _host.AsCallerAsync(admin, s => Topology(s).CreateTerminalAsync(new CreateTerminalRequest(airport.Id, "T1", "T1", "TPF"), Ct))).Data;
        (await _host.AsCallerAsync(admin, s => Topology(s).CreateLevelAsync(new CreateLevelRequest(terminal.Id, "L0", "Ground", 0, 50, 50), Ct))).HasErrors.Should().BeFalse();
        var upper = (await _host.AsCallerAsync(admin, s => Topology(s).CreateLevelAsync(new CreateLevelRequest(terminal.Id, "L1", "Upper", 1, 50, 50), Ct))).Data;

        (await _host.AsCallerAsync(admin, s => Topology(s).CreateLevelAsync(new CreateLevelRequest(terminal.Id, "L2", "Same floor", 0, 50, 50), Ct)))
            .ErrorMessages.Should().Equal(TopologyErrors.FloorTaken);
        (await _host.AsCallerAsync(admin, s => Topology(s).UpdateLevelAsync(upper.Id, new UpdateLevelRequest("Upper", 0, 50, 50), Ct)))
            .ErrorMessages.Should().Equal(TopologyErrors.FloorTaken);
        (await _host.AsCallerAsync(admin, s => Topology(s).CreateLevelAsync(new CreateLevelRequest(terminal.Id, "L1", "Same code", 2, 50, 50), Ct)))
            .ErrorMessages.Should().Equal(new[] { TopologyErrors.Duplicate }, "a code taken stays Duplicate");
        (await _host.ReadAsync<int>("SELECT floor_number FROM level WHERE id = @id", upper.Id)).Should().Be(1, "the refused update kept nothing");
    }

    /// <summary>
    /// ARV-281: only the topology's own unique indexes mean a code is taken. A unique violation of another index, or any other
    /// database error, is not turned into Duplicate: the create fails (500 at the API, without its text outside vm-local) and
    /// keeps nothing. A trigger on this test's database raises each error for the codes it names, and is dropped afterwards.
    /// </summary>
    [Theory]
    [InlineData("TXU", "Raise unique", "23505")]
    [InlineData("TXO", "Raise other", "P0001")]
    public async Task Create_Should_FailAndKeepNothing_When_TheDatabaseRefusesTheAirportForAnotherReason(string code, string name, string sqlState)
    {
        var admin = await AdminAsync($"it.topo.refused.{code.ToLowerInvariant()}");
        await _host.ReadAsync<int>("""
            DROP TRIGGER IF EXISTS it_refuse_airport ON airport;
            CREATE OR REPLACE FUNCTION it_refuse_airport() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.name = 'Raise unique' THEN
                    RAISE EXCEPTION 'refused by the test' USING ERRCODE = 'unique_violation', CONSTRAINT = 'ux_airport_elsewhere';
                END IF;
                RAISE EXCEPTION 'refused by the test';
            END $$;
            CREATE TRIGGER it_refuse_airport BEFORE INSERT ON airport FOR EACH ROW WHEN (NEW.iata_code IN ('TXU', 'TXO')) EXECUTE FUNCTION it_refuse_airport();
            SELECT 1
            """);
        try
        {
            var create = () => _host.AsCallerAsync(admin, s => Topology(s).CreateAirportAsync(new CreateAirportRequest(code, null, name, "Asia/Amman"), Ct));

            var thrown = await create.Should().ThrowAsync<global::NHibernate.Exceptions.GenericADOException>();
            thrown.Which.InnerException.Should().BeOfType<Npgsql.PostgresException>().Which.SqlState.Should().Be(sqlState);
        }
        finally
        {
            await _host.ReadAsync<int>("DROP TRIGGER IF EXISTS it_refuse_airport ON airport; DROP FUNCTION IF EXISTS it_refuse_airport(); SELECT 1");
        }

        (await _host.ReadAsync<long>("SELECT count(*) FROM airport WHERE iata_code IN ('TXU', 'TXO')")).Should().Be(0);
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE target_name IN ('TXU', 'TXO')")).Should().Be(0);
    }

    /// <summary>
    /// ARV-281: a zone profile draft needs no flush for this, since the create locks the site's row before it checks for a draft:
    /// two at once run one after the other, and the second answers DraftExists (409), not a 500.
    /// </summary>
    [Fact]
    public async Task CreateDraft_Should_AnswerDraftExistsToTheSecond_When_TwoMeet()
    {
        var admin = await AdminAsync("it.topo.drafts");
        await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest("TQD", "TQD"), Ct));

        var (_, second) = await ConcurrentCreates.RaceAsync(_host, admin,
            s => s.GetRequiredService<ISvcZoneProfiles>().CreateDraftAsync(new CreateZoneProfileDraftRequest("TQD", "Arrivals"), Ct));

        second.ErrorMessages.Should().Equal(ZoneProfileErrors.DraftExists);
        (await _host.ReadAsync<long>("SELECT count(*) FROM zone_profile WHERE site_code = 'TQD'")).Should().Be(1);
    }

    /// <summary>
    /// Races <paramref name="create"/> (<see cref="ConcurrentCreates"/>): the second answers <paramref name="error"/> and only the
    /// first's creation is audited (<paramref name="audited"/> once under <paramref name="site"/>); returns the first's record.
    /// </summary>
    private async Task<T> RaceAsync<T>(Guid admin, string site, string audited, Func<IServiceProvider, Task<Fluentx.Result<T>>> create,
        string error = TopologyErrors.Duplicate)
    {
        var (first, second) = await ConcurrentCreates.RaceAsync(_host, admin, create);
        second.ErrorMessages.Should().Equal(error);
        var actions = await _host.ReadAsync<string>("SELECT string_agg(action, ',') FROM audit_entry WHERE target_name = @secret OR target_name LIKE @secret || '/%'", secret: site);
        (actions ?? string.Empty).Split(',').Count(action => action == audited).Should().Be(1, "one creation audited, the first's");
        return first.Data;
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

