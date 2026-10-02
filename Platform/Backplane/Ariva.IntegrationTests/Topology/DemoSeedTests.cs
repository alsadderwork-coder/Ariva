using Ariva.Core.Services;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Seed;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.IntegrationTests.Topology;

/// <summary>
/// ARV-019 against PostgreSQL: the demo seed creates Demo International Airport with its 48 counters, 10 security lanes,
/// 44 immigration desks, 10 e-gates and zone profile v12 (published, with its event in the outbox and its hash in the
/// audit trail), and a second run changes nothing: no new rows, no updated rows, no audit entries. A draft someone
/// started is left alone, and existing records of another site stop the seed before it writes anything.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DemoSeedTests(PostgresFixture fixture) : IAsyncDisposable
{
    private readonly AccountsHost _host = new(fixture, database: TestDatabase.DemoSeed);

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private Task<SeedOutcome> SeedAsync() => _host.AsCallerAsync(null, s =>
        new DemoTopologySeed(s.GetRequiredService<IUnitOfWork>(), s.GetRequiredService<ICurrentUser>(), _host.Clock, s.GetRequiredService<AuditTrail>())
            .RunAsync(TestContext.Current.CancellationToken));

    private async Task<string> SnapshotAsync() => string.Join("|",
        await _host.ReadAsync<long>("SELECT count(*) FROM desk WHERE site_code = 'DMO'"),
        await _host.ReadAsync<long>("SELECT count(*) FROM checkpoint WHERE site_code = 'DMO'"),
        await _host.ReadAsync<long>("SELECT count(*) FROM level WHERE site_code = 'DMO'"),
        await _host.ReadAsync<long>("SELECT count(*) FROM zone z JOIN zone_profile p ON p.id = z.profile_id WHERE p.site_code = 'DMO'"),
        await _host.ReadAsync<long>("SELECT count(*) FROM line l JOIN zone_profile p ON p.id = l.profile_id WHERE p.site_code = 'DMO'"),
        await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry"),
        await _host.ReadAsync<long>("SELECT count(*) FROM alert_rule"),
        await _host.ReadAsync<long>("SELECT count(*) FROM outbox_message WHERE message_key = 'DMO'"),
        await _host.ReadAsync<long>("SELECT count(*) FROM desk WHERE site_code = 'DMO' AND modified_on IS NOT NULL"),
        await _host.ReadAsync<long>("SELECT count(*) FROM zone_profile WHERE site_code = 'DMO' AND modified_on IS NOT NULL AND modified_on <> created_on"));

    [Fact]
    public async Task Seed_Should_CreateTheDemoAirportOnceAndChangeNothing_When_RunAgain()
    {
        await _host.CreateUserAsync("it.seed.reader");
        // Two pods starting together: the advisory lock makes one wait, and it then finds everything in place.
        var together = await Task.WhenAll(SeedAsync(), SeedAsync());
        var afterFirst = await SnapshotAsync();
        var second = await SeedAsync();
        var afterSecond = await SnapshotAsync();

        together.Select(o => o.Created).Should().ContainSingle(c => c > 100).And.ContainSingle(c => c == 0);
        second.Created.Should().Be(0);
        afterSecond.Should().Be(afterFirst, "a re-run creates, updates and audits nothing");

        (await _host.ReadAsync<long>("SELECT count(*) FROM desk WHERE site_code = 'DMO' AND kind = 'Counter'")).Should().Be(48);
        (await _host.ReadAsync<long>("SELECT count(*) FROM desk WHERE site_code = 'DMO' AND kind = 'SecurityLane'")).Should().Be(10);
        (await _host.ReadAsync<long>("SELECT count(*) FROM desk WHERE site_code = 'DMO' AND kind = 'Desk'")).Should().Be(44);
        (await _host.ReadAsync<long>("SELECT count(*) FROM desk WHERE site_code = 'DMO' AND kind = 'EGate'")).Should().Be(10);
        (await _host.ReadAsync<long>("SELECT count(*) FROM zone z JOIN zone_profile p ON p.id = z.profile_id WHERE p.site_code = 'DMO'")).Should().Be(23);
        (await _host.ReadAsync<long>("SELECT count(*) FROM line l JOIN zone_profile p ON p.id = l.profile_id WHERE p.site_code = 'DMO'")).Should().Be(39);
        (await _host.ReadAsync<int>("SELECT version FROM zone_profile WHERE site_code = 'DMO' AND status = 'Published'")).Should().Be(12);
        (await _host.ReadAsync<string>("SELECT published_by FROM zone_profile WHERE site_code = 'DMO' AND status = 'Published'")).Should().Be("demo-seed");
        (await _host.ReadAsync<long>("SELECT count(*) FROM outbox_message WHERE message_key = 'DMO'")).Should().Be(1, "ZoneProfilePublished for v12");
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE action = 'Seed.DemoTopology'")).Should().Be(1);
        (await _host.ReadAsync<string>("SELECT string_agg(code, ',' ORDER BY code) FROM alert_rule WHERE site_code = 'DMO'")).Should().Be("R-001,R-002,R-003,R-004,R-005",
            "the prototype's rules, once (ARV-037)");
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE action = 'AlertRule.Created' AND after_summary LIKE '{\"site\":\"DMO\"%'")).Should().Be(5, "each seeded rule is audited with its values");
        (await _host.ReadAsync<string>("SELECT after_summary FROM audit_entry WHERE action = 'ZoneProfile.Published'")).Should()
            .Contain("version=12").And.MatchRegex("hash=[0-9a-f]{64}");
        (await _host.ReadAsync<long>("SELECT count(*) FROM \"user\" WHERE created_by = 'demo-seed'")).Should().Be(0, "the seed creates topology only, never accounts (CWE-269)");
    }

    [Fact]
    public async Task Seed_Should_StopWithoutWriting_When_TerminalT1OfTheDemoAirportBelongsToAnotherSite()
    {
        await using var host = new AccountsHost(fixture, database: TestDatabase.DemoSeedConflict);
        await host.CreateUserAsync("it.seed.conflict");
        (await host.ReadAsync<int>("""
            WITH s AS (INSERT INTO site (id, code, name) VALUES ('0199a000-0000-7000-8000-0000000d0101', 'OTHER', 'Other') RETURNING code),
                 a AS (INSERT INTO airport (id, iata_code, name, time_zone_id) VALUES ('0199a000-0000-7000-8000-0000000d0102', 'DMO', 'Demo', 'Asia/Dubai') RETURNING id)
            INSERT INTO terminal (id, airport_id, code, name, site_code)
            SELECT '0199a000-0000-7000-8000-0000000d0103', a.id, 'T1', 'Terminal 1', s.code FROM a, s RETURNING 1
            """)).Should().Be(1);

        var act = () => host.AsCallerAsync(null, s =>
            new DemoTopologySeed(s.GetRequiredService<IUnitOfWork>(), s.GetRequiredService<ICurrentUser>(), host.Clock, s.GetRequiredService<AuditTrail>())
                .RunAsync(TestContext.Current.CancellationToken));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*belongs to site OTHER*");
        (await host.ReadAsync<long>("SELECT count(*) FROM checkpoint")).Should().Be(0, "nothing is written into the other site");
        (await host.ReadAsync<long>("SELECT count(*) FROM level")).Should().Be(0);
    }

    [Fact]
    public async Task Seed_Should_LeaveADraftAloneAndSkipV12_When_TheSiteHasOnlyADraft()
    {
        await using var host = new AccountsHost(fixture, database: TestDatabase.DemoSeedDraft);
        await host.CreateUserAsync("it.seed.draft");
        (await host.ReadAsync<int>("""
            WITH s AS (INSERT INTO site (id, code, name) VALUES ('0199a000-0000-7000-8000-0000000d0001', 'DMO', 'Demo') RETURNING code)
            INSERT INTO zone_profile (id, site_code, name, status) SELECT '0199a000-0000-7000-8000-0000000d0002', code, 'Someone''s draft', 'Draft' FROM s RETURNING 1
            """)).Should().Be(1);

        var outcome = await host.AsCallerAsync(null, s =>
            new DemoTopologySeed(s.GetRequiredService<IUnitOfWork>(), s.GetRequiredService<ICurrentUser>(), host.Clock, s.GetRequiredService<AuditTrail>())
                .RunAsync(TestContext.Current.CancellationToken));

        outcome.ProfileSkipped.Should().Contain("draft");
        (await host.ReadAsync<long>("SELECT count(*) FROM desk WHERE site_code = 'DMO'")).Should().Be(112, "the topology is still seeded");
        (await host.ReadAsync<long>("SELECT count(*) FROM zone_profile WHERE site_code = 'DMO'")).Should().Be(1, "the draft is left alone and nothing is published");
        (await host.ReadAsync<string>("SELECT name FROM zone_profile WHERE site_code = 'DMO'")).Should().Be("Someone's draft");
        (await host.ReadAsync<long>("SELECT count(*) FROM alert_rule")).Should().Be(0, "the rules watch v12's zones, which were not published");
    }
}
