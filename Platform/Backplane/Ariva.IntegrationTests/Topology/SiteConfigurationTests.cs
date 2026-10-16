using System.Globalization;
using System.Text;
using Ariva.Core;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Security;
using Ariva.Core.Services;
using Ariva.Core.Services.Administration;
using Ariva.Core.Services.Alerting;
using Ariva.Core.Services.Displays;
using Ariva.Core.Services.Sensing;
using Ariva.Core.Services.Storage;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Security;
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
/// ARV-122b: devices arrive in Commissioning through the registration path with new working credentials (the source's
/// do not work there, and no calibration or access setting travels); alert rules keep their codes and lose their
/// escalation contacts, which the answer lists by code; displays arrive unpaired; a display code a live display holds
/// answers the conflict and writes nothing; a rule naming a zone the imported draft lacks is refused.
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
    /// desk code mappings, a published profile (a queue with its overflow band, a service zone on the desk, entry, exit,
    /// overflow entry and a count line on the upper level) and an SVG plan; a calibrated device with access settings, two
    /// alert rules (R-001 with an escalation contact, R-002 on the overflow band) and, when a code is given, a paired
    /// display; a terminal, a desk, a zone, a rule and a display named with markup or SQL. Answers the device's credential.
    /// </summary>
    private async Task<string> SourceAsync(Guid admin, string site, string iata, bool twoPlans = false, string displayCode = null)
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
        var overflow = Ok(await _host.AsCallerAsync(admin, s => profiles(s).AddZoneAsync(draft, new AddZoneRequest("Overflow A", "Overflow", arrivals, Rect(10, 24, 24, 4), queue), Ct))).Id;
        Ok(await _host.AsCallerAsync(admin, s => profiles(s).AddLineAsync(draft, new AddLineRequest("Overflow entry A", "OverflowEntry", arrivals, 10, 25, 10, 27, overflow), Ct)));
        await PublishAsync(admin, draft);

        var bytes = Encoding.UTF8.GetBytes(Svg);
        Ok(await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcFloorPlans>().UploadAsync(arrivals, new FloorPlanUpload(new MemoryStream(bytes), bytes.Length, "arrivals.svg", 0.05, 1, 2), Ct)));
        if (twoPlans)
            Ok(await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcFloorPlans>().UploadAsync(departures, new FloorPlanUpload(new MemoryStream(bytes), bytes.Length, "departures.svg", 0.05, 0, 0), Ct)));

        var devices = (IServiceProvider s) => s.GetRequiredService<ISvcDevices>();
        var registered = Ok(await _host.AsCallerAsync(admin, s => devices(s).RegisterAsync(
            new RegisterDeviceRequest("S01", "StereoVision", "PC2SE <b>", "HttpsPush", "Xovis", "Ntp", new DevicePlacement(arrivals, 20, 16, 5, 0, "Snake A")), Ct)));
        Ok(await _host.AsCallerAsync(admin, s => devices(s).SetAccessAsync(registered.Device.Id, new SetDeviceAccessRequest(["10.20.0.0/24"], new string('c', 64)), Ct)));
        Ok(await _host.AsCallerAsync(admin, s => devices(s).RecordCalibrationAsync(registered.Device.Id, new RecordCalibrationRequest("ManualCountTally", 200, 97, 0.4), Ct)));

        var rules = (IServiceProvider s) => s.GetRequiredService<ISvcAlertRules>();
        Ok(await _host.AsCallerAsync(admin, s => rules(s).CreateAsync(new AlertRuleRequest(site, HostileName, ["Snake A"], "Nowcast", "GreaterThan", 20, null, null, 5, 5,
            "Warning", RoleCodes.BorderShiftSupervisor, 15, null, Contact, false), Ct)));
        Ok(await _host.AsCallerAsync(admin, s => rules(s).CreateAsync(new AlertRuleRequest(site, "Overflow in use", ["Overflow A"], "OverflowOccupied", "IsTrue", null, null, null,
            5, 5, "Info", null, null, null, null, false), Ct)));

        if (displayCode is not null)
            Ok(await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcDisplays>().CreateAsync(Board(site, displayCode), Ct)));
        return registered.Credential;
    }

    /// <summary>An escalation contact: never exported.</summary>
    private const string Contact = "Duty officer, extension 4411";

    private static DisplayRequest Board(string site, string code) =>
        new(site, code, "<img src=x onerror=alert(1)>", "Gate 3", "Landscape", ["en", "ar"], 5, 1, 300,
            [new DisplayEntryRequest("Snake A", new Dictionary<string, string> { ["en"] = "Citizens", ["ar"] = "\u0645\u0648\u0627\u0637\u0646\u0648\u0646" })],
            new Dictionary<string, string> { ["en"] = "Ask a member of staff", ["ar"] = "\u0627\u0633\u0623\u0644" });

    private async Task DeleteDisplaysAsync(Guid admin, string site)
    {
        var displays = Ok(await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcDisplays>().SearchAsync(site, Ct)));
        foreach (var display in displays)
            Ok(await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcDisplays>().DeleteAsync(display.Id, Ct)));
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

    /// <summary>
    /// Checks text that could hold a credential (a part, an audit summary) without printing it: a failure shows only the
    /// fixed fragments that are missing or should not be there, never the text.
    /// </summary>
    private static void Holds(string text, string because, string[] wanted, string[] unwanted = null)
    {
        wanted.Where(w => !text.Contains(w, StringComparison.Ordinal)).Should().BeEmpty(because);
        (unwanted ?? []).Where(u => text.Contains(u, StringComparison.Ordinal)).Should().BeEmpty(because);
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
                 + (SELECT count(*) FROM floor_plan WHERE site_code = @secret) + (SELECT count(*) FROM device WHERE site_code = @secret)
                 + (SELECT count(*) FROM alert_rule WHERE site_code = @secret) + (SELECT count(*) FROM display WHERE site_code = @secret)
            """, secret: site);

    [Fact]
    public async Task ExportThenImport_Should_RecreateTheSiteAndKeepThePortableHash_When_TheDraftIsPublishedAtTheTarget()
    {
        var admin = await AdministratorAsync("rt");
        var sourceCredential = await SourceAsync(admin, "XCFA", "QXA", displayCode: "CFRT1");
        await SiteAsync(admin, "XCFB");
        await AirportAsync(admin, "QXB");

        var exported = Ok(await _host.AsCallerAsync(admin, s => Configuration(s).ExportAsync("XCFA", Ct)));
        // Display codes are unique among the deployment's live displays: the source's goes first (as when moving a site).
        await DeleteDisplaysAsync(admin, "XCFA");
        var imported = Ok(await _host.AsCallerAsync(admin, s => Configuration(s).ImportAsync("XCFB", Request(exported.Bundle), "QXB", Ct)));

        exported.ZoneProfileIncluded.Should().BeTrue();
        exported.Counts.Should().Be(new SiteConfigurationCountsViewModel(1, 2, 2, 3, 2, 3, 4, 1, 1, 2, 1));
        exported.Bundle.Manifest.Keys.Should().BeEquivalentTo("topology", "deskCodeMappings", "zoneProfile", "floorPlans", "devices", "alertRules", "displays", "floorPlanFile.1");
        var everyPart = string.Concat(exported.Bundle.Parts.Values.Select(part => Encoding.UTF8.GetString(Convert.FromBase64String(part))));
        // Booleans only around credentials, so a failure prints no credential material.
        System.Text.RegularExpressions.Regex.IsMatch(everyPart, "ard[kp]_").Should().BeFalse("no device credential or pairing secret is exported");
        (everyPart.Contains(sourceCredential[5..13], StringComparison.Ordinal) || everyPart.Contains(sourceCredential[13..], StringComparison.Ordinal))
            .Should().BeFalse("no fragment of the source device's credential is exported");
        Holds(everyPart, "no contact, access setting or calibration travels", [], [Contact, "10.20.0.0", new string('c', 64), "ManualCountTally"]);
        var deviceRows = System.Text.Json.JsonSerializer.Deserialize<List<DevicePartRow>>(Convert.FromBase64String(exported.Bundle.Parts[SiteConfigurationBundles.Devices]),
            SiteConfigurationParts.Json)!;
        deviceRows.Select(d => (d.Code, d.AccessSettingsWithheld)).Should().Equal([("S01", true)], "that access settings were withheld travels, never what they were");
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

        // Devices: registered by the registration path in Commissioning, each with a new credential that works there; the
        // source's credential does not, and no access setting or calibration came along.
        imported.DeviceCredentials.Count.Should().Be(1, "one device, one new credential");
        var issued = imported.DeviceCredentials[0];
        issued.Code.Should().Be("S01");
        DeviceCredentials.IsWellFormed(issued.Credential).Should().BeTrue();
        var hash = await _host.ReadAsync<string>("SELECT credential_hash FROM device WHERE site_code = @secret", secret: "XCFB");
        DeviceCredentials.Matches(issued.Credential, hash).Should().BeTrue("the new credential works at the target");
        DeviceCredentials.Matches(sourceCredential, hash).Should().BeFalse("the source's credential does not");
        DeviceCredentials.Matches(sourceCredential, await _host.ReadAsync<string>("SELECT credential_hash FROM device WHERE site_code = @secret", secret: "XCFA"))
            .Should().BeTrue("the source keeps its own");
        (await _host.ReadAsync<string>("SELECT state FROM device WHERE site_code = @secret", secret: "XCFB")).Should().Be("Commissioning");
        (await _host.ReadAsync<string>("SELECT state FROM device WHERE site_code = @secret", secret: "XCFA")).Should().Be("Online", "the source was calibrated");
        (await _host.ReadAsync<long>("SELECT count(*) FROM device WHERE site_code = @secret AND (allowed_sources IS NOT NULL OR client_certificate_sha256 IS NOT NULL)", secret: "XCFB"))
            .Should().Be(0);
        (await _host.ReadAsync<long>("SELECT count(*) FROM device_calibration WHERE site_code = @secret", secret: "XCFB")).Should().Be(0);
        (await _host.ReadAsync<long>("""
            SELECT count(*) FROM device d JOIN level l ON l.id = d.level_id WHERE d.site_code = 'XCFB' AND l.site_code = 'XCFB' AND l.code = 'L0' AND d.queue_zone_name = 'Snake A'
            """)).Should().Be(1, "remapped to the target's level");
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE position(@secret IN coalesce(before_summary, '') || coalesce(after_summary, '')) > 0", null,
            issued.Credential[13..])).Should().Be(0, "no audit entry holds a credential");

        // Alert rules: their codes, the target's site, no escalation contact; the answer lists the rule that needs one.
        (await _host.ReadAsync<string>("SELECT string_agg(code, ',' ORDER BY code) FROM alert_rule WHERE site_code = @secret", secret: "XCFB")).Should().Be("R-001,R-002");
        (await _host.ReadAsync<long>("SELECT count(*) FROM alert_rule WHERE site_code = @secret AND escalation_contact IS NOT NULL", secret: "XCFB")).Should().Be(0);
        (await _host.ReadAsync<string>("SELECT name FROM alert_rule WHERE site_code = @secret AND code = 'R-001'", secret: "XCFB")).Should().Be(HostileName, "kept as data");
        (await _host.ReadAsync<string>("SELECT owner_role FROM alert_rule WHERE site_code = @secret AND code = 'R-001'", secret: "XCFB")).Should().Be(RoleCodes.BorderShiftSupervisor);
        imported.RulesNeedingEscalationContact.Should().Equal("R-001");
        imported.DevicesNeedingAccessSettings.Should().Equal(["S01"], "the source device had source networks and a certificate pin");

        // Displays: unpaired, at the target site, with their entries.
        (await _host.ReadAsync<long>("SELECT count(*) FROM display WHERE site_code = @secret AND code = 'CFRT1' AND credential_hash IS NULL AND credential_prefix IS NULL", secret: "XCFB"))
            .Should().Be(1);
        imported.Notes.Should().Contain([SiteConfigurationErrors.DevicesArriveCommissioning, SiteConfigurationErrors.AccessSettingsWithheld, SiteConfigurationErrors.DisplaysArriveUnpaired,
            SiteConfigurationErrors.RulesAndDisplaysWaitForPublication, SiteConfigurationErrors.EscalationContactsWithheld]);

        var published = await PublishAsync(admin, imported.DraftProfileId!.Value);
        var again = Ok(await _host.AsCallerAsync(admin, s => Configuration(s).ExportAsync("XCFB", Ct)));
        again.PortableGeometryHash.Should().Be(exported.PortableGeometryHash, "the round trip keeps the portable geometry hash");
        published.GeometryHash.Should().NotBe(exported.GeometryHash, "F22 v1 names levels and desks by their database ids");
        again.Counts.Should().Be(exported.Counts);

        var audit = await _host.ReadAsync<string>("SELECT after_summary FROM audit_entry WHERE action = 'SiteConfiguration.Imported' AND target_name = @secret", secret: "XCFB");
        Holds(audit, "the audit holds the bundle hash, the outcome and the counts, and no part content",
            [exported.BundleSha256, "\"outcome\":\"imported\"", "\"desks\":3", "\"devices\":1", "\"alertRules\":2", "\"displays\":1"],
            ["script", "onerror", "OR '1'", "S01"]);
        System.Text.RegularExpressions.Regex.IsMatch(audit, "ard[kp]_").Should().BeFalse("the audit holds no credential");
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
        (await RecordsAsync("XCFG")).Should().Be(1 + 2 + 2 + 3 + 2 + 1 + 2 + 1 + 2, "terminal, levels, checkpoints, desks, mappings, profile, plans, device, rules");
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

        SiteConfigurationImportViewModel imported;
        await using (var scope = _host.Provider.CreateAsyncScope())
        {
            var caller = (TestCurrentUser)scope.ServiceProvider.GetRequiredService<ICurrentUser>();
            caller.Id = admin;
            caller.UserName = "it-admin";
            imported = Ok(await Configuration(scope.ServiceProvider).ImportAsync("XCFQ", Request(bundle), "QXQ", Ct));
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().RollbackAsync(Ct);
        }

        imported.DeviceCredentials.Count.Should().Be(1, "the device was registered before the rollback");
        imported.Counts.AlertRules.Should().Be(2);
        (await RecordsAsync("XCFQ")).Should().Be(0, "no record of any part, the device included, survives the rollback");
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE action = 'Device.Registered' AND after_summary LIKE '%' || @secret || '%'", null,
            imported.DeviceCredentials[0].Credential[..13])).Should().Be(0, "the registration's audit entry went with it");
        (await StoredAsync(storage, storage.Saved.Should().ContainSingle().Subject)).Should().BeFalse("the rollback removes the stored file");
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE action = 'SiteConfiguration.Imported' AND target_name = @secret", secret: "XCFQ")).Should().Be(0);
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE action = 'SiteConfiguration.ImportFailed' AND target_name = @secret", secret: "XCFQ"))
            .Should().Be(1, "one failure, one entry");
        var failed = await _host.ReadAsync<string>("SELECT after_summary FROM audit_entry WHERE action = 'SiteConfiguration.ImportFailed' AND target_name = @secret", secret: "XCFQ");
        Holds(failed, "the failure is recorded with its fixed reason", ["\"outcome\":\"failed\"", "rolled back after its records were written"]);
        System.Text.RegularExpressions.Regex.IsMatch(failed, "ard[kp]_").Should().BeFalse("the failure entry holds no credential");
    }

    [Fact]
    public async Task Import_Should_ApplyTheRuleServicesRoleCheck_When_TheCallerIsNotASystemAdministrator()
    {
        // The endpoint needs SiteConfiguration.Transfer, which only system administrators hold, so this caller cannot reach
        // the import through the API today; the service applies the rule service's role check all the same (CWE-863, CWE-269).
        var admin = await AdministratorAsync("ro");
        await SourceAsync(admin, "XCFX", "QXX");
        var bundle = Ok(await _host.AsCallerAsync(admin, s => Configuration(s).ExportAsync("XCFX", Ct))).Bundle;
        await SiteAsync(admin, "XCFY");
        await AirportAsync(admin, "QXY");
        var supervisor = await _host.CreateUserAsync("it.cfg.supervisor", roles: [RoleCodes.BorderShiftSupervisor]);
        await _host.ReadAsync<int>("UPDATE \"user\" SET all_sites = true WHERE id = @id RETURNING 1", supervisor);
        var rules = System.Text.Json.JsonSerializer.Deserialize<List<AlertRulePartRow>>(Convert.FromBase64String(bundle.Parts[SiteConfigurationBundles.AlertRules]),
            SiteConfigurationParts.Json)!;
        var owned = rules.Single(r => r.Code == "R-001");
        owned.OwnerRole.Should().Be(RoleCodes.BorderShiftSupervisor);
        ImportSiteConfigurationRequest With(params AlertRulePartRow[] only) => Rehashed(bundle, SiteConfigurationBundles.AlertRules, SiteConfigurationParts.Write(only.ToList()));

        foreach (var (what, refused) in new[]
                 {
                     ("a rule without an owner (every role's)", With(rules.Single(r => r.OwnerRole is null))),
                     ("an owner role the caller does not hold", With(owned with { OwnerRole = RoleCodes.TerminalDutyManager })),
                     ("an escalation role the caller does not hold", With(owned with { EscalateToRole = RoleCodes.HandlerStationManager }))
                 })
        {
            (await _host.AsCallerAsync(supervisor, s => Configuration(s).ImportAsync("XCFY", refused, "QXY", Ct))).ErrorMessages
                .Should().Equal([SiteConfigurationErrors.InvalidAlertRules], what);
            (await RecordsAsync("XCFY")).Should().Be(0, what);
        }

        // Only roles the caller holds: past the role check, and the whole import goes through.
        var held = Ok(await _host.AsCallerAsync(supervisor, s => Configuration(s).ImportAsync("XCFY",
            With(owned with { EscalateToRole = RoleCodes.BorderShiftSupervisor }), "QXY", Ct)));
        held.Counts.AlertRules.Should().Be(1);
        (await _host.ReadAsync<string>("SELECT owner_role || '/' || escalate_to_role FROM alert_rule WHERE site_code = @secret", secret: "XCFY"))
            .Should().Be($"{RoleCodes.BorderShiftSupervisor}/{RoleCodes.BorderShiftSupervisor}");
    }

    /// <summary>
    /// ARV-181b2: a rule on open desks below recommendation travels like any other once it belongs to a border role and watches
    /// queue zones only; an import refuses one with an airport-side or no owner, or on an overflow band, and the export is
    /// refused to a caller without ArrivalWaveLanes.View (only administrators reach the endpoint; the service checks it besides).
    /// </summary>
    [Fact]
    public async Task Transfer_Should_CarryOpenDesksBelowRecommendation_When_TheRuleBelongsToABorderRole()
    {
        var admin = await AdministratorAsync("db");
        await SourceAsync(admin, "XCDA", "QDA");
        Ok(await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAlertRules>().CreateAsync(new AlertRuleRequest("XCDA", "Desks short", ["Snake A"], "DesksBelowPlan",
            "GreaterOrEqual", 2, null, null, 3, 2, "Warning", RoleCodes.BorderShiftSupervisor, null, null, null, false), Ct)));
        var duty = await _host.CreateUserAsync("it.cfg.desks.duty", roles: [RoleCodes.TerminalDutyManager]);
        await _host.ReadAsync<int>("UPDATE \"user\" SET all_sites = true WHERE id = @id RETURNING 1", duty);

        (await _host.AsCallerAsync(duty, s => Configuration(s).ExportAsync("XCDA", Ct))).ErrorMessages
            .Should().Equal([AlertRuleErrors.BorderOnly], "the border rule never travels to a caller without the lane split");
        var exported = Ok(await _host.AsCallerAsync(admin, s => Configuration(s).ExportAsync("XCDA", Ct)));
        var rules = System.Text.Json.JsonSerializer.Deserialize<List<AlertRulePartRow>>(Convert.FromBase64String(exported.Bundle.Parts[SiteConfigurationBundles.AlertRules]),
            SiteConfigurationParts.Json)!;
        var desks = rules.Single(r => r.Metric == "DesksBelowPlan");
        desks.OwnerRole.Should().Be(RoleCodes.BorderShiftSupervisor);
        await SiteAsync(admin, "XCDB");
        await AirportAsync(admin, "QDB");
        ImportSiteConfigurationRequest With(AlertRulePartRow changed) =>
            Rehashed(exported.Bundle, SiteConfigurationBundles.AlertRules, SiteConfigurationParts.Write(rules.Select(r => r.Code == desks.Code ? changed : r).ToList()));

        foreach (var (what, refused, error) in new[]
                 {
                     ("an airport-side owner", With(desks with { OwnerRole = RoleCodes.TerminalDutyManager }), SiteConfigurationErrors.InvalidAlertRules),
                     ("no owner (every role's)", With(desks with { OwnerRole = null }), SiteConfigurationErrors.InvalidAlertRules),
                     ("an airport-side escalation role", With(desks with { EscalateAfterMinutes = 10, EscalateToRole = RoleCodes.HandlerStationManager }),
                         SiteConfigurationErrors.InvalidAlertRules),
                     ("a rule that fires below", With(desks with { Comparator = "LessThan" }), SiteConfigurationErrors.InvalidAlertRules),
                     ("an overflow band", With(desks with { Zones = ["Overflow A"] }), SiteConfigurationErrors.AlertRuleZonesNotInProfile)
                 })
        {
            (await _host.AsCallerAsync(admin, s => Configuration(s).ImportAsync("XCDB", refused, "QDB", Ct))).ErrorMessages.Should().Equal([error], what);
            (await RecordsAsync("XCDB")).Should().Be(0, what);
        }

        (await _host.AsCallerAsync(duty, s => Configuration(s).ImportAsync("XCDB", Request(exported.Bundle), "QDB", Ct))).ErrorMessages
            .Should().Equal(SiteConfigurationErrors.InvalidAlertRules);
        (await RecordsAsync("XCDB")).Should().Be(0);

        var imported = Ok(await _host.AsCallerAsync(admin, s => Configuration(s).ImportAsync("XCDB", Request(exported.Bundle), "QDB", Ct)));
        imported.Counts.AlertRules.Should().Be(3);
        (await _host.ReadAsync<string>("SELECT owner_role || '/' || comparator FROM alert_rule WHERE site_code = @secret AND metric = 'DesksBelowPlan'", secret: "XCDB"))
            .Should().Be($"{RoleCodes.BorderShiftSupervisor}/GreaterOrEqual");
    }

    [Fact]
    public async Task Import_Should_RefuseADisplayCodeALiveDisplayHoldsAndWriteNothing_When_TheSourceStillHasIt()
    {
        var admin = await AdministratorAsync("dc");
        await SourceAsync(admin, "XCFR", "QXR", displayCode: "CFDC1");
        var bundle = Ok(await _host.AsCallerAsync(admin, s => Configuration(s).ExportAsync("XCFR", Ct))).Bundle;
        await SiteAsync(admin, "XCFS");
        await AirportAsync(admin, "QXS");

        var refused = await _host.AsCallerAsync(admin, s => Configuration(s).ImportAsync("XCFS", Request(bundle), "QXS", Ct));

        refused.ErrorMessages.Should().Equal(SiteConfigurationErrors.DisplayCodeTaken);
        SiteConfigurationErrors.Conflicts.Should().Contain(SiteConfigurationErrors.DisplayCodeTaken, "the controller answers 409");
        (await RecordsAsync("XCFS")).Should().Be(0);
        (await _host.ReadAsync<string>("SELECT after_summary FROM audit_entry WHERE action = 'SiteConfiguration.ImportRefused' AND target_name = @secret", secret: "XCFS"))
            .Should().Contain(SiteConfigurationErrors.DisplayCodeTaken).And.NotContain("CFDC1", "the fixed text names no code");

        await DeleteDisplaysAsync(admin, "XCFR");
        Ok(await _host.AsCallerAsync(admin, s => Configuration(s).ImportAsync("XCFS", Request(bundle), "QXS", Ct))).Counts.Displays.Should().Be(1);
    }

    [Fact]
    public async Task Import_Should_RefuseARuleOrDisplay_When_ItNamesAZoneTheImportedDraftLacks()
    {
        var admin = await AdministratorAsync("dz");
        await SourceAsync(admin, "XCFT", "QXT", displayCode: "CFDZ1");
        var exported = Ok(await _host.AsCallerAsync(admin, s => Configuration(s).ExportAsync("XCFT", Ct)));
        await DeleteDisplaysAsync(admin, "XCFT");
        await SiteAsync(admin, "XCFU");
        await AirportAsync(admin, "QXU");
        var rules = System.Text.Json.JsonSerializer.Deserialize<List<AlertRulePartRow>>(Convert.FromBase64String(exported.Bundle.Parts[SiteConfigurationBundles.AlertRules]),
            SiteConfigurationParts.Json)!;
        var displays = System.Text.Json.JsonSerializer.Deserialize<List<DisplayPartRow>>(Convert.FromBase64String(exported.Bundle.Parts[SiteConfigurationBundles.Displays]),
            SiteConfigurationParts.Json)!;

        var ghostRule = Rehashed(exported.Bundle, SiteConfigurationBundles.AlertRules, SiteConfigurationParts.Write(rules.Select(r => r with { Zones = ["Snake B"] }).ToList()));
        var healthOnBand = Rehashed(exported.Bundle, SiteConfigurationBundles.AlertRules, SiteConfigurationParts.Write(rules.Select(r => r with
        {
            Zones = ["Overflow A"], Metric = "TrackCompletion", Comparator = "LessThan", Threshold = 80, ClearThreshold = null, MinQueueLength = null, LeadMinutes = null
        }).ToList()));
        var displayOnBand = Rehashed(exported.Bundle, SiteConfigurationBundles.Displays,
            SiteConfigurationParts.Write(displays.Select(d => d with { Entries = [d.Entries[0] with { Zone = "Overflow A" }] }).ToList()));

        (await _host.AsCallerAsync(admin, s => Configuration(s).ImportAsync("XCFU", ghostRule, "QXU", Ct))).ErrorMessages
            .Should().Equal(SiteConfigurationErrors.AlertRuleZonesNotInProfile);
        (await _host.AsCallerAsync(admin, s => Configuration(s).ImportAsync("XCFU", healthOnBand, "QXU", Ct))).ErrorMessages
            .Should().Equal([SiteConfigurationErrors.AlertRuleZonesNotInProfile], "a health check watches queue zones only");
        (await _host.AsCallerAsync(admin, s => Configuration(s).ImportAsync("XCFU", displayOnBand, "QXU", Ct))).ErrorMessages
            .Should().Equal(SiteConfigurationErrors.DisplayZonesNotInProfile);
        (await RecordsAsync("XCFU")).Should().Be(0);
    }

    [Fact]
    public async Task Import_Should_LeaveNothingBehind_When_TheRegistrationPathRefusesALaterDeviceAfterRecordsFilesAndADeviceWereWritten()
    {
        var admin = await AdministratorAsync("dv");
        await SourceAsync(admin, "XCFV", "QXV");
        var bundle = Ok(await _host.AsCallerAsync(admin, s => Configuration(s).ExportAsync("XCFV", Ct))).Bundle;
        await SiteAsync(admin, "XCFW");
        await AirportAsync(admin, "QXW");
        var devices = System.Text.Json.JsonSerializer.Deserialize<List<DevicePartRow>>(Convert.FromBase64String(bundle.Parts[SiteConfigurationBundles.Devices]),
            SiteConfigurationParts.Json)!;
        // The second device hangs inside the level but far from its queue zone: the registration path's reach check refuses it.
        var farAway = Rehashed(bundle, SiteConfigurationBundles.Devices, SiteConfigurationParts.Write(new List<DevicePartRow> { devices[0], devices[0] with { Code = "S02", X = 90, Y = 45 } }));
        var storage = (SwitchableFileStorage)_host.Provider.GetRequiredService<IFileStorage>();
        storage.Arm(failAt: 0);

        (await _host.AsCallerAsync(admin, s => Configuration(s).ImportAsync("XCFW", farAway, "QXW", Ct))).ErrorMessages.Should().Equal(SiteConfigurationErrors.InvalidDevices);

        (await RecordsAsync("XCFW")).Should().Be(0, "the first device, the records and the plan are all rolled back");
        (await StoredAsync(storage, storage.Saved.Should().ContainSingle("the plan's file was stored before the devices").Subject)).Should().BeFalse();
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE action = 'SiteConfiguration.ImportRefused' AND target_name = @secret", secret: "XCFW")).Should().Be(1);
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
