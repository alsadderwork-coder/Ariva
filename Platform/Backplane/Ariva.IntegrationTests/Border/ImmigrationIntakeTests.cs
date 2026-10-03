using Ariva.Business.Contracts.Aman.V1;
using Ariva.Core.Border;
using Ariva.Core.Services;
using Ariva.Core.Services.Border;
using Ariva.Di.Extensions;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Seed;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using Ariva.Simulation.Api.Emulators.Aman;
using Ariva.Simulation.Api.Scenarios.Engine;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ariva.IntegrationTests.Border;

/// <summary>
/// ARV-048 on PostgreSQL: a minute of the AMAN emulator's feed is stored whole, its AMAN codes resolved to the demo
/// airport's desks and e-gates through the seeded AMAN desk code mappings; the same records again (another transport, a
/// redelivery) change nothing; an unmapped code is kept apart with a warning; a record of another site or an unknown site
/// is refused; a lane demand is replaced only by a later computation; the database repeats the rules (sums, small cells)
/// and the runtime role cannot change or delete a record.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ImmigrationIntakeTests(PostgresFixture fixture) : IAsyncDisposable
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _seeded;

    private readonly AccountsHost _host = new(fixture, database: TestDatabase.Border,
        configure: services => services.AddArivaBorderFeed(new ConfigurationBuilder().Build()));

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateTime Now => _host.Clock.GetUtcNow().UtcDateTime;

    private async Task SeedAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            await _host.CreateUserAsync("it.border.probe." + Guid.NewGuid().ToString("N")[..8]);
            if (_seeded)
                return;
            await _host.AsCallerAsync(null, s =>
                new DemoTopologySeed(s.GetRequiredService<IUnitOfWork>(), s.GetRequiredService<ICurrentUser>(), _host.Clock, s.GetRequiredService<AuditTrail>()).RunAsync(Ct));
            _seeded = true;
        }
        finally
        {
            Gate.Release();
        }
    }

    private AmanMinute Minute(int minute, bool first = true)
    {
        var day = ScenarioDay.Run(ScenarioConfig.Reference(9303));
        var start = new DateTime(Now.Ticks - Now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc).AddMinutes(-minute);
        return AmanFeed.Build(day, minute, m => start.AddMinutes(m), "DMO", BorderSides.Both, first);
    }

    private Task<IReadOnlyList<Ariva.Core.Border.ImmigrationItemResult>> Apply<T>(Func<ISvcImmigrationIntake, Task<IReadOnlyList<Ariva.Core.Border.ImmigrationItemResult>>> call) =>
        _host.AsCallerAsync(null, s => call(s.GetRequiredService<ISvcImmigrationIntake>()));

    [Fact]
    public async Task Intake_Should_StoreAMinuteOnceWithItsDesksResolved_When_ItArrivesTwice()
    {
        await SeedAsync();
        var minute = Minute(1110);

        foreach (var feed in new[] { "aman-kafka", "api-aman-rest" })
        {
            var sessions = await Apply<DeskSessionChanged>(i => i.ApplyDeskSessionsAsync(feed == "aman-kafka" ? ImmigrationScope.RecordSite : ImmigrationScope.ForSite("DMO"), feed, minute.Sessions, Ct));
            var desks = await Apply<DeskIntervalStats>(i => i.ApplyDeskIntervalsAsync(ImmigrationScope.RecordSite, feed, minute.Desks, Ct));
            var gates = await Apply<EGateIntervalStats>(i => i.ApplyEgateIntervalsAsync(ImmigrationScope.RecordSite, feed, minute.Gates, Ct));
            var demand = await Apply<InboundFlightLaneDemand>(i => i.ApplyLaneDemandAsync(ImmigrationScope.ForSite("DMO"), feed, minute.Demand, Ct));
            var all = sessions.Concat(desks).Concat(gates).Concat(demand).ToList();
            all.Should().HaveCount(minute.Count).And.OnlyContain(r => !r.HasErrors);
            if (feed == "aman-kafka")
                all.Should().OnlyContain(r => r.Applied && r.Warnings.Count == 0, "every AMAN code of the demo airport is mapped");
            else
                all.Should().OnlyContain(r => !r.Applied && r.Warnings.Count == 1, "the same records over the other transport change nothing");
        }

        (await _host.ReadAsync<long>("SELECT count(*) FROM border_desk_interval WHERE desk_id IS NULL AND feed = 'aman-kafka' AND source_event_id LIKE 'aman-%'")).Should().Be(0);
        (await _host.ReadAsync<string>("SELECT d.code FROM border_desk_interval b JOIN desk d ON d.id = b.desk_id WHERE b.desk_code = 'IN09' LIMIT 1")).Should().Be("AR-09");
        (await _host.ReadAsync<string>("SELECT d.code || ' ' || d.kind FROM border_egate_interval b JOIN desk d ON d.id = b.desk_id WHERE b.gate_code = 'EGOUT2' LIMIT 1"))
            .Should().Be("DG-2 EGate");
        (await _host.ReadAsync<long>("SELECT count(*) FROM border_desk_session WHERE feed = 'aman-kafka'")).Should().Be(minute.Sessions.Count);
    }

    [Fact]
    public async Task Intake_Should_KeepUnmappedCodesApartAndRefuseOtherSites_When_Received()
    {
        await SeedAsync();
        var at = new DateTimeOffset(new DateTime(Now.Ticks - Now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc));
        DeskIntervalStats Desk(string site, string code, string id) => new(site, code, at, 60, 1, 2, 30, 45, 60, "CIT", id);

        var results = await Apply<DeskIntervalStats>(i => i.ApplyDeskIntervalsAsync(ImmigrationScope.ForSite("DMO"), "api-imm-test", [
            Desk("DMO", "NOSUCH1", "imm-unmapped-1"), Desk("DMO", "EGIN1", "imm-wrongkind-1"), Desk("BEY", "IN01", "imm-othersite-1")], Ct));
        var kafka = await Apply<DeskIntervalStats>(i => i.ApplyDeskIntervalsAsync(ImmigrationScope.RecordSite, "aman-kafka", [Desk("ZZ9", "IN01", "imm-unknownsite-1")], Ct));

        results[0].Should().Match<Ariva.Core.Border.ImmigrationItemResult>(r => r.Applied && r.Warnings.Single().Contains("no AMAN desk code mapping"));
        results[1].Should().Match<Ariva.Core.Border.ImmigrationItemResult>(r => r.Applied && r.Warnings.Single().Contains("maps to a EGate"));
        results[2].Errors.Should().Equal("siteCode is the site of the call.");
        kafka[0].Errors.Should().Equal("siteCode is not a site Ariva knows.");
        (await _host.ReadAsync<long>("SELECT count(*) FROM border_desk_interval WHERE source_event_id IN ('imm-unmapped-1', 'imm-wrongkind-1') AND desk_id IS NULL"))
            .Should().Be(2);
    }

    [Fact]
    public async Task Intake_Should_ResolveOnlyMappingsWhoseDeskIsAtTheSameSite_When_AMappingCrossesSites()
    {
        await SeedAsync();
        // A second site whose AMAN mapping (written by the migration role, past the entity's own check) points at a desk
        // of DMO: it resolves neither for DMO (another site's mapping) nor for XS1 (the desk is not at XS1). CWE-863.
        await ExecuteAsMigrationAsync("""
            INSERT INTO site (id, code, name) SELECT gen_random_uuid(), 'XS1', 'Cross-site probe' WHERE NOT EXISTS (SELECT 1 FROM site WHERE code = 'XS1');
            INSERT INTO desk (id, checkpoint_id, site_code, code, name, kind)
                 SELECT gen_random_uuid(), d.checkpoint_id, 'DMO', 'XT-1', 'Cross-site probe desk', 'Desk' FROM desk d
                  WHERE d.site_code = 'DMO' AND d.code = 'AR-09' AND d.deleted_on IS NULL
                    AND NOT EXISTS (SELECT 1 FROM desk WHERE site_code = 'DMO' AND code = 'XT-1');
            INSERT INTO desk_code_mapping (id, system, external_code, desk_id, site_code)
                 SELECT gen_random_uuid(), 'Aman', 'XSITE1', d.id, 'XS1' FROM desk d WHERE d.site_code = 'DMO' AND d.code = 'XT-1'
                    AND NOT EXISTS (SELECT 1 FROM desk_code_mapping WHERE system = 'Aman' AND site_code = 'XS1' AND external_code = 'XSITE1');
            """);
        var at = new DateTimeOffset(new DateTime(Now.Ticks - Now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc));
        DeskIntervalStats Desk(string site, string id) => new(site, "XSITE1", at, 60, 1, 2, 30, 45, 60, "CIT", id);

        var dmo = await Apply<DeskIntervalStats>(i => i.ApplyDeskIntervalsAsync(ImmigrationScope.ForSite("DMO"), "api-imm-test", [Desk("DMO", "imm-cross-dmo")], Ct));
        var xs1 = await Apply<DeskIntervalStats>(i => i.ApplyDeskIntervalsAsync(ImmigrationScope.RecordSite, "aman-kafka", [Desk("XS1", "imm-cross-xs1")], Ct));

        foreach (var result in dmo.Concat(xs1))
            result.Should().Match<Ariva.Core.Border.ImmigrationItemResult>(r => r.Applied && r.Warnings.Single().Contains("no AMAN desk code mapping"));
        (await _host.ReadAsync<long>("SELECT count(*) FROM border_desk_interval WHERE source_event_id IN ('imm-cross-dmo', 'imm-cross-xs1') AND desk_id IS NULL"))
            .Should().Be(2);
    }

    [Fact]
    public async Task Intake_Should_NeverEchoACodeOutsideTheMappingShape_When_ARecordIsRefused()
    {
        await SeedAsync();
        var at = new DateTimeOffset(new DateTime(Now.Ticks - Now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc));
        var results = await Apply<DeskIntervalStats>(i => i.ApplyDeskIntervalsAsync(ImmigrationScope.ForSite("DMO"), "api-imm-test",
            [new("DMO", "<script>", at, 60, 1, 2, 30, 45, 60, "CIT", "imm-echo-1"), new("DMO", "IN01", at, 60, 1, 2, 30, 45, 60, "EG", "imm-echo-2")], Ct));

        results[0].Key.Should().BeNull("a code outside the mapping shape is never echoed");
        results[1].Key.Should().Be("IN01");
        System.Text.Json.JsonSerializer.Serialize(results).Should().NotContain("script");
    }

    [Fact]
    public async Task Intake_Should_RefuseTheCall_When_NoScopeWasChosen()
    {
        await SeedAsync();
        var call = () => Apply<DeskIntervalStats>(i => i.ApplyDeskIntervalsAsync(default, "aman-kafka", [], Ct));
        await call.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task LaneDemand_Should_KeepTheLatestComputation_When_AnOlderOneArrivesLate()
    {
        await SeedAsync();
        var at = new DateTimeOffset(Now);
        InboundFlightLaneDemand Demand(int boarded, DateTimeOffset computed, string id) =>
            new("DMO", "QR900-20261003-A", at.AddHours(2), boarded, new Dictionary<string, int> { ["VIS"] = boarded / 2 }, 0, computed, id);

        (await Apply<InboundFlightLaneDemand>(i => i.ApplyLaneDemandAsync(ImmigrationScope.ForSite("DMO"), "aman-kafka", [Demand(180, at.AddMinutes(-30), "ld-new")], Ct)))[0].Applied.Should().BeTrue();
        var older = await Apply<InboundFlightLaneDemand>(i => i.ApplyLaneDemandAsync(ImmigrationScope.ForSite("DMO"), "aman-kafka", [Demand(150, at.AddMinutes(-90), "ld-old")], Ct));
        older[0].Applied.Should().BeFalse();
        older[0].Warnings.Single().Should().Contain("Not newer");
        (await Apply<InboundFlightLaneDemand>(i => i.ApplyLaneDemandAsync(ImmigrationScope.ForSite("DMO"), "aman-kafka", [Demand(190, at.AddMinutes(-5), "ld-latest")], Ct)))[0].Applied.Should().BeTrue();
        (await _host.ReadAsync<string>("SELECT boarded_total || ' ' || vis || ' ' || source_event_id FROM inbound_lane_demand WHERE flight_key = 'QR900-20261003-A'"))
            .Should().Be("190 95 ld-latest");
    }

    [Theory]
    [InlineData("UPDATE border_desk_interval SET documents_processed = 0", "42501")]
    [InlineData("DELETE FROM border_desk_session", "42501")]
    [InlineData("DELETE FROM inbound_lane_demand", "42501")]
    [InlineData("UPDATE border_egate_interval SET attempts = 0", "42501")]
    [InlineData("DELETE FROM border_egate_interval", "42501")]
    [InlineData("UPDATE border_desk_session SET state = 'Closed'", "42501")]
    [InlineData("DELETE FROM border_desk_interval", "42501")]
    [InlineData("INSERT INTO border_egate_interval (id, site_code, gate_code, interval_start_utc, attempts, accepted, rejected, rejects_other, rejects_document_read, rejects_biometric_capture, rejects_eligibility, rejects_referred_to_officer, rejects_technical, mean_cycle_seconds, feed, source_event_id, received_utc) VALUES (gen_random_uuid(), 'DMO', 'EGIN1', date_trunc('minute', now()), 4, 2, 2, 0, 2, 0, 0, 0, 0, 18, 'aman-kafka', 'db-small-cell', now())", "23514")]
    [InlineData("INSERT INTO border_desk_interval (id, site_code, desk_code, interval_start_utc, transactions_processed, documents_processed, mean_service_seconds, p90_service_seconds, mean_cycle_seconds, lane_category, feed, source_event_id, received_utc) VALUES (gen_random_uuid(), 'DMO', 'IN01', date_trunc('minute', now()), 5, 3, 30, 40, 12, 'CIT', 'aman-kafka', 'db-sums', now())", "23514")]
    public async Task Database_Should_RefuseChangesAndBrokenRecords_When_TheRuntimeRoleTries(string sql, string state)
    {
        await SeedAsync();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE ariva_runtime", connection, transaction))
            await role.ExecuteNonQueryAsync(Ct);
#pragma warning disable CA2100 // literal statements from the inline data above
        await using var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100

        var change = () => command.ExecuteNonQueryAsync(Ct);

        (await change.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(state);
    }

    private async Task ExecuteAsMigrationAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // literal statements of this class
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(Ct);
    }
}
