using System.Text;
using Ariva.Core;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Services.Administration;
using Ariva.Core.Services.Topology;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.IntegrationTests.Topology;

/// <summary>
/// ARV-018 against PostgreSQL with script 0010: a second upload replaces the first in one transaction (the partial
/// unique index allows one live plan per level), a level with a live plan cannot be deleted, concurrent uploads for one level queue on the level row instead of
/// failing, and every change is audited.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FloorPlanTests(PostgresFixture fixture) : IAsyncDisposable
{
    private const string Svg = """<svg xmlns="http://www.w3.org/2000/svg" width="100" height="50"><rect width="10" height="10"/></svg>""";

    private readonly AccountsHost _host = new(fixture, database: TestDatabase.Administration);

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FloorPlanUpload Upload(string marker)
    {
        var bytes = Encoding.UTF8.GetBytes(Svg.Replace("<rect", $"<!-- {marker} --><rect", StringComparison.Ordinal));
        return new FloorPlanUpload(new MemoryStream(bytes), bytes.Length, $"{marker}.svg", 0.05, 0, 0);
    }

    private async Task<(Guid Admin, Guid Level)> LevelAsync(string prefix)
    {
        var admin = await _host.CreateUserAsync($"it.plan.{prefix}", roles: [RoleCodes.SystemAdministrator]);
        await _host.ReadAsync<int>("UPDATE \"user\" SET all_sites = true WHERE id = @id RETURNING 1", admin);
        var site = $"P{prefix}".ToUpperInvariant();
        await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest(site, site), Ct));
        var topology = (IServiceProvider s) => s.GetRequiredService<ISvcTopology>();
        var airport = await _host.AsCallerAsync(admin, s => topology(s).CreateAirportAsync(new CreateAirportRequest($"Q{prefix}".ToUpperInvariant(), null, "Plans", "Asia/Amman"), Ct));
        var terminal = await _host.AsCallerAsync(admin, s => topology(s).CreateTerminalAsync(new CreateTerminalRequest(airport.Data.Id, "T1", "T1", site), Ct));
        var level = await _host.AsCallerAsync(admin, s => topology(s).CreateLevelAsync(new CreateLevelRequest(terminal.Data.Id, "L0", "Ground", 0, 100, 50), Ct));
        level.HasErrors.Should().BeFalse(string.Join(", ", level.ErrorMessages ?? []));
        return (admin, level.Data.Id);
    }

    [Fact]
    public async Task Upload_Should_ReplaceThePreviousPlan_When_TheLevelHasOne()
    {
        var (admin, level) = await LevelAsync("ra");

        var first = await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcFloorPlans>().UploadAsync(level, Upload("first"), Ct));
        var second = await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcFloorPlans>().UploadAsync(level, Upload("second"), Ct));

        first.HasErrors.Should().BeFalse(string.Join(", ", first.ErrorMessages ?? []));
        second.HasErrors.Should().BeFalse(string.Join(", ", second.ErrorMessages ?? []));
        (await _host.ReadAsync<long>("SELECT count(*) FROM floor_plan WHERE level_id = @id AND deleted_on IS NULL", level)).Should().Be(1);
        (await _host.ReadAsync<long>("SELECT count(*) FROM floor_plan WHERE level_id = @id", level)).Should().Be(2, "the replaced plan is soft deleted, not removed");
        (await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcFloorPlans>().GetAsync(level, Ct))).Data.Id.Should().Be(second.Data.Id);
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE action = 'FloorPlan.Uploaded' AND target_id = @id", second.Data.Id)).Should().Be(1);
        (await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcTopology>().DeleteLevelAsync(level, Ct)))
            .ErrorMessages.Should().Equal(new[] { TopologyErrors.HasChildren }, "a live plan keeps its level");
    }

    [Fact]
    public async Task List_Should_GiveTheSitesLivePlansOnly_When_Asked()
    {
        var (admin, level) = await LevelAsync("rl");
        var plans = (IServiceProvider s) => s.GetRequiredService<ISvcFloorPlans>();
        (await _host.AsCallerAsync(admin, s => plans(s).ListAsync("PRL", Ct))).Data.Should().BeEmpty("no level has a plan yet");

        await _host.AsCallerAsync(admin, s => plans(s).UploadAsync(level, Upload("one"), Ct));
        var second = await _host.AsCallerAsync(admin, s => plans(s).UploadAsync(level, Upload("two"), Ct));

        (await _host.AsCallerAsync(admin, s => plans(s).ListAsync("PRL", Ct))).Data.Should().ContainSingle()
            .Which.Should().Match<Ariva.Core.Domain.ViewModels.FloorPlanViewModel>(p => p.Id == second.Data.Id && p.LevelId == level, "the replaced plan is not listed");

        // Another site's caller and no caller are told nothing (CWE-863).
        var other = await _host.CreateUserAsync("it.plan.rl.other", roles: [RoleCodes.SystemAdministrator]);
        (await _host.AsCallerAsync(other, s => plans(s).ListAsync("PRL", Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(null, s => plans(s).ListAsync("PRL", Ct))).HasErrors.Should().BeTrue();

        // An all-sites administrator is told the same for a site that does not exist.
        (await _host.AsCallerAsync(admin, s => plans(s).ListAsync("NOPE", Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
    }

    [Fact]
    public async Task Upload_Should_LeaveOneLivePlan_When_UploadsRace()
    {
        var (admin, level) = await LevelAsync("rb");
        await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcFloorPlans>().UploadAsync(level, Upload("seed"), Ct));

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
            _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcFloorPlans>().UploadAsync(level, Upload($"race{i}"), Ct))));

        results.Should().OnlyContain(r => !r.HasErrors, "uploads for one level queue on the level row");
        (await _host.ReadAsync<long>("SELECT count(*) FROM floor_plan WHERE level_id = @id AND deleted_on IS NULL", level)).Should().Be(1);
        (await _host.ReadAsync<long>("SELECT count(*) FROM floor_plan WHERE level_id = @id", level)).Should().Be(5);
    }
}
