using System.Globalization;
using Ariva.Core;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Security;
using Ariva.Core.Services.Administration;
using Ariva.Core.Services.Sensing;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Settings;
using Ariva.Infra.Timescale;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ariva.IntegrationTests.Topology;

/// <summary>
/// ARV-159 against PostgreSQL with the real scripts, in a database of its own: the site, topology and zone profile services
/// refuse a name with a control, invisible format or broken character with one fixed text and store nothing, and take
/// Arabic names; the pre-pilot check lists exactly the stored values that break their rule (written past the services, as
/// values accepted before ARV-158, ARV-159 and ARV-161 would be), escaped, and cannot write. ARV-162: a draft that copied a
/// legacy name or outline cannot be published until it is fixed, and a legacy topology name refuses the export. Invisible
/// characters here are escapes.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class StoredNameTests(PostgresFixture fixture) : IAsyncDisposable
{
    private const string Override = "\x202E";
    private const string Tag = "\U000E0041";

    /// <summary>"Arrivals hall" in Arabic.</summary>
    private const string Arabic = "\x0635\x0627\x0644\x0629 \x0627\x0644\x0648\x0635\x0648\x0644";

    private readonly AccountsHost _host = new(fixture, database: TestDatabase.StoredNames);

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static T Ok<T>(Fluentx.Result<T> result)
    {
        result.HasErrors.Should().BeFalse(string.Join(", ", result.ErrorMessages ?? []));
        return result.Data;
    }

    private static string Rect(double x, double y, double w, double h) =>
        string.Create(CultureInfo.InvariantCulture, $"{x} {y},{x + w} {y},{x + w} {y + h},{x} {y + h}");

    private async Task<Guid> AdministratorAsync(string name)
    {
        var admin = await _host.CreateUserAsync($"it.names.{name}", roles: [RoleCodes.SystemAdministrator]);
        await _host.ReadAsync<int>("UPDATE \"user\" SET all_sites = true WHERE id = @id RETURNING 1", admin);
        return admin;
    }

    /// <summary>A site with one level and a published profile: queue Snake A with its entry and exit lines. Returns the level.</summary>
    private async Task<(Guid Airport, Guid Terminal, Guid Level)> SiteAsync(Guid admin, string site)
    {
        Ok(await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest(site, site), Ct)));
        var topology = (IServiceProvider s) => s.GetRequiredService<ISvcTopology>();
        var airport = Ok(await _host.AsCallerAsync(admin, s => topology(s).CreateAirportAsync(new CreateAirportRequest(site[1..], null, $"Airport {site}", "Asia/Dubai"), Ct))).Id;
        var terminal = Ok(await _host.AsCallerAsync(admin, s => topology(s).CreateTerminalAsync(new CreateTerminalRequest(airport, "T1", "T1", site), Ct))).Id;
        var level = Ok(await _host.AsCallerAsync(admin, s => topology(s).CreateLevelAsync(new CreateLevelRequest(terminal, "L0", "Arrivals", 0, 100, 50), Ct))).Id;
        var profiles = (IServiceProvider s) => s.GetRequiredService<ISvcZoneProfiles>();
        var draft = Ok(await _host.AsCallerAsync(admin, s => profiles(s).CreateDraftAsync(new CreateZoneProfileDraftRequest(site, "Arrivals"), Ct))).Profile.Id;
        var queue = Ok(await _host.AsCallerAsync(admin, s => profiles(s).AddZoneAsync(draft, new AddZoneRequest("Snake A", "Queue", level, Rect(10, 10, 24, 12)), Ct))).Id;
        Ok(await _host.AsCallerAsync(admin, s => profiles(s).AddLineAsync(draft, new AddLineRequest("Entry A", "Entry", level, 10, 12, 10, 16, queue), Ct)));
        Ok(await _host.AsCallerAsync(admin, s => profiles(s).AddLineAsync(draft, new AddLineRequest("Exit A", "Exit", level, 30, 22, 34, 22, queue), Ct)));
        var reviewed = Ok(await _host.AsCallerAsync(admin, s => profiles(s).ValidateAsync(draft, Ct)));
        Ok(await _host.AsCallerAsync(admin, s => profiles(s).PublishAsync(draft, new PublishZoneProfileRequest(reviewed.GeometryHash), Ct)));
        return (airport, terminal, level);
    }

    [Fact]
    public async Task Sites_Should_RefuseAnUnsafeNameWithAFixedTextAndTakeArabic_When_CreatedOrRenamed()
    {
        var admin = await AdministratorAsync("site");
        var sites = (IServiceProvider s) => s.GetRequiredService<ISvcSites>();

        (await _host.AsCallerAsync(admin, s => sites(s).CreateAsync(new CreateSiteRequest("XSNA", "Arrivals" + Override + "evil"), Ct))).ErrorMessages
            .Should().Equal(AdministrationErrors.InvalidSiteName);
        (await _host.ReadAsync<long>("SELECT count(*) FROM site WHERE code = 'XSNA'")).Should().Be(0, "nothing stored");

        Ok(await _host.AsCallerAsync(admin, s => sites(s).CreateAsync(new CreateSiteRequest("XSNA", Arabic), Ct))).Name.Should().Be(Arabic);
        (await _host.AsCallerAsync(admin, s => sites(s).UpdateAsync("XSNA", new UpdateSiteRequest("Hall" + Tag), Ct))).ErrorMessages
            .Should().Equal(AdministrationErrors.InvalidSiteName);
        (await _host.AsCallerAsync(admin, s => sites(s).UpdateAsync("XSNB", new UpdateSiteRequest("Hall" + Tag), Ct))).ErrorMessages
            .Should().Equal([AdministrationErrors.NotFound], "a site that does not exist answers as before, whatever the name");
        (await _host.ReadAsync<string>("SELECT name FROM site WHERE code = 'XSNA'")).Should().Be(Arabic, "a refused rename keeps the name");
    }

    [Fact]
    public async Task TopologyAndProfiles_Should_RefuseAnUnsafeNameWithTheFixedText_When_CreatedOrRenamed()
    {
        var admin = await AdministratorAsync("topo");
        var (airport, terminal, level) = await SiteAsync(admin, "XSNC");
        var topology = (IServiceProvider s) => s.GetRequiredService<ISvcTopology>();
        var profiles = (IServiceProvider s) => s.GetRequiredService<ISvcZoneProfiles>();

        (await _host.AsCallerAsync(admin, s => topology(s).CreateTerminalAsync(new CreateTerminalRequest(airport, "T2", "T2" + Tag, "XSNC"), Ct))).ErrorMessages
            .Should().Equal(TopologyErrors.UnsafeName);
        (await _host.AsCallerAsync(admin, s => topology(s).UpdateTerminalAsync(terminal, new UpdateTerminalRequest("T1" + Override), Ct))).ErrorMessages
            .Should().Equal(TopologyErrors.UnsafeName);
        Ok(await _host.AsCallerAsync(admin, s => topology(s).CreateLevelAsync(new CreateLevelRequest(terminal, "L1", Arabic, 1, 100, 50), Ct))).Name.Should().Be(Arabic);

        var draft = Ok(await _host.AsCallerAsync(admin, s => profiles(s).CreateDraftAsync(new CreateZoneProfileDraftRequest("XSNC", "Next"), Ct))).Profile.Id;
        (await _host.AsCallerAsync(admin, s => profiles(s).AddZoneAsync(draft, new AddZoneRequest("Band" + "\x200B", "Queue", level, Rect(40, 10, 24, 12)), Ct))).ErrorMessages
            .Should().Equal(TopologyErrors.UnsafeName);
        (await _host.AsCallerAsync(admin, s => profiles(s).RenameAsync(draft, new RenameZoneProfileRequest("Next" + Tag), Ct))).ErrorMessages
            .Should().Equal(TopologyErrors.UnsafeName);

        (await _host.ReadAsync<long>("SELECT count(*) FROM terminal WHERE site_code = 'XSNC'")).Should().Be(1);
        (await _host.ReadAsync<string>("SELECT name FROM terminal WHERE site_code = 'XSNC'")).Should().Be("T1");
        (await _host.ReadAsync<long>("SELECT count(*) FROM zone z JOIN zone_profile p ON p.id = z.profile_id WHERE p.site_code = 'XSNC' AND z.name LIKE 'Band%'"))
            .Should().Be(0);
        (await _host.ReadAsync<string>("SELECT name FROM zone_profile WHERE site_code = 'XSNC' AND status = 'Draft'")).Should().Be("Next");
    }

    [Fact]
    public async Task Check_Should_ListExactlyTheStoredValuesThatBreakTheirRuleEscapedAndNeverWrite_When_Run()
    {
        var admin = await AdministratorAsync("check");
        var (_, _, level) = await SiteAsync(admin, "XSND");
        Ok(await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcDevices>().RegisterAsync(new RegisterDeviceRequest("S-01", "StereoVision", "PC2SE", "HttpsPush",
            "Xovis", "Ntp", new DevicePlacement(level, 20, 16, 5, 0, "Snake A")), Ct)));
        Ok(await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcZoneProfiles>().CreateDraftAsync(new CreateZoneProfileDraftRequest("XSND", "Next"), Ct)));

        // Values accepted before ARV-158, ARV-159 and ARV-161, written past the services as such a database would hold them.
        await _host.ReadAsync<int>("UPDATE site SET name = @secret WHERE code = 'XSND' RETURNING 1", secret: "Site" + Override + "D");
        await _host.ReadAsync<int>("UPDATE terminal SET name = @secret WHERE site_code = 'XSND' RETURNING 1", secret: "T1" + Tag);
        await _host.ReadAsync<int>("""
            UPDATE zone SET name = @secret, polygon = '10.000 10.000,34.000 10.000,34.000 22.000,22.000 10.005,10.000 22.000'
             WHERE name = 'Snake A' AND profile_id = (SELECT id FROM zone_profile WHERE site_code = 'XSND' AND status = 'Draft')
            RETURNING 1
            """, secret: "Snake\x200BA");
        await _host.ReadAsync<int>("""
            UPDATE line SET name = @secret
             WHERE name = 'Entry A' AND profile_id = (SELECT id FROM zone_profile WHERE site_code = 'XSND' AND status = 'Draft')
            RETURNING 1
            """, secret: "Entry\x0007A");
        await _host.ReadAsync<int>("UPDATE device SET queue_zone_name = @secret WHERE site_code = 'XSND' RETURNING 1", secret: "Snake A" + Tag);
        await _host.ReadAsync<int>("UPDATE device SET model = @secret WHERE site_code = 'XSND' RETURNING 1", secret: "PC2" + Override + "SE");
        await _host.ReadAsync<int>("UPDATE device SET line_coverage = 'Listed', covered_lines = @secret WHERE site_code = 'XSND' RETURNING 1",
            secret: "Exit A\nEntry" + Override + "B");
        await _host.ReadAsync<int>("UPDATE \"user\" SET display_name = @secret WHERE id = @id RETURNING 1", admin, "Admin" + Tag);
        await _host.ReadAsync<int>("""
            INSERT INTO floor_plan (id, level_id, site_code, storage_key, content_type, size_bytes, sha256, original_file_name, metres_per_pixel, origin_x, origin_y)
            VALUES (gen_random_uuid(), @id, 'XSND', '0199a0000000700080000000000162aa.svg', 'image/svg+xml', 10, repeat('a', 64), @secret, 0.05, 0, 0)
            RETURNING 1
            """, level, "arrivals" + Tag + ".svg");
        var before = await _host.ReadAsync<string>("SELECT md5(string_agg(name, '|' ORDER BY id)) FROM site");

        var database = await _host.DatabaseAsync();
        var settings = new DatabaseSettings { Host = fixture.Hostname, Port = fixture.Port, Name = database, Username = fixture.AdminUsername, Password = fixture.AdminPassword };
        var result = await new StoredNameCheck(settings).RunAsync(Ct);

        result.Findings.Where(f => f.SiteCode == "XSND").Select(f => (f.Place, f.State, f.Escaped)).Should().BeEquivalentTo(new[]
        {
            ("site.name", "live", "Site\\u202ED"),
            ("terminal.name", "live", "T1\\U000E0041"),
            ("zone.name", "draft", "Snake\\u200BA"),
            ("zone.polygon", "draft", "Snake\\u200BA"),
            ("line.name", "draft", "Entry\\u0007A"),
            ("device.queue_zone_name", "live", "Snake A\\U000E0041"),
            ("device.model", "live", "PC2\\u202ESE"),
            ("device.covered_lines", "live", "Entry\\u202EB"),
            ("floor_plan.original_file_name", "live", "arrivals\\U000E0041.svg")
        });
        result.Findings.Should().ContainSingle(f => f.SiteCode == null).Which.Should().Be(new StoredNameFinding("user.display_name", null, admin, "live", "U+E0041 at 6 (text not shown)"));
        // XSNE is the publication test's site, which may run first and leaves a legacy terminal name behind.
        result.Findings.Should().OnlyContain(f => f.SiteCode == "XSND" || f.SiteCode == "XSNE" || f.Id == admin,
            "every other stored value in this database passes, the Arabic ones included");
        result.Checked.Should().BeGreaterThan(result.Findings.Count);
        (await _host.ReadAsync<string>("SELECT md5(string_agg(name, '|' ORDER BY id)) FROM site")).Should().Be(before, "the check only reads");

        // Its connection cannot write, whatever a future statement would try.
        await using var connection = new NpgsqlConnection(StoredNameCheck.ReadOnlyConnectionString(settings));
        await connection.OpenAsync(Ct);
        await using var write = new NpgsqlCommand("UPDATE site SET name = name WHERE code = 'XSND'", connection);
        var refused = async () => await write.ExecuteNonQueryAsync(Ct);
        (await refused.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("25006", "read_only_sql_transaction");
    }

    [Fact]
    public async Task Publication_Should_BeBlockedByALegacyNameOrOutlineNamedEscapedUntilFixed_When_TheDraftCopiedThem()
    {
        // ARV-162: a draft copies the published version's zones and lines as stored; a name or outline the rules now refuse
        // blocks publication, named escaped; renaming the zone, moving it and adding the line again lets it publish.
        var admin = await AdministratorAsync("publish");
        var (_, terminal, level) = await SiteAsync(admin, "XSNE");
        var profiles = (IServiceProvider s) => s.GetRequiredService<ISvcZoneProfiles>();
        var draft = Ok(await _host.AsCallerAsync(admin, s => profiles(s).CreateDraftAsync(new CreateZoneProfileDraftRequest("XSNE", "Next"), Ct))).Profile.Id;
        await _host.ReadAsync<int>("""
            UPDATE zone SET name = @secret, polygon = '10.000 10.000,34.000 10.000,34.000 22.000,22.000 10.005,10.000 22.000'
             WHERE name = 'Snake A' AND profile_id = (SELECT id FROM zone_profile WHERE site_code = 'XSNE' AND status = 'Draft')
            RETURNING 1
            """, secret: "Snake" + Override + "A");
        await _host.ReadAsync<int>("""
            UPDATE line SET name = @secret
             WHERE name = 'Entry A' AND profile_id = (SELECT id FROM zone_profile WHERE site_code = 'XSNE' AND status = 'Draft')
            RETURNING 1
            """, secret: "Entry" + Tag);

        var copied = Ok(await _host.AsCallerAsync(admin, s => profiles(s).GetAsync(draft, Ct)));
        var queue = copied.Zones.Single(z => z.Kind == "Queue");
        var problems = Ok(await _host.AsCallerAsync(admin, s => profiles(s).ValidateAsync(draft, Ct)));

        problems.Publishable.Should().BeFalse();
        problems.Problems.Should().Contain(p => p.StartsWith("Zone Snake\\u202EA: its name holds a control", StringComparison.Ordinal));
        problems.Problems.Should().Contain(p => p.StartsWith("Zone Snake\\u202EA: its outline touches or crosses itself", StringComparison.Ordinal));
        problems.Problems.Should().Contain(p => p.StartsWith("Line Entry\\U000E0041: its name holds a control", StringComparison.Ordinal));
        problems.Problems.Should().OnlyContain(p => p.All(c => c >= ' ' && c <= '~'), "a legacy name is never shown as stored");
        (await _host.AsCallerAsync(admin, s => profiles(s).PublishAsync(draft, new PublishZoneProfileRequest(problems.GeometryHash), Ct))).ErrorMessages
            .Should().StartWith(ZoneProfileErrors.NotPublishable, "a legacy name or outline blocks publication");

        Ok(await _host.AsCallerAsync(admin, s => profiles(s).UpdateZoneAsync(draft, queue.Id, new UpdateZoneRequest("Snake A", Rect(10, 10, 24, 12)), Ct)));
        var legacyLine = copied.Lines.Single(l => l.Role == "Entry");
        Ok(await _host.AsCallerAsync(admin, s => profiles(s).RemoveLineAsync(draft, legacyLine.Id, Ct)));
        Ok(await _host.AsCallerAsync(admin, s => profiles(s).AddLineAsync(draft, new AddLineRequest("Entry A", "Entry", level, 10, 12, 10, 16, queue.Id), Ct)));
        var reviewed = Ok(await _host.AsCallerAsync(admin, s => profiles(s).ValidateAsync(draft, Ct)));
        reviewed.Problems.Should().BeEmpty();
        Ok(await _host.AsCallerAsync(admin, s => profiles(s).PublishAsync(draft, new PublishZoneProfileRequest(reviewed.GeometryHash), Ct)));

        // A legacy topology name refuses the export instead of giving a bundle the import would refuse.
        await _host.ReadAsync<int>("UPDATE terminal SET name = @secret WHERE id = @id RETURNING 1", terminal, "T1" + Override);
        (await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSiteConfiguration>().ExportAsync("XSNE", Ct))).ErrorMessages
            .Should().Equal(SiteConfigurationErrors.TopologyNamesNotImportable);
    }
}
