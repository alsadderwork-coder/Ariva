using System.Security.Cryptography;
using Ariva.Core;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Services;
using Ariva.Core.Services.Administration;
using Ariva.Core.Services.Topology;
using Ariva.Core.Services.Storage;
using Ariva.Infra.NHibernate;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Seed;
using Ariva.Infra.Settings;
using Ariva.Infra.Streaming;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NHibernate.Engine;
using Npgsql;
using Site = Ariva.Core.Domain.Entities.Site;

namespace Ariva.IntegrationTests.Topology;

/// <summary>
/// ARV-139a against PostgreSQL: the illustrative AUH Terminal A arrivals seed beside the DMO seed. Both create their
/// site once; a re-run of either creates, updates and audits nothing; the site carries the illustrative flag and the
/// runtime role cannot set or clear it (CWE-269); the plan is stored through the floor plan pipeline; every counter's
/// staff and service zones reach the stream's desk links (ARV-116); the sensors are in commissioning without a
/// credential; and the seed never writes into a site AUH-TA that is not illustrative.
/// <para>
/// ARV-139c, in Debug builds only (the NBJ-BC1 seed exists only there, ARIVA_DEV_SEED): the development-only NBJ terminal
/// BC1 seed under the same rules: once, a re-run writes nothing, the counts of its layout, no account, grant or AMAN code,
/// sensors in commissioning without a credential, and neither refusal path writes anything.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class IllustrativeSeedTests(PostgresFixture fixture) : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _files = Path.Combine(Path.GetTempPath(), "ariva-it-files", Guid.NewGuid().ToString("N"));
    private AccountsHost _host;

    private AccountsHost Host(TestDatabase database = TestDatabase.IllustrativeSeed) =>
        _host ??= new AccountsHost(fixture, new Dictionary<string, string> { ["Storage:LocalRoot"] = _files }, database);

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
            await _host.DisposeAsync();
        if (Directory.Exists(_files))
            Directory.Delete(_files, recursive: true);
    }

    private static Task<SeedOutcome> AuhAsync(AccountsHost host) => host.AsCallerAsync(null, s =>
        new AuhTerminalASeed(s.GetRequiredService<IUnitOfWork>(), s.GetRequiredService<ICurrentUser>(), host.Clock, s.GetRequiredService<AuditTrail>(),
            s.GetRequiredService<IFileStorage>()).RunAsync(Ct));

    private static Task<SeedOutcome> DmoAsync(AccountsHost host) => host.AsCallerAsync(null, s =>
        new DemoTopologySeed(s.GetRequiredService<IUnitOfWork>(), s.GetRequiredService<ICurrentUser>(), host.Clock, s.GetRequiredService<AuditTrail>()).RunAsync(Ct));

    private async Task<string> SnapshotAsync() => string.Join("|",
        await _host.ReadAsync<long>("SELECT count(*) FROM site"),
        await _host.ReadAsync<long>("SELECT count(*) FROM airport"),
        await _host.ReadAsync<long>("SELECT count(*) FROM terminal"),
        await _host.ReadAsync<long>("SELECT count(*) FROM level"),
        await _host.ReadAsync<long>("SELECT count(*) FROM checkpoint"),
        await _host.ReadAsync<long>("SELECT count(*) FROM desk"),
        await _host.ReadAsync<long>("SELECT count(*) FROM zone_profile"),
        await _host.ReadAsync<long>("SELECT count(*) FROM zone"),
        await _host.ReadAsync<long>("SELECT count(*) FROM line"),
        await _host.ReadAsync<long>("SELECT count(*) FROM floor_plan"),
        await _host.ReadAsync<long>("SELECT count(*) FROM device"),
        await _host.ReadAsync<long>("SELECT count(*) FROM desk_code_mapping"),
        await _host.ReadAsync<long>("SELECT count(*) FROM alert_rule"),
        await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry"),
        await _host.ReadAsync<long>("SELECT count(*) FROM outbox_message"),
        await _host.ReadAsync<long>("SELECT count(*) FROM site WHERE modified_on IS NOT NULL"),
        await _host.ReadAsync<long>("SELECT count(*) FROM desk WHERE modified_on IS NOT NULL"),
        await _host.ReadAsync<long>("SELECT count(*) FROM device WHERE modified_on IS NOT NULL"),
        await _host.ReadAsync<long>("SELECT count(*) FROM zone_profile WHERE modified_on IS NOT NULL AND modified_on <> created_on"));

    [Fact]
    public async Task Seeds_Should_CreateBothSitesOnceAndChangeNothing_When_RunAgain()
    {
        var host = Host();
        await host.CreateUserAsync("it.illustrative.reader");
        var dmo = await DmoAsync(host);
        // Two pods starting together: the advisory lock makes one wait, and it then finds everything in place.
        var together = await Task.WhenAll(AuhAsync(host), AuhAsync(host));
        var afterFirst = await SnapshotAsync();
        var again = (await DmoAsync(host), await AuhAsync(host));
        var afterSecond = await SnapshotAsync();

        dmo.Created.Should().BeGreaterThan(100);
        together.Select(o => o.Created).Should().ContainSingle(c => c > 200).And.ContainSingle(c => c == 0);
        together.Should().OnlyContain(o => o.ProfileSkipped == null);
        (again.Item1.Created, again.Item2.Created).Should().Be((0, 0));
        afterSecond.Should().Be(afterFirst, "a re-run of either seed creates, updates and audits nothing");

        // Both sites coexist; only AUH-TA is illustrative.
        (await host.ReadAsync<string>("SELECT string_agg(code || '=' || is_illustrative, ',' ORDER BY code) FROM site WHERE code IN ('DMO', 'AUH-TA')"))
            .Should().Be("AUH-TA=true,DMO=false");
        (await host.ReadAsync<long>("SELECT count(*) FROM desk WHERE site_code = 'DMO'")).Should().Be(112, "DMO is unchanged");
        (await host.ReadAsync<long>("SELECT count(*) FROM zone z JOIN zone_profile p ON p.id = z.profile_id WHERE p.site_code = 'DMO'")).Should().Be(23);

        (await host.ReadAsync<string>("SELECT a.iata_code || '/' || t.code || '/' || l.code || '/' || c.code || '/' || c.kind FROM checkpoint c " +
                                      "JOIN level l ON l.id = c.level_id JOIN terminal t ON t.id = l.terminal_id JOIN airport a ON a.id = t.airport_id WHERE c.site_code = 'AUH-TA'"))
            .Should().Be("AUH/A/ARR/IMM/Immigration");
        (await host.ReadAsync<long>("SELECT count(*) FROM desk WHERE site_code = 'AUH-TA' AND kind = 'Desk'")).Should().Be(38);
        (await host.ReadAsync<long>("SELECT count(*) FROM desk WHERE site_code = 'AUH-TA' AND kind = 'EGate'")).Should().Be(34);
        (await host.ReadAsync<string>("SELECT string_agg(DISTINCT lane_category_codes, ',' ORDER BY lane_category_codes) FROM desk WHERE site_code = 'AUH-TA'"))
            .Should().Be("CIT,CRW,DIP,EG,GCC,RES,TRF,VIS");
        (await host.ReadAsync<long>("SELECT count(*) FROM desk_code_mapping WHERE site_code = 'AUH-TA'")).Should().Be(0, "no AMAN codes: the counters are sensor-derived");

        (await host.ReadAsync<int>("SELECT version FROM zone_profile WHERE site_code = 'AUH-TA' AND status = 'Published'")).Should().Be(1);
        (await host.ReadAsync<string>("SELECT published_by FROM zone_profile WHERE site_code = 'AUH-TA'")).Should().Be("demo-seed");
        (await host.ReadAsync<string>("SELECT string_agg(z.kind || '=' || z.n, ',' ORDER BY z.kind) FROM (SELECT kind, count(*) AS n FROM zone z " +
                                      "JOIN zone_profile p ON p.id = z.profile_id WHERE p.site_code = 'AUH-TA' GROUP BY kind) z"))
            .Should().Be("Overflow=2,Queue=8,Service=38,Staff=38");
        (await host.ReadAsync<long>("SELECT count(*) FROM zone z JOIN zone_profile p ON p.id = z.profile_id JOIN desk d ON d.id = z.desk_id " +
                                    "WHERE p.site_code = 'AUH-TA' AND z.name IN (d.code || ' staff', d.code || ' service')")).Should().Be(76, "each named after its desk");
        (await host.ReadAsync<long>("SELECT count(*) FROM outbox_message WHERE message_key = 'AUH-TA' AND message_type LIKE '%ZoneProfilePublished%'")).Should().Be(1);

        (await host.ReadAsync<long>("SELECT count(*) FROM device WHERE site_code = 'AUH-TA'")).Should().Be(AuhTerminalALayout.Sensors().Count);
        (await host.ReadAsync<long>("SELECT count(*) FROM device WHERE site_code = 'AUH-TA' AND (state <> 'Commissioning' OR credential_hash IS NOT NULL)"))
            .Should().Be(0, "no credential is issued and nothing is calibrated");
        (await host.ReadAsync<long>("SELECT count(*) FROM \"user\" WHERE created_by = 'demo-seed'")).Should().Be(0, "topology only (CWE-269)");
        (await host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE action = 'Seed.IllustrativeTopology'")).Should().Be(1);

        // The plan went through the pipeline: an SVG under a generated key whose stored bytes have the recorded hash.
        var key = await host.ReadAsync<string>("SELECT storage_key FROM floor_plan WHERE site_code = 'AUH-TA' AND deleted_on IS NULL");
        key.Should().MatchRegex("^[0-9a-f]{32}\\.svg$");
        (await host.ReadAsync<string>("SELECT content_type || ' ' || width_pixels || 'x' || height_pixels || ' ' || metres_per_pixel FROM floor_plan WHERE site_code = 'AUH-TA'"))
            .Should().Be("image/svg+xml 2000x1500 0.1");
        var stored = await host.AsCallerAsync(null, async s =>
        {
            await using var file = await s.GetRequiredService<IFileStorage>().OpenReadAsync(key, Ct);
            using var copy = new MemoryStream();
            await file.CopyToAsync(copy, Ct);
            return copy.ToArray();
        });
        Convert.ToHexStringLower(SHA256.HashData(stored)).Should().Be(await host.ReadAsync<string>("SELECT sha256 FROM floor_plan WHERE site_code = 'AUH-TA'"));

        // The stream links every counter's two zones to its desk (ARV-116): 17 visitors' counters, 2 crew counters.
        var source = new ZoneGeometrySource(new DatabaseSettings
        {
            Host = fixture.Hostname, Port = fixture.Port, Name = await host.DatabaseAsync(), Username = fixture.AdminUsername, Password = fixture.AdminPassword
        });
        (await source.LoadAsync("AUH-TA", "A-VIS", Ct)).Geometry.DeskZones.Should().HaveCount(34);
        (await source.LoadAsync("AUH-TA", "A-CRW", Ct)).Geometry.DeskZones.Should().HaveCount(4);
        (await source.LoadAsync("AUH-TA", "A-EG", Ct)).Geometry.DeskZones.Should().BeEmpty("smart gates have no desk zones");
    }

    // CWE-269 (script 0046): a real runtime login, as ariva_ensure_runtime_login creates it for the hosts, renames the
    // illustrative site through SvcSites and NHibernate, re-runs the seed (its SELECT ... FOR UPDATE on site), and fails
    // with 42501 on every statement that would set, clear or recode the flag's row or delete it. The Site mapping must
    // keep dynamic update: a full-row UPDATE would also write is_illustrative and code, which the login may not.
    [Fact]
    public async Task RuntimeLogin_Should_RenameAndLockASiteButNeverSetOrClearTheIllustrativeFlag_When_ConnectedAsTheHostsAre()
    {
        var host = Host(TestDatabase.IllustrativeSeedRuntime);
        await host.CreateUserAsync("it.illustrative.runtime.seed");
        (await AuhAsync(host)).Created.Should().BeGreaterThan(200);
        var database = await host.DatabaseAsync();

        const string login = "it_illustrative_runtime";
        var password = "rt-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        await using (var admin = new NpgsqlConnection(fixture.ConnectionString(database)))
        {
            await admin.OpenAsync(Ct);
            await using var ensure = new NpgsqlCommand("SELECT ariva_ensure_runtime_login(@login, @password)", admin);
            ensure.Parameters.AddWithValue("login", login);
            ensure.Parameters.AddWithValue("password", password);
            await ensure.ExecuteNonQueryAsync(Ct);
        }

        (await host.ReadAsync<bool>("SELECT pg_has_role('it_illustrative_runtime', 'ariva_runtime', 'MEMBER') AND NOT rolsuper AND NOT rolcreaterole FROM pg_roles WHERE rolname = 'it_illustrative_runtime'"))
            .Should().BeTrue("the login is the hosts' runtime login, not the migration owner");

        await using var runtime = new AccountsHost(fixture, new Dictionary<string, string>
        {
            ["Storage:LocalRoot"] = _files, ["Database:Username"] = login, ["Database:Password"] = password
        }, TestDatabase.IllustrativeSeedRuntime);
        var operatorId = await runtime.CreateUserAsync("it.illustrative.runtime", allSites: true);

        // The mapping NHibernate runs under: dynamic update, so a rename writes only the columns script 0046 grants.
        var factory = (ISessionFactoryImplementor)runtime.Provider.GetRequiredService<NHibernateSessionFactoryProvider>().SessionFactory;
        factory.GetEntityPersister(typeof(Site).FullName).EntityMetamodel.IsDynamicUpdate
            .Should().BeTrue("a full-row UPDATE of site would write is_illustrative and code, which the runtime role may not");

        // A rename through the service, as PUT /sites/{code} does it.
        var renamed = await runtime.AsCallerAsync(operatorId, s => s.GetRequiredService<ISvcSites>().UpdateAsync("AUH-TA", new UpdateSiteRequest("AUH Terminal A (renamed)"), Ct));
        renamed.HasErrors.Should().BeFalse(string.Join(", ", renamed.ErrorMessages ?? []));
        (await host.ReadAsync<string>("SELECT name || '|' || is_illustrative || '|' || modified_by FROM site WHERE code = 'AUH-TA'"))
            .Should().Be("AUH Terminal A (renamed)|true|it-admin");

        // A re-run of the seed under the login: it locks the site row (SELECT ... FOR UPDATE) and finds everything in place.
        var rerun = await AuhAsync(runtime);
        rerun.Created.Should().Be(0, "a re-run under the runtime login writes nothing");
        rerun.ProfileSkipped.Should().BeNull();

        await using var connection = new NpgsqlConnection(fixture.ConnectionString(database, login, password));
        await connection.OpenAsync(Ct);
        foreach (var sql in new[]
                 {
                     "UPDATE site SET is_illustrative = false WHERE code = 'AUH-TA'",
                     "UPDATE site SET is_illustrative = true WHERE code = 'DMO' OR code = 'AUH-TA'",
                     "UPDATE site SET code = 'AUH-TB' WHERE code = 'AUH-TA'",
                     "DELETE FROM site WHERE code = 'AUH-TA'"
                 })
        {
            async Task<int> RunAsync()
            {
                await using var transaction = await connection.BeginTransactionAsync(Ct);
#pragma warning disable CA2100 // literal statements from this test
                await using var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100
                var rows = await command.ExecuteNonQueryAsync(Ct);
                await transaction.RollbackAsync(Ct);
                return rows;
            }

            var change = RunAsync;
            (await change.Should().ThrowAsync<PostgresException>(sql)).Which.SqlState.Should().Be("42501", "permission denied");
        }

        (await host.ReadAsync<string>("SELECT code || '|' || is_illustrative FROM site WHERE id = (SELECT id FROM site WHERE name = 'AUH Terminal A (renamed)')"))
            .Should().Be("AUH-TA|true");
    }

    // CWE-269, CWE-863: AUH terminal A bound to another site stops the seed, and nothing of it remains: the site it saved
    // before the check is rolled back, no level, desk, zone, profile, plan or sensor is written, nothing is audited, and
    // no plan file is left in storage.
    [Fact]
    public async Task Seed_Should_StopWithoutWriting_When_TerminalABelongsToAnotherSite()
    {
        var host = Host(TestDatabase.IllustrativeSeedForeignTerminal);
        var admin = await host.CreateUserAsync("it.illustrative.foreign", roles: [RoleCodes.SystemAdministrator], allSites: true);
        (await host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest("OTH", "Another site"), Ct))).HasErrors.Should().BeFalse();
        var airport = await host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcTopology>()
            .CreateAirportAsync(new CreateAirportRequest(AuhTerminalALayout.AirportIata, null, "Another deployment's airport", AuhTerminalALayout.TimeZoneId), Ct));
        airport.HasErrors.Should().BeFalse(string.Join(", ", airport.ErrorMessages ?? []));
        var terminal = await host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcTopology>()
            .CreateTerminalAsync(new CreateTerminalRequest(airport.Data.Id, AuhTerminalALayout.TerminalCode, "Another site's terminal", "OTH"), Ct));
        terminal.HasErrors.Should().BeFalse(string.Join(", ", terminal.ErrorMessages ?? []));
        var before = await SnapshotAsync();

        var act = () => AuhAsync(host);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*belongs to site OTH*");
        (await SnapshotAsync()).Should().Be(before, "the seed's writes are rolled back: no site, level, desk, zone, profile, plan, sensor or audit entry");
        (await host.ReadAsync<long>("SELECT count(*) FROM site WHERE code = 'AUH-TA'")).Should().Be(0, "the site saved before the check is rolled back");
        (await host.ReadAsync<long>("SELECT count(*) FROM level")).Should().Be(0);
        (await host.ReadAsync<long>("SELECT count(*) FROM desk")).Should().Be(0);
        (await host.ReadAsync<long>("SELECT count(*) FROM zone")).Should().Be(0);
        (await host.ReadAsync<long>("SELECT count(*) FROM zone_profile")).Should().Be(0);
        (await host.ReadAsync<long>("SELECT count(*) FROM floor_plan")).Should().Be(0);
        (await host.ReadAsync<long>("SELECT count(*) FROM device")).Should().Be(0);
        (await host.ReadAsync<string>("SELECT t.site_code || '|' || t.name FROM terminal t JOIN airport a ON a.id = t.airport_id WHERE a.iata_code = 'AUH' AND t.code = 'A'"))
            .Should().Be("OTH|Another site's terminal", "the other site's terminal is untouched");
        (Directory.Exists(_files) ? Directory.EnumerateFiles(_files, "*", SearchOption.AllDirectories).ToList() : []).Should().BeEmpty("no plan file is left in storage");
    }

    [Fact]
    public async Task Seed_Should_StopWithoutWriting_When_SiteAuhTaExistsAndIsNotIllustrative()
    {
        await using var host = new AccountsHost(fixture, new Dictionary<string, string> { ["Storage:LocalRoot"] = _files }, TestDatabase.IllustrativeSeedReal);
        await host.CreateUserAsync("it.illustrative.real");
        (await host.ReadAsync<int>("""
            WITH s AS (INSERT INTO site (id, code, name) VALUES ('0199a000-0000-7000-8000-000000139b01', 'AUH-TA', 'A real deployment') RETURNING 1)
            SELECT 1 FROM s
            """)).Should().Be(1);

        var act = () => AuhAsync(host);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not illustrative*");
        (await host.ReadAsync<long>("SELECT count(*) FROM airport")).Should().Be(0, "nothing is written into a real site");
        (await host.ReadAsync<long>("SELECT count(*) FROM floor_plan")).Should().Be(0);
        (await host.ReadAsync<bool>("SELECT is_illustrative FROM site WHERE code = 'AUH-TA'")).Should().BeFalse();
    }

#if ARIVA_DEV_SEED
    #region NBJ terminal BC1 (ARV-139c, development only)

    private static Task<SeedOutcome> NbjAsync(AccountsHost host) => host.AsCallerAsync(null, s =>
        new NbjBc1Seed(s.GetRequiredService<IUnitOfWork>(), s.GetRequiredService<ICurrentUser>(), host.Clock, s.GetRequiredService<AuditTrail>(),
            s.GetRequiredService<IFileStorage>()).RunAsync(Ct));

    /// <summary>Who may act: accounts, their roles and site grants, sessions and integration clients (CWE-269).</summary>
    private async Task<string> AccountsSnapshotAsync() => string.Join("|",
        await _host.ReadAsync<long>("SELECT count(*) FROM \"user\""),
        await _host.ReadAsync<long>("SELECT count(*) FROM user_role"),
        await _host.ReadAsync<long>("SELECT count(*) FROM user_site"),
        await _host.ReadAsync<long>("SELECT count(*) FROM refresh_token"),
        await _host.ReadAsync<long>("SELECT count(*) FROM integration_client"));

    private List<string> StoredFiles() => Directory.Exists(_files) ? [.. Directory.EnumerateFiles(_files, "*", SearchOption.AllDirectories)] : [];

    [Fact]
    public async Task NbjSeed_Should_CreateTheSiteOnceAndChangeNothing_When_RunAgain()
    {
        var host = Host(TestDatabase.NbjSeed);
        await host.CreateUserAsync("it.nbj.reader");
        var accounts = await AccountsSnapshotAsync();
        // Two pods starting together: the advisory lock makes one wait, and it then finds everything in place.
        var together = await Task.WhenAll(NbjAsync(host), NbjAsync(host));
        var afterFirst = await SnapshotAsync();
        var again = await NbjAsync(host);
        var afterSecond = await SnapshotAsync();

        together.Select(o => o.Created).Should().ContainSingle(c => c > 200).And.ContainSingle(c => c == 0);
        together.Should().OnlyContain(o => o.ProfileSkipped == null);
        again.Created.Should().Be(0);
        again.ProfileSkipped.Should().BeNull();
        afterSecond.Should().Be(afterFirst, "a re-run creates, updates and audits nothing");

        // The topology the story defines, flagged illustrative.
        (await host.ReadAsync<string>("SELECT code || '=' || is_illustrative FROM site")).Should().Be("NBJ-BC1=true");
        (await host.ReadAsync<string>("SELECT iata_code || ' ' || time_zone_id FROM airport")).Should().Be("NBJ Africa/Luanda");
        (await host.ReadAsync<string>("SELECT string_agg(a.iata_code || '/' || t.code || '/' || l.code || '/' || c.code || '/' || c.kind || '/' || t.site_code, ',' ORDER BY l.code) " +
                                      "FROM checkpoint c JOIN level l ON l.id = c.level_id JOIN terminal t ON t.id = l.terminal_id JOIN airport a ON a.id = t.airport_id"))
            .Should().Be("NBJ/BC1/ARR/IMM/Immigration/NBJ-BC1,NBJ/BC1/DEP/EMI/Emigration/NBJ-BC1");

        // Desks and e-gates (NbjBc1Layout: 13 double booths, so 26 desks, and 5 e-gates per hall), on lanes ALL and EG.
        (await host.ReadAsync<string>("SELECT string_agg(c.code || ' ' || d.kind || '=' || d.n, ',' ORDER BY c.code, d.kind) FROM " +
                                      "(SELECT checkpoint_id, kind, count(*) AS n FROM desk WHERE site_code = 'NBJ-BC1' GROUP BY checkpoint_id, kind) d JOIN checkpoint c ON c.id = d.checkpoint_id"))
            .Should().Be("EMI Desk=26,EMI EGate=5,IMM Desk=26,IMM EGate=5");
        (await host.ReadAsync<long>("SELECT count(*) FROM desk WHERE site_code = 'NBJ-BC1' AND kind = 'Desk'")).Should().Be(NbjBc1Layout.Halls.Count * NbjBc1Layout.DesksPerRow);
        (await host.ReadAsync<long>("SELECT count(*) FROM desk WHERE site_code = 'NBJ-BC1' AND kind = 'EGate'")).Should().Be(NbjBc1Layout.Halls.Count * NbjBc1Layout.EGatesPerRow);
        (await host.ReadAsync<string>("SELECT string_agg(code, ',' ORDER BY code) FROM desk WHERE site_code = 'NBJ-BC1' AND code IN ('IM-01', 'IM-26', 'EM-01', 'EM-26', 'EGA-01', 'EGA-05', 'EGD-01', 'EGD-05')"))
            .Should().Be("EGA-01,EGA-05,EGD-01,EGD-05,EM-01,EM-26,IM-01,IM-26");
        (await host.ReadAsync<string>("SELECT string_agg(DISTINCT lane_category_codes, ',' ORDER BY lane_category_codes) FROM desk WHERE site_code = 'NBJ-BC1'"))
            .Should().Be("ALL,EG", "no segregation: every desk on ALL, every e-gate on EG");
        (await host.ReadAsync<long>("SELECT count(*) FROM desk_code_mapping")).Should().Be(0, "no AMAN desk codes");

        // Zone profile v1, published by the seed: per hall a shared queue, an overflow band, an e-gates' queue, and a staff
        // and a service zone per desk named after it.
        (await host.ReadAsync<string>("SELECT version || ' ' || status || ' ' || published_by FROM zone_profile WHERE site_code = 'NBJ-BC1'")).Should().Be("1 Published demo-seed");
        (await host.ReadAsync<string>("SELECT string_agg(z.kind || '=' || z.n, ',' ORDER BY z.kind) FROM (SELECT kind, count(*) AS n FROM zone z " +
                                      "JOIN zone_profile p ON p.id = z.profile_id WHERE p.site_code = 'NBJ-BC1' GROUP BY kind) z"))
            .Should().Be("Overflow=2,Queue=4,Service=52,Staff=52");
        (await host.ReadAsync<string>("SELECT string_agg(z.name, ',' ORDER BY z.name) FROM zone z JOIN zone_profile p ON p.id = z.profile_id " +
                                      "WHERE p.site_code = 'NBJ-BC1' AND z.kind IN ('Queue', 'Overflow')"))
            .Should().Be("A-ALL,A-ALL-OV,A-EG,D-ALL,D-ALL-OV,D-EG");
        (await host.ReadAsync<long>("SELECT count(*) FROM zone z JOIN zone_profile p ON p.id = z.profile_id JOIN desk d ON d.id = z.desk_id " +
                                    "WHERE p.site_code = 'NBJ-BC1' AND z.name IN (d.code || ' staff', d.code || ' service')")).Should().Be(104, "each named after its desk");
        (await host.ReadAsync<long>("SELECT count(*) FROM line l JOIN zone_profile p ON p.id = l.profile_id WHERE p.site_code = 'NBJ-BC1'")).Should().Be(10);
        (await host.ReadAsync<long>("SELECT count(*) FROM outbox_message WHERE message_key = 'NBJ-BC1' AND message_type LIKE '%ZoneProfilePublished%'")).Should().Be(1);

        // The sensors of the layout, in commissioning without a credential.
        (await host.ReadAsync<long>("SELECT count(*) FROM device WHERE site_code = 'NBJ-BC1'")).Should().Be(NbjBc1Layout.Sensors().Count).And.Be(48);
        (await host.ReadAsync<long>("SELECT count(*) FROM device WHERE site_code = 'NBJ-BC1' AND (state <> 'Commissioning' OR credential_hash IS NOT NULL)"))
            .Should().Be(0, "no credential is issued and nothing is calibrated");

        // Topology only (CWE-269): no account, role, site grant, session or integration client.
        (await host.ReadAsync<long>("SELECT count(*) FROM \"user\" WHERE created_by = 'demo-seed'")).Should().Be(0);
        (await AccountsSnapshotAsync()).Should().Be(accounts, "the seed creates no account, role, site grant, session or client");
        (await host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE action = 'Seed.IllustrativeTopology'")).Should().Be(1);

        // Ariva's own schematic per level, through the floor plan pipeline: stored unchanged under a generated key.
        (await host.ReadAsync<string>("SELECT string_agg(l.code || ' ' || f.content_type || ' ' || f.width_pixels || 'x' || f.height_pixels || ' ' || f.metres_per_pixel, ',' ORDER BY l.code) " +
                                      "FROM floor_plan f JOIN level l ON l.id = f.level_id WHERE f.site_code = 'NBJ-BC1' AND f.deleted_on IS NULL"))
            .Should().Be("ARR image/svg+xml 1280x800 0.05,DEP image/svg+xml 1280x680 0.05");
        foreach (var hall in NbjBc1Layout.Halls)
        {
            var key = hall.Arrivals
                ? await host.ReadAsync<string>("SELECT f.storage_key FROM floor_plan f JOIN level l ON l.id = f.level_id WHERE f.site_code = 'NBJ-BC1' AND l.code = 'ARR'")
                : await host.ReadAsync<string>("SELECT f.storage_key FROM floor_plan f JOIN level l ON l.id = f.level_id WHERE f.site_code = 'NBJ-BC1' AND l.code = 'DEP'");
            key.Should().MatchRegex("^[0-9a-f]{32}\\.svg$");
            var stored = await host.AsCallerAsync(null, async s =>
            {
                await using var file = await s.GetRequiredService<IFileStorage>().OpenReadAsync(key, Ct);
                using var copy = new MemoryStream();
                await file.CopyToAsync(copy, Ct);
                return copy.ToArray();
            });
            stored.Should().Equal(NbjBc1Plan.Svg(hall), "the schematic passes the inspection unchanged ({0})", hall.LevelCode);
        }

        // The stream links every desk's two zones to its desk (ARV-116): 26 desks per hall, none for the e-gates.
        var source = new ZoneGeometrySource(new DatabaseSettings
        {
            Host = fixture.Hostname, Port = fixture.Port, Name = await host.DatabaseAsync(), Username = fixture.AdminUsername, Password = fixture.AdminPassword
        });
        (await source.LoadAsync("NBJ-BC1", "A-ALL", Ct)).Geometry.DeskZones.Should().HaveCount(52);
        (await source.LoadAsync("NBJ-BC1", "D-ALL", Ct)).Geometry.DeskZones.Should().HaveCount(52);
        (await source.LoadAsync("NBJ-BC1", "A-EG", Ct)).Geometry.DeskZones.Should().BeEmpty("e-gates have no desk zones");
    }

    // CWE-269, CWE-863: NBJ terminal BC1 bound to another site stops the seed, and nothing of it remains: the site it saved
    // before the check is rolled back, nothing else is written or audited, and no plan file is left in storage.
    [Fact]
    public async Task NbjSeed_Should_StopWithoutWriting_When_TerminalBc1BelongsToAnotherSite()
    {
        var host = Host(TestDatabase.NbjSeedForeignTerminal);
        var admin = await host.CreateUserAsync("it.nbj.foreign", roles: [RoleCodes.SystemAdministrator], allSites: true);
        (await host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest("OTH", "Another site"), Ct))).HasErrors.Should().BeFalse();
        var airport = await host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcTopology>()
            .CreateAirportAsync(new CreateAirportRequest(NbjBc1Layout.AirportIata, null, "Another deployment's airport", NbjBc1Layout.TimeZoneId), Ct));
        airport.HasErrors.Should().BeFalse(string.Join(", ", airport.ErrorMessages ?? []));
        var terminal = await host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcTopology>()
            .CreateTerminalAsync(new CreateTerminalRequest(airport.Data.Id, NbjBc1Layout.TerminalCode, "Another site's terminal", "OTH"), Ct));
        terminal.HasErrors.Should().BeFalse(string.Join(", ", terminal.ErrorMessages ?? []));
        var before = await SnapshotAsync();
        var accounts = await AccountsSnapshotAsync();

        var act = () => NbjAsync(host);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*belongs to site OTH*");
        (await SnapshotAsync()).Should().Be(before, "the seed's writes are rolled back: no site, level, desk, zone, profile, plan, sensor or audit entry");
        (await AccountsSnapshotAsync()).Should().Be(accounts);
        (await host.ReadAsync<long>("SELECT count(*) FROM site WHERE code = 'NBJ-BC1'")).Should().Be(0, "the site saved before the check is rolled back");
        (await host.ReadAsync<long>("SELECT count(*) FROM level")).Should().Be(0);
        (await host.ReadAsync<long>("SELECT count(*) FROM desk")).Should().Be(0);
        (await host.ReadAsync<long>("SELECT count(*) FROM zone_profile")).Should().Be(0);
        (await host.ReadAsync<long>("SELECT count(*) FROM floor_plan")).Should().Be(0);
        (await host.ReadAsync<long>("SELECT count(*) FROM device")).Should().Be(0);
        (await host.ReadAsync<string>("SELECT t.site_code || '|' || t.name FROM terminal t JOIN airport a ON a.id = t.airport_id WHERE a.iata_code = 'NBJ' AND t.code = 'BC1'"))
            .Should().Be("OTH|Another site's terminal", "the other site's terminal is untouched");
        StoredFiles().Should().BeEmpty("no plan file is left in storage");
    }

    [Fact]
    public async Task NbjSeed_Should_StopWithoutWriting_When_SiteNbjBc1ExistsAndIsNotIllustrative()
    {
        var host = Host(TestDatabase.NbjSeedReal);
        await host.CreateUserAsync("it.nbj.real");
        (await host.ReadAsync<int>("""
            WITH s AS (INSERT INTO site (id, code, name) VALUES ('0199a000-0000-7000-8000-000000139c01', 'NBJ-BC1', 'A real deployment') RETURNING 1)
            SELECT 1 FROM s
            """)).Should().Be(1);
        var before = await SnapshotAsync();
        var accounts = await AccountsSnapshotAsync();

        var act = () => NbjAsync(host);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not illustrative*");
        (await SnapshotAsync()).Should().Be(before, "nothing is written into a real site");
        (await AccountsSnapshotAsync()).Should().Be(accounts);
        (await host.ReadAsync<long>("SELECT count(*) FROM airport")).Should().Be(0);
        (await host.ReadAsync<long>("SELECT count(*) FROM floor_plan")).Should().Be(0);
        (await host.ReadAsync<string>("SELECT name || '|' || is_illustrative FROM site WHERE code = 'NBJ-BC1'")).Should().Be("A real deployment|false");
        StoredFiles().Should().BeEmpty("no plan file is written");
    }

    #endregion
#endif
}
