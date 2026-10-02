using Ariva.Core;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Services;
using Ariva.Core.Services.Administration;
using Ariva.Core.Services.Alerting;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Seed;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ariva.IntegrationTests.Alerting;

/// <summary>
/// ARV-037 against PostgreSQL with script 0020: alert rules round-trip through the service with their typed values, get
/// codes R-001, R-002 and so on per site (deleted codes are not reused, concurrent creates never share one), stay within
/// the caller's sites, watch only zones of the site's published profile, take owner and escalation roles the creator
/// holds (nor change or remove one they do not), take enum values by their exact names only, are audited on every change
/// with JSON summaries, and the runtime role can neither delete them nor store values or identity changes the entity
/// would refuse.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AlertRuleServiceTests(PostgresFixture fixture) : IAsyncDisposable
{
    // The demo airport (site DMO with v12 and R-001 to R-005) is seeded once for the class's shared database.
    private static readonly SemaphoreSlim SeedGate = new(1, 1);
    private static bool _seeded;

    private readonly AccountsHost _host = new(fixture, database: TestDatabase.AlertRules);

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ISvcAlertRules Rules(IServiceProvider s) => s.GetRequiredService<ISvcAlertRules>();

    private static AlertRuleRequest Request(string name = "Visitors wave", string site = "DMO", string[] zones = null, string owner = null, string escalateTo = null) =>
        new(site, name, zones ?? ["A-VIS", "D-VIS"], "Nowcast", "GreaterThan", 20, 5, 15, 2, 2, "Warning", owner, escalateTo is null ? null : 10, escalateTo, null, false);

    private async Task<Guid> AdminAsync(string userName)
    {
        var admin = await _host.CreateUserAsync(userName, roles: [RoleCodes.SystemAdministrator]);
        await _host.ReadAsync<int>("UPDATE \"user\" SET all_sites = true WHERE id = @id RETURNING 1", admin);
        await SeedGate.WaitAsync(Ct);
        try
        {
            if (!_seeded)
            {
                await _host.AsCallerAsync(null, s =>
                    new DemoTopologySeed(s.GetRequiredService<IUnitOfWork>(), s.GetRequiredService<ICurrentUser>(), _host.Clock, s.GetRequiredService<AuditTrail>()).RunAsync(Ct));
                await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest("ALX", "Other airport"), Ct));
                _seeded = true;
            }
        }
        finally
        {
            SeedGate.Release();
        }

        return admin;
    }

    private async Task<Guid> SupervisorAsync(Guid admin, string userName, string site = "DMO", string role = RoleCodes.BorderShiftSupervisor)
    {
        var user = await _host.CreateUserAsync(userName, roles: [role]);
        await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcUsers>().SetSitesAsync(user, new SiteAccessRequest(false, [site]), Ct));
        return user;
    }

    [Fact]
    public async Task Rule_Should_RoundTripWithItsCodeAndAudit_When_CreatedUpdatedAndDeleted()
    {
        var admin = await AdminAsync("it.alert.admin");
        var supervisor = await SupervisorAsync(admin, "it.alert.supervisor");

        var created = await _host.AsCallerAsync(supervisor, s => Rules(s).CreateAsync(Request(owner: RoleCodes.BorderShiftSupervisor), Ct));
        created.HasErrors.Should().BeFalse(string.Join(", ", created.ErrorMessages ?? []));
        created.Data.Code.Should().MatchRegex("^R-0(0[6-9]|[1-9][0-9])$", "R-001 to R-005 are the demo's; codes go on from there");
        created.Data.Zones.Should().Equal("A-VIS", "D-VIS");
        created.Data.Metric.Should().Be("Nowcast");
        created.Data.Severity.Should().Be("Warning");

        var read = await _host.AsCallerAsync(supervisor, s => Rules(s).GetAsync(created.Data.Id, Ct));
        read.Data.Should().BeEquivalentTo(created.Data, o => o.Excluding(v => v.ModifiedOn));
        (await _host.ReadAsync<string>("SELECT scope_zones FROM alert_rule WHERE id = @id", created.Data.Id)).Should().Be("A-VIS\nD-VIS");
        (await _host.ReadAsync<string>("SELECT metric FROM alert_rule WHERE id = @id", created.Data.Id)).Should().Be("Nowcast", "enums are stored by name");

        var updated = await _host.AsCallerAsync(supervisor, s => Rules(s).UpdateAsync(created.Data.Id, Request("Visitors wave, both halls") with { Threshold = 25, Enabled = false }, Ct));
        updated.HasErrors.Should().BeFalse(string.Join(", ", updated.ErrorMessages ?? []));
        updated.Data.Code.Should().Be(created.Data.Code, "an update keeps the code");
        updated.Data.Threshold.Should().Be(25);
        updated.Data.Enabled.Should().BeFalse();
        (await _host.AsCallerAsync(supervisor, s => Rules(s).UpdateAsync(created.Data.Id, Request() with { SiteCode = "ALX" }, Ct)))
            .ErrorMessages.Should().Equal("A rule stays in its site.");

        (await _host.AsCallerAsync(supervisor, s => Rules(s).DeleteAsync(created.Data.Id, Ct))).Data.Should().BeTrue();
        (await _host.AsCallerAsync(supervisor, s => Rules(s).GetAsync(created.Data.Id, Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.ReadAsync<DateTime?>("SELECT deleted_on FROM alert_rule WHERE id = @id", created.Data.Id)).Should().NotBeNull("deletes are soft");

        var next = await _host.AsCallerAsync(supervisor, s => Rules(s).CreateAsync(Request("After the delete"), Ct));
        Number(next.Data.Code).Should().BeGreaterThan(Number(created.Data.Code), "a deleted rule keeps its code");

        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE target_id = @id AND action IN ('AlertRule.Created', 'AlertRule.Updated', 'AlertRule.Deleted')", created.Data.Id))
            .Should().Be(3);
        (await _host.ReadAsync<string>("SELECT after_summary FROM audit_entry WHERE target_id = @id AND action = 'AlertRule.Updated'", created.Data.Id))
            .Should().Contain("\"threshold\":25").And.Contain("\"enabled\":false");
        (await _host.ReadAsync<string>("SELECT before_summary FROM audit_entry WHERE target_id = @id AND action = 'AlertRule.Updated'", created.Data.Id))
            .Should().Contain("\"threshold\":20").And.Contain("\"enabled\":true");
    }

    [Fact]
    public async Task Rules_Should_StayInTheCallersSites_When_ReadOrWrittenFromAnotherSite()
    {
        var admin = await AdminAsync("it.alert.scope.admin");
        var elsewhere = await SupervisorAsync(admin, "it.alert.scope.alx", site: "ALX");
        var dmo = (await _host.AsCallerAsync(admin, s => Rules(s).CreateAsync(Request("Scope probe"), Ct))).Data;

        (await _host.AsCallerAsync(elsewhere, s => Rules(s).GetAsync(dmo.Id, Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(elsewhere, s => Rules(s).UpdateAsync(dmo.Id, Request(), Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(elsewhere, s => Rules(s).DeleteAsync(dmo.Id, Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(elsewhere, s => Rules(s).CreateAsync(Request(), Ct))).ErrorMessages.Should().Equal(TopologyErrors.UnknownSite);
        (await _host.AsCallerAsync(elsewhere, s => Rules(s).SearchAsync(new AlertRuleCriteria(), Ct))).Data.Data.Should().NotContain(r => r.SiteCode == "DMO");
        (await _host.AsCallerAsync(elsewhere, s => Rules(s).SearchAsync(new AlertRuleCriteria { SiteCode = "DMO" }, Ct))).Data.TotalCount.Should().Be(0);
        (await _host.ReadAsync<DateTime?>("SELECT deleted_on FROM alert_rule WHERE id = @id", dmo.Id)).Should().BeNull("the other site's delete did nothing");
        (await _host.AsCallerAsync(admin, s => Rules(s).CreateAsync(Request(site: "NOPE"), Ct))).ErrorMessages.Should().Equal(TopologyErrors.UnknownSite);
    }

    [Fact]
    public async Task Rule_Should_BeRefused_When_ItWatchesAnotherZoneOrGivesARoleTheCreatorLacks()
    {
        var admin = await AdminAsync("it.alert.refuse.admin");
        var supervisor = await SupervisorAsync(admin, "it.alert.refuse.supervisor");

        (await _host.AsCallerAsync(supervisor, s => Rules(s).CreateAsync(Request(zones: ["A-VIS", "NOT-A-ZONE"]), Ct)))
            .ErrorMessages.Should().Equal(AlertRuleErrors.UnknownZones);
        (await _host.AsCallerAsync(supervisor, s => Rules(s).CreateAsync(Request(owner: RoleCodes.SystemAdministrator), Ct)))
            .ErrorMessages.Should().Equal(AlertRuleErrors.RoleNotHeld);
        (await _host.AsCallerAsync(supervisor, s => Rules(s).CreateAsync(Request(escalateTo: RoleCodes.TerminalDutyManager), Ct)))
            .ErrorMessages.Should().Equal(AlertRuleErrors.RoleNotHeld);
        (await _host.AsCallerAsync(admin, s => Rules(s).CreateAsync(Request("Admin escalation", escalateTo: RoleCodes.TerminalDutyManager), Ct)))
            .HasErrors.Should().BeFalse("an administrator may give any role");
        // Exact names only: Enum.TryParse alone would take numbers, padding and comma-joined names (OR-ed into another value).
        foreach (var bad in new[]
                 {
                     Request() with { Metric = "1" }, Request() with { Metric = "nowcast" }, Request() with { Metric = "Nowcast " }, Request() with { Metric = " Nowcast" },
                     Request() with { Metric = "Nowcast,Nowcast" }, Request() with { Metric = "QueueLength,BinP90" }, Request() with { Severity = "Critical,Warning" },
                     Request() with { Severity = "Info,Warning" }, Request() with { Severity = "Info, Critical" }, Request() with { Comparator = "GreaterThan,GreaterOrEqual" },
                     Request() with { Comparator = "IsTrue" }, Request() with { Comparator = null }
                 })
            (await _host.AsCallerAsync(supervisor, s => Rules(s).CreateAsync(bad, Ct))).HasErrors.Should().BeTrue($"'{bad.Metric}' '{bad.Comparator}' '{bad.Severity}'");
        (await _host.AsCallerAsync(admin, s => Rules(s).SearchAsync(new AlertRuleCriteria { Metric = "Nowcast,BinP90" }, Ct))).HasErrors.Should().BeTrue();

        // Only queue and overflow zones: a desk's service zone of the published profile is refused.
        (await _host.ReadAsync<int>("""
            SET session_replication_role = replica;
            INSERT INTO zone (id, profile_id, name, kind, level_id, queue_zone_id, polygon)
            SELECT gen_random_uuid(), z.profile_id, 'A-VIS-D08', 'Service', z.level_id, z.id, '0 0,1 0,1 1'
            FROM zone z JOIN zone_profile p ON p.id = z.profile_id
            WHERE p.site_code = 'DMO' AND p.status = 'Published' AND z.name = 'A-VIS'
            ON CONFLICT DO NOTHING;
            SELECT CAST(count(*) AS integer) FROM zone WHERE name = 'A-VIS-D08' AND kind = 'Service';
            """)).Should().Be(1);
        (await _host.AsCallerAsync(admin, s => Rules(s).CreateAsync(Request(zones: ["A-VIS-D08"]), Ct))).ErrorMessages.Should().Equal(AlertRuleErrors.UnknownZones);
        (await _host.AsCallerAsync(admin, s => Rules(s).CreateAsync(Request("Overflow watched", zones: ["A-OV"]) with
        {
            Metric = "OverflowOccupied", Comparator = "IsTrue", Threshold = null, MinQueueLength = null, ClearThreshold = null
        }, Ct))).HasErrors.Should().BeFalse("overflow zones can be watched");
        (await _host.ReadAsync<long>("SELECT count(*) FROM alert_rule WHERE scope_zones LIKE '%NOT-A-ZONE%'")).Should().Be(0);
    }

    [Fact]
    public async Task Roles_Should_StayAsAnAdministratorSetThem_When_ACallerWithoutThemEdits()
    {
        var admin = await AdminAsync("it.alert.roles.admin");
        var supervisor = await SupervisorAsync(admin, "it.alert.roles.supervisor");
        var rule = (await _host.AsCallerAsync(admin, s => Rules(s).CreateAsync(
            Request("Escalates to the duty manager", owner: RoleCodes.SystemAdministrator, escalateTo: RoleCodes.TerminalDutyManager), Ct))).Data;

        var kept = await _host.AsCallerAsync(supervisor, s => Rules(s).UpdateAsync(rule.Id,
            Request("Renamed by the supervisor", owner: RoleCodes.SystemAdministrator, escalateTo: RoleCodes.TerminalDutyManager) with { Threshold = 30 }, Ct));
        kept.HasErrors.Should().BeFalse("unchanged roles can stay: " + string.Join(", ", kept.ErrorMessages ?? []));
        foreach (var stripped in new[]
                 {
                     Request(owner: null, escalateTo: RoleCodes.TerminalDutyManager) with { Enabled = false },
                     Request(owner: RoleCodes.SystemAdministrator, escalateTo: null),
                     Request(owner: RoleCodes.BorderShiftSupervisor, escalateTo: RoleCodes.TerminalDutyManager)
                 })
            (await _host.AsCallerAsync(supervisor, s => Rules(s).UpdateAsync(rule.Id, stripped, Ct))).ErrorMessages
                .Should().Equal(new[] { AlertRuleErrors.RoleNotHeld }, "a role the caller does not hold cannot be taken away or replaced");

        var stored = (await _host.AsCallerAsync(admin, s => Rules(s).GetAsync(rule.Id, Ct))).Data;
        stored.Should().Match<Ariva.Core.Domain.ViewModels.AlertRuleViewModel>(r =>
            r.OwnerRole == RoleCodes.SystemAdministrator && r.EscalateToRole == RoleCodes.TerminalDutyManager && r.Enabled && r.Name == "Renamed by the supervisor");
    }

    [Fact]
    public async Task Codes_Should_BeUnique_When_RulesAreCreatedTogether()
    {
        var admin = await AdminAsync("it.alert.codes.admin");

        var together = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => _host.AsCallerAsync(admin, s => Rules(s).CreateAsync(Request($"Together {i}"), Ct))));

        together.Should().OnlyContain(r => !r.HasErrors);
        together.Select(r => r.Data.Code).Should().OnlyHaveUniqueItems("the site lock gives each create its own code");
        (await _host.ReadAsync<long>("SELECT count(*) - count(DISTINCT code) FROM alert_rule WHERE site_code = 'DMO'")).Should().Be(0);
    }

    [Fact]
    public async Task Seed_Should_NotBringBackARule_When_SomeoneDeletedIt()
    {
        var admin = await AdminAsync("it.alert.seed.admin");
        var live = (await _host.AsCallerAsync(admin, s => Rules(s).SearchAsync(new AlertRuleCriteria { SiteCode = "DMO", PageSize = 500 }, Ct))).Data.Data;
        live.Should().Contain(r => r.Code == "R-005");
        foreach (var rule in live)
            (await _host.AsCallerAsync(admin, s => Rules(s).DeleteAsync(rule.Id, Ct))).Data.Should().BeTrue();
        var r005 = live.Single(r => r.Code == "R-005");
        var before = await _host.ReadAsync<long>("SELECT count(*) FROM alert_rule");
        (await _host.ReadAsync<long>("SELECT count(*) FROM alert_rule WHERE deleted_on IS NULL AND site_code = 'DMO'")).Should().Be(0);

        var again = await _host.AsCallerAsync(null, s =>
            new DemoTopologySeed(s.GetRequiredService<IUnitOfWork>(), s.GetRequiredService<ICurrentUser>(), _host.Clock, s.GetRequiredService<AuditTrail>()).RunAsync(Ct));

        again.Created.Should().Be(0);
        (await _host.ReadAsync<long>("SELECT count(*) FROM alert_rule")).Should().Be(before, "a site whose rules were all deleted is not seeded again");
        (await _host.ReadAsync<DateTime?>("SELECT deleted_on FROM alert_rule WHERE id = @id", r005.Id)).Should().NotBeNull();
    }

    [Theory]
    [InlineData("DELETE FROM alert_rule")]
    [InlineData("TRUNCATE alert_rule")]
    [InlineData("UPDATE alert_rule SET threshold = 100001")]
    [InlineData("UPDATE alert_rule SET code = 'R-777'")]
    [InlineData("UPDATE alert_rule SET site_code = 'ALX'")]
    [InlineData("UPDATE alert_rule SET name = E'Rule \\u202Eevil' WHERE metric = 'Nowcast'")]
    [InlineData("UPDATE alert_rule SET min_queue_length = 3 WHERE metric = 'BinP90'")]
    [InlineData("INSERT INTO alert_rule (id, site_code, code, name, scope_zones, metric, comparator, sustain_minutes, clear_after_minutes, severity, notify_by_email, enabled, created_on, created_by) VALUES (gen_random_uuid(), 'DMO', 'R-900', 'x', 'A-VIS', 'Sql', 'GreaterThan', 1, 1, 'Info', false, true, now(), 'x')")]
    public async Task Table_Should_RefuseTheRuntimeRole_When_ItDeletesOrWritesOutOfRange(string sql)
    {
        await AdminAsync("it.alert.runtime." + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sql)))[..12]);
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE ariva_runtime", connection, transaction))
            await role.ExecuteNonQueryAsync(Ct);
#pragma warning disable CA2100 // literal statements from the inline data above
        await using var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100

        var change = () => command.ExecuteNonQueryAsync(Ct);

        // Deletes are revoked (42501); an update or insert outside the typed values, or one that moves a rule's site or
        // code, fails the table's checks or its trigger (23514).
        (await change.Should().ThrowAsync<PostgresException>(sql)).Which.SqlState.Should().BeOneOf("42501", "23514");
        await transaction.RollbackAsync(Ct);
    }

    private static int Number(string code) => int.Parse(code.AsSpan(2), System.Globalization.CultureInfo.InvariantCulture);
}
