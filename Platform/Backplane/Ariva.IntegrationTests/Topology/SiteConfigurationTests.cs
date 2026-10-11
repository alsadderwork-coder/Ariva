using System.Globalization;
using System.Text;
using Ariva.Core;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Security;
using Ariva.Core.Services;
using Ariva.Core.Services.Administration;
using Ariva.Core.Services.Storage;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Storage;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ariva.IntegrationTests.Topology;

/// <summary>
/// ARV-122a against PostgreSQL with the real scripts: a site exported and imported into an empty site at another airport
/// arrives whole with new ids and a draft profile which, once published, has the source's portable geometry hash (its
/// F22 v1 hash differs, as it names levels and desks by id); a site with any configuration is refused; a storage failure
/// on the second of two plan files, after the records and the first file were written, leaves no row and removes the
/// first file, and is audited as failed; a rollback after a successful import (a failed commit) is audited as failed and
/// leaves nothing; of concurrent imports into one empty site one wins and the rest are refused; an SVG plan passes the
/// ARV-018 sanitiser again; a plan file name with a path, a drive or a right-to-left override is refused and one made of
/// markup is kept as text; stored keys are generated; audit entries hold the bundle hash and counts, never a name.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SiteConfigurationTests(PostgresFixture fixture) : IAsyncDisposable
{
    private const string Svg = """<svg xmlns="http://www.w3.org/2000/svg" width="100" height="50"><rect width="10" height="10"/></svg>""";
    private const string HostileName = "<script>alert(1)</script>";

    private readonly AccountsHost _host = new(fixture, database: TestDatabase.Administration,
        configure: services => services.Replace(ServiceDescriptor.Singleton<IFileStorage, SwitchableFileStorage>()));

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ISvcSiteConfiguration Configuration(IServiceProvider s) => s.GetRequiredService<ISvcSiteConfiguration>();

    private static ISvcTopology Topology(IServiceProvider s) => s.GetRequiredService<ISvcTopology>();

    private static string Rect(double x, double y, double w, double h) =>
        string.Create(CultureInfo.InvariantCulture, $"{x} {y},{x + w} {y},{x + w} {y + h},{x} {y + h}");

    private static T Ok<T>(Fluentx.Result<T> result)
    {
        result.HasErrors.Should().BeFalse(string.Join(", ", result.ErrorMessages ?? []));
        return result.Data;
    }

    private async Task<Guid> AdministratorAsync(string name)
    {
        var admin = await _host.CreateUserAsync($"it.cfg.{name}", roles: [RoleCodes.SystemAdministrator]);
        await _host.ReadAsync<int>("UPDATE \"user\" SET all_sites = true WHERE id = @id RETURNING 1", admin);
        return admin;
    }

    private Task SiteAsync(Guid admin, string site) =>
        _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest(site, site), Ct));

    private async Task<Guid> AirportAsync(Guid admin, string iata) =>
        Ok(await _host.AsCallerAsync(admin, s => Topology(s).CreateAirportAsync(new CreateAirportRequest(iata, null, $"Airport {iata}", "Asia/Dubai"), Ct))).Id;

    /// <summary>
    /// A configured source site: two levels, an immigration checkpoint with a desk and an e-gate, a check-in counter, two
    /// desk code mappings, a published profile (a queue, a service zone on the desk, entry, exit and a count line on the
    /// upper level) and an SVG plan; a terminal, a desk and a zone named with markup or SQL.
    /// </summary>
    private async Task SourceAsync(Guid admin, string site, string iata, bool twoPlans = false)
    {
        await SiteAsync(admin, site);
        var airport = await AirportAsync(admin, iata);
        var terminal = Ok(await _host.AsCallerAsync(admin, s => Topology(s).CreateTerminalAsync(new CreateTerminalRequest(airport, "T1", HostileName, site), Ct))).Id;
        var arrivals = Ok(await _host.AsCallerAsync(admin, s => Topology(s).CreateLevelAsync(new CreateLevelRequest(terminal, "L0", "Arrivals", 0, 100, 50), Ct))).Id;
        var departures = Ok(await _host.AsCallerAsync(admin, s => Topology(s).CreateLevelAsync(new CreateLevelRequest(terminal, "L1", "Departures", 1, 80, 40), Ct))).Id;
        var immigration = Ok(await _host.AsCallerAsync(admin, s => Topology(s).CreateCheckpointAsync(new CreateCheckpointRequest(arrivals, "IMM", "Immigration", "Immigration"), Ct))).Id;
        var desk = Ok(await _host.AsCallerAsync(admin, s => Topology(s).CreateDeskAsync(new CreateDeskRequest(immigration, "D01", "' OR '1'='1", "Desk", ["CIT"]), Ct))).Id;
        Ok(await _host.AsCallerAsync(admin, s => Topology(s).CreateDeskAsync(new CreateDeskRequest(immigration, "EG01", null, "EGate", ["EG"]), Ct)));
        var checkIn = Ok(await _host.AsCallerAsync(admin, s => Topology(s).CreateCheckpointAsync(new CreateCheckpointRequest(departures, "CHK", "Check-in", "CheckIn"), Ct))).Id;
        var counter = Ok(await _host.AsCallerAsync(admin, s => Topology(s).CreateDeskAsync(new CreateDeskRequest(checkIn, "C01", null, "Counter"), Ct))).Id;
        var mappings = (IServiceProvider s) => s.GetRequiredService<ISvcDeskCodeMappings>();
        Ok(await _host.AsCallerAsync(admin, s => mappings(s).CreateAsync(new CreateDeskCodeMappingRequest("Aman", "IMM-01", desk), Ct)));
        Ok(await _host.AsCallerAsync(admin, s => mappings(s).CreateAsync(new CreateDeskCodeMappingRequest("Aodb", "C/01", counter), Ct)));

        var profiles = (IServiceProvider s) => s.GetRequiredService<ISvcZoneProfiles>();
        var draft = Ok(await _host.AsCallerAsync(admin, s => profiles(s).CreateDraftAsync(new CreateZoneProfileDraftRequest(site, "Arrivals"), Ct))).Profile.Id;
        var queue = Ok(await _host.AsCallerAsync(admin, s => profiles(s).AddZoneAsync(draft, new AddZoneRequest("Snake A", "Queue", arrivals, Rect(10, 10, 24, 12), LaneCategory: "CIT", PhysicalCapacity: 190), Ct))).Id;
        Ok(await _host.AsCallerAsync(admin, s => profiles(s).AddZoneAsync(draft, new AddZoneRequest("<img src=x onerror=alert(1)>", "Service", arrivals, Rect(40, 10, 4, 4), queue, desk), Ct)));
        Ok(await _host.AsCallerAsync(admin, s => profiles(s).AddLineAsync(draft, new AddLineRequest("Entry A", "Entry", arrivals, 10, 12, 10, 16, queue), Ct)));
        Ok(await _host.AsCallerAsync(admin, s => profiles(s).AddLineAsync(draft, new AddLineRequest("Exit A", "Exit", arrivals, 30, 22, 34, 22, queue), Ct)));
        Ok(await _host.AsCallerAsync(admin, s => profiles(s).AddLineAsync(draft, new AddLineRequest("Count hall", "Count", departures, 1, 1, 1, 9), Ct)));
        await PublishAsync(admin, draft);

        var bytes = Encoding.UTF8.GetBytes(Svg);
        Ok(await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcFloorPlans>().UploadAsync(arrivals, new FloorPlanUpload(new MemoryStream(bytes), bytes.Length, "arrivals.svg", 0.05, 1, 2), Ct)));
        if (twoPlans)
            Ok(await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcFloorPlans>().UploadAsync(departures, new FloorPlanUpload(new MemoryStream(bytes), bytes.Length, "departures.svg", 0.05, 0, 0), Ct)));
    }

    private const string StorageKeyPattern = "^[0-9a-f]{32}\\.(png|jpg|svg)$";

    private async Task<List<string>> StorageKeysAsync(string site)
    {
        await using var connection = new Npgsql.NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand("SELECT storage_key FROM floor_plan WHERE site_code = @site", connection);
        command.Parameters.AddWithValue("site", site);
        var keys = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            keys.Add(reader.GetString(0));
        return keys;
    }

    private static async Task<bool> StoredAsync(IFileStorage storage, string key)
    {
        await using var stream = await storage.OpenReadAsync(key, Ct);
        return stream is not null;
    }

    /// <summary>The bundle with its floorPlans part's file names replaced (the manifest recomputed to match).</summary>
    private static ImportSiteConfigurationRequest Named(SiteConfigurationBundleViewModel bundle, string fileName)
    {
        var rows = System.Text.Json.JsonSerializer.Deserialize<List<FloorPlanPartRow>>(Convert.FromBase64String(bundle.Parts[SiteConfigurationBundles.FloorPlans]), SiteConfigurationParts.Json)!;
        return Rehashed(bundle, SiteConfigurationBundles.FloorPlans, SiteConfigurationParts.Write(rows.Select(r => r with { FileName = fileName }).ToList()));
    }

    private async Task<ZoneProfileSummaryViewModel> PublishAsync(Guid admin, Guid draft)
    {
        var profiles = (IServiceProvider s) => s.GetRequiredService<ISvcZoneProfiles>();
        var reviewed = Ok(await _host.AsCallerAsync(admin, s => profiles(s).ValidateAsync(draft, Ct)));
        reviewed.Publishable.Should().BeTrue(string.Join(", ", reviewed.Problems ?? []));
        return Ok(await _host.AsCallerAsync(admin, s => profiles(s).PublishAsync(draft, new PublishZoneProfileRequest(reviewed.GeometryHash), Ct)));
    }

    private static ImportSiteConfigurationRequest Request(SiteConfigurationBundleViewModel bundle) =>
        new(bundle.FormatVersion, bundle.SourceSite, bundle.ExportedAtUtc, new Dictionary<string, string>(bundle.Manifest), new Dictionary<string, string>(bundle.Parts));

    /// <summary>A bundle whose part is replaced and whose manifest is recomputed to match: anyone can, so the manifest proves no author.</summary>
    private static ImportSiteConfigurationRequest Rehashed(SiteConfigurationBundleViewModel bundle, string part, byte[] bytes)
    {
        var request = Request(bundle);
        request.Parts[part] = Convert.ToBase64String(bytes);
        request.Manifest[part] = SiteConfigurationBundles.Sha256(bytes);
        return request;
    }

    private async Task<long> RecordsAsync(string site) =>
        await _host.ReadAsync<long>("""
            SELECT (SELECT count(*) FROM terminal WHERE site_code = @secret) + (SELECT count(*) FROM level WHERE site_code = @secret)
                 + (SELECT count(*) FROM checkpoint WHERE site_code = @secret) + (SELECT count(*) FROM desk WHERE site_code = @secret)
                 + (SELECT count(*) FROM desk_code_mapping WHERE site_code = @secret) + (SELECT count(*) FROM zone_profile WHERE site_code = @secret)
                 + (SELECT count(*) FROM floor_plan WHERE site_code = @secret)
            """, secret: site);

    [Fact]
    public async Task ExportThenImport_Should_RecreateTheSiteAndKeepThePortableHash_When_TheDraftIsPublishedAtTheTarget()
    {
        var admin = await AdministratorAsync("rt");
        await SourceAsync(admin, "XCFA", "QXA");
        await SiteAsync(admin, "XCFB");
        await AirportAsync(admin, "QXB");

        var exported = Ok(await _host.AsCallerAsync(admin, s => Configuration(s).ExportAsync("XCFA", Ct)));
        var imported = Ok(await _host.AsCallerAsync(admin, s => Configuration(s).ImportAsync("XCFB", Request(exported.Bundle), "QXB", Ct)));

        exported.ZoneProfileIncluded.Should().BeTrue();
        exported.Counts.Should().Be(new SiteConfigurationCountsViewModel(1, 2, 2, 3, 2, 2, 3, 1));
        imported.BundleSha256.Should().Be(exported.BundleSha256, "an operator compares the two out of band");
        imported.Counts.Should().Be(exported.Counts);
        imported.PortableGeometryHash.Should().Be(exported.PortableGeometryHash);
        imported.SourceSite.Should().Be("XCFA");
        (await _host.ReadAsync<string>("SELECT a.iata_code FROM terminal t JOIN airport a ON a.id = t.airport_id WHERE t.site_code = @secret", secret: "XCFB")).Should().Be("QXB");
        (await _host.ReadAsync<string>("SELECT name FROM terminal WHERE site_code = @secret", secret: "XCFB")).Should().Be(HostileName, "a hostile name is stored as data");
        (await _host.ReadAsync<string>("SELECT status FROM zone_profile WHERE site_code = @secret", secret: "XCFB")).Should().Be("Draft");
        // Remapped: every zone of the draft lies on a level of the target and names a desk of the target.
        (await _host.ReadAsync<long>("""
            SELECT count(*) FROM zone z JOIN zone_profile p ON p.id = z.profile_id JOIN level l ON l.id = z.level_id LEFT JOIN desk d ON d.id = z.desk_id
            WHERE p.site_code = 'XCFB' AND (l.site_code <> 'XCFB' OR d.site_code <> 'XCFB')
            """)).Should().Be(0);
        (await _host.ReadAsync<long>("SELECT count(*) FROM zone z JOIN zone_profile p ON p.id = z.profile_id WHERE p.site_code = 'XCFB' AND z.desk_id IS NOT NULL"))
            .Should().Be(1);
        (await _host.ReadAsync<bool>("SELECT in_service FROM desk WHERE site_code = @secret AND code = 'EG01'", secret: "XCFB")).Should().BeTrue();
        (await _host.ReadAsync<string>("SELECT d.code FROM desk_code_mapping m JOIN desk d ON d.id = m.desk_id WHERE m.site_code = @secret AND m.external_code = 'IMM-01'", secret: "XCFB"))
            .Should().Be("D01");
        (await _host.ReadAsync<string>("SELECT sha256 FROM floor_plan WHERE site_code = @secret", secret: "XCFB"))
            .Should().Be(await _host.ReadAsync<string>("SELECT sha256 FROM floor_plan WHERE site_code = @secret AND deleted_on IS NULL", secret: "XCFA"),
                "the stored SVG passes the sanitiser again unchanged");

        (await StorageKeysAsync("XCFB")).Should().ContainSingle().Which.Should().MatchRegex(StorageKeyPattern, "the key is generated, never a name from the bundle");

        var published = await PublishAsync(admin, imported.DraftProfileId!.Value);
        var again = Ok(await _host.AsCallerAsync(admin, s => Configuration(s).ExportAsync("XCFB", Ct)));
        again.PortableGeometryHash.Should().Be(exported.PortableGeometryHash, "the round trip keeps the portable geometry hash");
        published.GeometryHash.Should().NotBe(exported.GeometryHash, "F22 v1 names levels and desks by their database ids");
        again.Counts.Should().Be(exported.Counts);

        var audit = await _host.ReadAsync<string>("SELECT after_summary FROM audit_entry WHERE action = 'SiteConfiguration.Imported' AND target_name = @secret", secret: "XCFB");
        audit.Should().Contain(exported.BundleSha256).And.Contain("\"outcome\":\"imported\"").And.Contain("\"desks\":3");
        audit.Should().NotContain("script").And.NotContain("onerror").And.NotContain("OR '1'", "the audit holds no part content");
        (await _host.ReadAsync<string>("SELECT after_summary FROM audit_entry WHERE action = 'SiteConfiguration.Exported' AND target_name = @secret", secret: "XCFA"))
            .Should().Contain(exported.BundleSha256);
    }

    [Fact]
    public async Task Import_Should_RefuseASiteThatIsNotEmpty_When_ItHoldsAnyConfiguration()
    {
        var admin = await AdministratorAsync("ne");
        await SourceAsync(admin, "XCFC", "QXC");
        var bundle = Ok(await _host.AsCallerAsync(admin, s => Configuration(s).ExportAsync("XCFC", Ct))).Bundle;
        await SiteAsync(admin, "XCFD");
        var airport = await AirportAsync(admin, "QXD");
        Ok(await _host.AsCallerAsync(admin, s => Topology(s).CreateTerminalAsync(new CreateTerminalRequest(airport, "T9", "Already here", "XCFD"), Ct)));
        await SiteAsync(admin, "XCFE");
        Ok(await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcZoneProfiles>().CreateDraftAsync(new CreateZoneProfileDraftRequest("XCFE", "An empty draft"), Ct)));

        (await _host.AsCallerAsync(admin, s => Configuration(s).ImportAsync("XCFD", Request(bundle), "QXD", Ct))).ErrorMessages
            .Should().Equal(SiteConfigurationErrors.SiteNotEmpty);
        (await _host.AsCallerAsync(admin, s => Configuration(s).ImportAsync("XCFE", Request(bundle), "QXD", Ct))).ErrorMessages
            .Should().Equal([SiteConfigurationErrors.SiteNotEmpty], "an empty draft profile is configuration too");
        (await _host.AsCallerAsync(admin, s => Configuration(s).ImportAsync("XCFC", Request(bundle), null, Ct))).ErrorMessages
            .Should().Equal([SiteConfigurationErrors.SiteNotEmpty], "nor into its own source");
        (await RecordsAsync("XCFD")).Should().Be(1, "only the terminal that was there");
        (await _host.ReadAsync<string>("SELECT after_summary FROM audit_entry WHERE action = 'SiteConfiguration.ImportRefused' AND target_name = @secret", secret: "XCFD"))
            .Should().Contain("\"outcome\":\"refused\"").And.Contain(SiteConfigurationErrors.SiteNotEmpty);
    }

    [Fact]
    public async Task Import_Should_LeaveNothingBehind_When_AFileWriteFailsAfterRecordsAndAnotherFileWereWritten()
    {
        var admin = await AdministratorAsync("rb");
        await SourceAsync(admin, "XCFF", "QXF", twoPlans: true);
        var exported = Ok(await _host.AsCallerAsync(admin, s => Configuration(s).ExportAsync("XCFF", Ct)));
        exported.Counts.FloorPlans.Should().Be(2);
        var bundle = exported.Bundle;
        await SiteAsync(admin, "XCFG");
        await AirportAsync(admin, "QXG");
        var storage = (SwitchableFileStorage)_host.Provider.GetRequiredService<IFileStorage>();

        // The plan files are written last, after the topology, mappings and profile: the first is stored, the second fails.
        storage.Arm(failAt: 2);
        try
        {
            var failing = () => _host.AsCallerAsync(admin, s => Configuration(s).ImportAsync("XCFG", Request(bundle), "QXG", Ct));
            await failing.Should().ThrowAsync<IOException>();
        }
        finally
        {
            storage.Disarm();
        }

        var written = storage.Saved.Should().ContainSingle("the first plan's file was stored before the second failed").Subject;
        (await StoredAsync(storage, written)).Should().BeFalse("the rollback removes the file it stored");
        (await RecordsAsync("XCFG")).Should().Be(0, "the import is all or nothing");
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE action = 'SiteConfiguration.Imported' AND target_name = @secret", secret: "XCFG")).Should().Be(0);
        (await _host.ReadAsync<string>("SELECT after_summary FROM audit_entry WHERE action = 'SiteConfiguration.ImportFailed' AND target_name = @secret", secret: "XCFG"))
            .Should().Contain("\"outcome\":\"failed\"").And.Contain(exported.BundleSha256);

        // A part refused by its checks (a file that is not an image, the manifest recomputed to match) writes nothing either.
        var notAnImage = Rehashed(bundle, SiteConfigurationBundles.FloorPlanFile(2), "not an image"u8.ToArray());
        (await _host.AsCallerAsync(admin, s => Configuration(s).ImportAsync("XCFG", notAnImage, "QXG", Ct))).ErrorMessages
            .Should().Equal(SiteConfigurationErrors.FloorPlanFileRefused);
        (await RecordsAsync("XCFG")).Should().Be(0);

        // Then the same bundle imports whole.
        Ok(await _host.AsCallerAsync(admin, s => Configuration(s).ImportAsync("XCFG", Request(bundle), "QXG", Ct)));
        (await RecordsAsync("XCFG")).Should().Be(1 + 2 + 2 + 3 + 2 + 1 + 2);
        (await StorageKeysAsync("XCFG")).Should().HaveCount(2).And.OnlyContain(key => System.Text.RegularExpressions.Regex.IsMatch(key, StorageKeyPattern));
    }

    [Fact]
    public async Task Import_Should_BeAuditedAsFailedAndLeaveNothing_When_ItIsRolledBackAfterItsRecordsWereWritten()
    {
        // As when the commit fails: the unit of work rolls back after a successful import and runs its post-rollback actions.
        var admin = await AdministratorAsync("cm");
        await SourceAsync(admin, "XCFP", "QXP");
        var bundle = Ok(await _host.AsCallerAsync(admin, s => Configuration(s).ExportAsync("XCFP", Ct))).Bundle;
        await SiteAsync(admin, "XCFQ");
        await AirportAsync(admin, "QXQ");
        var storage = (SwitchableFileStorage)_host.Provider.GetRequiredService<IFileStorage>();
        storage.Arm(failAt: 0);

        await using (var scope = _host.Provider.CreateAsyncScope())
        {
            var caller = (TestCurrentUser)scope.ServiceProvider.GetRequiredService<ICurrentUser>();
            caller.Id = admin;
            caller.UserName = "it-admin";
            Ok(await Configuration(scope.ServiceProvider).ImportAsync("XCFQ", Request(bundle), "QXQ", Ct));
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().RollbackAsync(Ct);
        }

        (await RecordsAsync("XCFQ")).Should().Be(0);
        (await StoredAsync(storage, storage.Saved.Should().ContainSingle().Subject)).Should().BeFalse("the rollback removes the stored file");
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE action = 'SiteConfiguration.Imported' AND target_name = @secret", secret: "XCFQ")).Should().Be(0);
        (await _host.ReadAsync<string>("SELECT after_summary FROM audit_entry WHERE action = 'SiteConfiguration.ImportFailed' AND target_name = @secret", secret: "XCFQ"))
            .Should().Contain("\"outcome\":\"failed\"").And.Contain("rolled back after its records were written");
    }

    [Fact]
    public async Task Import_Should_RefusePathLikeFileNamesAndKeepMarkupAsText_When_TheBundleNamesItsPlan()
    {
        var admin = await AdministratorAsync("fn");
        await SourceAsync(admin, "XCFN", "QXN");
        var bundle = Ok(await _host.AsCallerAsync(admin, s => Configuration(s).ExportAsync("XCFN", Ct))).Bundle;
        await SiteAsync(admin, "XCFO");
        await AirportAsync(admin, "QXO");

        // CWE-22 and CWE-79 again on import, as on upload: a path, a backslash, a drive, a right-to-left override.
        foreach (var name in new[] { "../../etc/x.svg", "a\\b.svg", "c:x.svg", "plan\u202Egvs.png" })
        {
            (await _host.AsCallerAsync(admin, s => Configuration(s).ImportAsync("XCFO", Named(bundle, name), "QXO", Ct))).ErrorMessages
                .Should().Equal([SiteConfigurationErrors.FloorPlanFileRefused], name);
            (await RecordsAsync("XCFO")).Should().Be(0, name);
        }

        const string markup = "<img src=x onerror=alert(1)>.svg";
        Ok(await _host.AsCallerAsync(admin, s => Configuration(s).ImportAsync("XCFO", Named(bundle, markup), "QXO", Ct)));
        (await _host.ReadAsync<string>("SELECT original_file_name FROM floor_plan WHERE site_code = @secret", secret: "XCFO")).Should().Be(markup, "kept as data");
        Ok(await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcFloorPlans>().ListAsync("XCFO", Ct))).Should().ContainSingle()
            .Which.OriginalFileName.Should().Be(markup, "answered as a text value, never as markup");
        (await StorageKeysAsync("XCFO")).Should().ContainSingle().Which.Should().MatchRegex(StorageKeyPattern, "the key never comes from the name");
    }

    [Fact]
    public async Task Import_Should_SanitiseAnSvgPlanAgain_When_TheBundleCarriesAHostileOne()
    {
        var admin = await AdministratorAsync("sv");
        await SourceAsync(admin, "XCFH", "QXH");
        var bundle = Ok(await _host.AsCallerAsync(admin, s => Configuration(s).ExportAsync("XCFH", Ct))).Bundle;
        await SiteAsync(admin, "XCFI");
        await AirportAsync(admin, "QXI");
        var hostile = """<svg xmlns="http://www.w3.org/2000/svg" width="100" height="50" onload="alert(1)"><script>alert(2)</script><rect width="10" height="10"/></svg>"""u8.ToArray();

        var imported = Ok(await _host.AsCallerAsync(admin, s => Configuration(s).ImportAsync("XCFI", Rehashed(bundle, SiteConfigurationBundles.FloorPlanFile(1), hostile), "QXI", Ct)));

        imported.Counts.FloorPlans.Should().Be(1);
        var key = await _host.ReadAsync<string>("SELECT storage_key FROM floor_plan WHERE site_code = @secret", secret: "XCFI");
        await using var stored = await _host.Provider.GetRequiredService<IFileStorage>().OpenReadAsync(key, Ct);
        using var reader = new StreamReader(stored);
        var text = await reader.ReadToEndAsync(Ct);
        text.Should().Contain("<rect").And.NotContain("script").And.NotContain("onload");
    }

    [Fact]
    public async Task Import_Should_LetOneOfTwoConcurrentImportsWin_When_BothTargetOneEmptySite()
    {
        var admin = await AdministratorAsync("cc");
        await SourceAsync(admin, "XCFJ", "QXJ");
        var bundle = Ok(await _host.AsCallerAsync(admin, s => Configuration(s).ExportAsync("XCFJ", Ct))).Bundle;
        await SiteAsync(admin, "XCFK");
        await AirportAsync(admin, "QXK");

        // Without the site row lock, imports that overlap would all see the site empty and the losers would fail on the
        // terminal's unique index (TerminalTaken); with it, each later one waits and finds the winner's records.
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            _host.AsCallerAsync(admin, s => Configuration(s).ImportAsync("XCFK", Request(bundle), "QXK", Ct))));

        results.Count(r => !r.HasErrors).Should().Be(1, "the site row lock lets one import see the site empty");
        results.Where(r => r.HasErrors).Should().OnlyContain(r => r.ErrorMessages.SequenceEqual(new[] { SiteConfigurationErrors.SiteNotEmpty }));
        (await _host.ReadAsync<long>("SELECT count(*) FROM terminal WHERE site_code = @secret", secret: "XCFK")).Should().Be(1);
        (await _host.ReadAsync<long>("SELECT count(*) FROM zone_profile WHERE site_code = @secret", secret: "XCFK")).Should().Be(1);
    }

    [Fact]
    public async Task ExportAndImport_Should_AnswerNotFound_When_TheSiteIsOutOfTheCallersReach()
    {
        var admin = await AdministratorAsync("sc");
        await SourceAsync(admin, "XCFL", "QXL");
        var bundle = Ok(await _host.AsCallerAsync(admin, s => Configuration(s).ExportAsync("XCFL", Ct))).Bundle;
        await SiteAsync(admin, "XCFM");
        var scoped = await _host.CreateUserAsync("it.cfg.scoped", roles: [RoleCodes.SystemAdministrator]);
        Ok(await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcUsers>().SetSitesAsync(scoped, new SiteAccessRequest(false, ["XCFL"]), Ct)));

        (await _host.AsCallerAsync(scoped, s => Configuration(s).ExportAsync("XCFM", Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(scoped, s => Configuration(s).ImportAsync("XCFM", Request(bundle), "QXL", Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(scoped, s => Configuration(s).ExportAsync("XCFL", Ct))).HasErrors.Should().BeFalse("its own site");
        (await RecordsAsync("XCFM")).Should().Be(0);
    }

    /// <summary>
    /// Local disk storage that records the keys it stored since it was armed and can fail the Nth write, to break an import
    /// after its records and some of its files were written.
    /// </summary>
    private sealed class SwitchableFileStorage(IConfiguration configuration) : IFileStorage
    {
        private readonly LocalDiskFileStorage _inner = new(configuration);
        private int _failAt;
        private int _writes;

        public List<string> Saved { get; } = [];

        /// <summary>Starts counting writes again; the write numbered <paramref name="failAt"/> (from 1) fails, none for 0.</summary>
        public void Arm(int failAt)
        {
            _failAt = failAt;
            _writes = 0;
            Saved.Clear();
        }

        /// <summary>No write fails any more; the keys stored so far stay recorded.</summary>
        public void Disarm() => _failAt = 0;

        public async Task SaveAsync(string key, Stream content, CancellationToken ct = default)
        {
            if (_failAt > 0 && ++_writes == _failAt)
                throw new IOException("The test's storage refuses this write.");
            await _inner.SaveAsync(key, content, ct);
            Saved.Add(key);
        }

        public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default) => _inner.OpenReadAsync(key, ct);

        public Task DeleteAsync(string key, CancellationToken ct = default) => _inner.DeleteAsync(key, ct);
    }
}
