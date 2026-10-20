using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Ariva.Core;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Administration;
using Ariva.Core.Services.Displays;
using Ariva.Core.Services.Flights;
using Ariva.Core.Services.Imports;
using Ariva.Core.Services.Storage;
using Ariva.Core.Services.Topology;
using Ariva.Di.Extensions;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace Ariva.IntegrationTests.Storage;

/// <summary>
/// ARV-092b against PostgreSQL with the real scripts and a fake clamd that finds the EICAR test file: every upload of
/// Ariva.Api.Main (a floor plan, an SSIM file at preview and import, a device and a desk code mapping CSV at preview and
/// commit, every part of a site configuration bundle, an advertisement) is scanned before anything parses, stores or
/// imports it. A detection is refused with the fixed text, nothing is stored or imported, and an audit entry names the
/// caller, the kind and the signature with the file's size and SHA-256, never the file; a clean file is still taken. With
/// the scanner down outside vm-local (k8s-dev) every upload is refused as unavailable and nothing is kept.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class UploadScanTests : IAsyncDisposable
{
    #region Fields

    private const string Svg = """<svg xmlns="http://www.w3.org/2000/svg" width="100" height="50"><rect width="10" height="10"/></svg>""";

    private const string DeviceHeader = "code,family,model,transport,dialect,clockSource,terminal,level,x,y,mountingHeightMetres,orientationDegrees,queueZone";

    private const string MappingHeader = "externalSystem,externalCode,checkpoint,desk";

    private readonly FakeClamd _clamd = new() { Mode = FakeClamd.Behaviour.Detect };
    private readonly string _files = Path.Combine(Path.GetTempPath(), "ariva-it-upload-scan", Guid.NewGuid().ToString("N"));
    private readonly string _downFiles = Path.Combine(Path.GetTempPath(), "ariva-it-upload-scan", Guid.NewGuid().ToString("N"));
    private readonly string _slowFiles = Path.Combine(Path.GetTempPath(), "ariva-it-upload-scan", Guid.NewGuid().ToString("N"));
    private readonly AccountsHost _host;
    private readonly AccountsHost _down;
    private readonly AccountsHost _slow;

    #endregion

    #region Setup

    public UploadScanTests(PostgresFixture fixture)
    {
        // vm-local with a scanner: a detection is refused there too (only a missing verdict is let through in vm-local).
        _host = new AccountsHost(fixture, Settings("vm-local", _clamd.Port, _files), TestDatabase.UploadScan, Schedules);
        // A cluster environment whose scanner does not answer: fail closed.
        _down = new AccountsHost(fixture, Settings("k8s-dev", ClosedPort(), _downFiles), TestDatabase.UploadScan, Schedules);
        // A cluster environment with the shortest scan budget, for the bundle's one deadline (its services built when first used).
        var slow = Settings("k8s-dev", _clamd.Port, _slowFiles);
        slow["MalwareScan:TimeoutSeconds"] = "5";
        _slow = new AccountsHost(fixture, slow, TestDatabase.UploadScan, Schedules);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        await _down.DisposeAsync();
        await _slow.DisposeAsync();
        await _clamd.DisposeAsync();
        foreach (var folder in new[] { _files, _downFiles, _slowFiles }.Where(Directory.Exists))
            Directory.Delete(folder, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Schedules(IServiceCollection services) => services.AddArivaFlightSchedules(new ConfigurationBuilder().Build());

    private static Dictionary<string, string> Settings(string environment, int port, string files) => new()
    {
        ["Application:Environment"] = environment,
        // AddArivaTokenIssuing allows sign-in without TOTP in vm-local only; nothing here signs in.
        ["Auth:TotpRequired"] = environment == "vm-local" ? "false" : "true",
        ["MalwareScan:Host"] = "127.0.0.1",
        ["MalwareScan:Port"] = port.ToString(CultureInfo.InvariantCulture),
        ["Storage:LocalRoot"] = files
    };

    /// <summary>A port nothing listens on: bound, then released.</summary>
    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static T Ok<T>(Fluentx.Result<T> result)
    {
        result.HasErrors.Should().BeFalse(string.Join(", ", result.ErrorMessages ?? []));
        return result.Data;
    }

    private static int FilesIn(string folder) => Directory.Exists(folder) ? Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Length : 0;

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>An administrator of every site, in both hosts' database (the hosts share it).</summary>
    private async Task<Guid> AdministratorAsync(string name)
    {
        var admin = await _host.CreateUserAsync($"it.scan.{name}", roles: [RoleCodes.SystemAdministrator]);
        await _host.ReadAsync<int>("UPDATE \"user\" SET all_sites = true WHERE id = @id RETURNING 1", admin);
        // The down host builds its services on its first account.
        await _down.CreateUserAsync($"it.scan.{name}.probe");
        return admin;
    }

    private sealed record SiteSeed(string Code, string Airport, Guid Level);

    /// <summary>A site with an airport (its IATA code the site code's last three letters), terminal T1, level L0 and checkpoint IMM with desk D01.</summary>
    private async Task<SiteSeed> SiteAsync(Guid admin, string site)
    {
        Ok(await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest(site, site), Ct)));
        var topology = (IServiceProvider s) => s.GetRequiredService<ISvcTopology>();
        var airport = Ok(await _host.AsCallerAsync(admin, s => topology(s).CreateAirportAsync(new CreateAirportRequest(site[1..], null, $"Airport {site}", "Asia/Dubai"), Ct))).Id;
        var terminal = Ok(await _host.AsCallerAsync(admin, s => topology(s).CreateTerminalAsync(new CreateTerminalRequest(airport, "T1", "T1", site), Ct))).Id;
        var level = Ok(await _host.AsCallerAsync(admin, s => topology(s).CreateLevelAsync(new CreateLevelRequest(terminal, "L0", "Arrivals", 0, 100, 50), Ct))).Id;
        var immigration = Ok(await _host.AsCallerAsync(admin, s => topology(s).CreateCheckpointAsync(new CreateCheckpointRequest(level, "IMM", "Immigration", "Immigration"), Ct))).Id;
        Ok(await _host.AsCallerAsync(admin, s => topology(s).CreateDeskAsync(new CreateDeskRequest(immigration, "D01", null, "Desk", ["CIT"]), Ct)));
        return new SiteSeed(site, site[1..], level);
    }

    /// <summary>How many malware refusals of this kind are audited for this caller, and the latest one's summary.</summary>
    private async Task<(long Count, string Latest)> RefusalsAsync(Guid admin, string kind)
    {
        var count = await _host.ReadAsync<long>(
            "SELECT count(*) FROM audit_entry WHERE action = 'Upload.MalwareRefused' AND actor_id = @id AND target_type = @secret AND target_name = 'Eicar-Test-Signature'",
            admin, kind);
        var latest = await _host.ReadAsync<string>(
            "SELECT after_summary FROM audit_entry WHERE action = 'Upload.MalwareRefused' AND actor_id = @id AND target_type = @secret ORDER BY occurred_on DESC, id DESC LIMIT 1",
            admin, kind);
        return (count, latest);
    }

    /// <summary>One refusal's summary holds the kind, the signature, the size and the SHA-256 of the EICAR file, never its text.</summary>
    private static void HoldsTheFilesFingerprintOnly(string summary, string kind, byte[] file)
    {
        new[] { $"\"kind\":\"{kind}\"", "\"signature\":\"Eicar-Test-Signature\"", $"\"bytes\":{file.Length}", Sha256(file) }
            .Where(w => !summary.Contains(w, StringComparison.Ordinal)).Should().BeEmpty("the kind, the signature, the size and the hash");
        summary.Should().NotContain("EICAR-STANDARD", "never the file").And.NotContain("upload.", "never its name");
    }

    #endregion

    #region Floor plans

    [Fact]
    public async Task FloorPlanUpload_Should_RefuseStoreNothingAndAudit_When_TheFileIsTheEicarTestFile()
    {
        var admin = await AdministratorAsync("fp");
        var site = await SiteAsync(admin, "XUFA");
        var plans = (IServiceProvider s) => s.GetRequiredService<ISvcFloorPlans>();
        var eicar = Eicar.Bytes();
        _clamd.Clear();

        var refused = await _host.AsCallerAsync(admin, s => plans(s).UploadAsync(site.Level, new FloorPlanUpload(new MemoryStream(eicar), eicar.Length, "upload.svg", 0.05, 0, 0), Ct));

        refused.ErrorMessages.Should().Equal(UploadScanErrors.Malware);
        (await _host.ReadAsync<long>("SELECT count(*) FROM floor_plan WHERE level_id = @id", site.Level)).Should().Be(0, "nothing is stored");
        FilesIn(_files).Should().Be(0);
        var audited = await RefusalsAsync(admin, "FloorPlan");
        audited.Count.Should().Be(1);
        HoldsTheFilesFingerprintOnly(audited.Latest, "FloorPlan", eicar);
        _clamd.Sessions.Should().ContainSingle().Which.Content.Should().Equal(eicar, "the bytes read are the bytes scanned");

        var svg = Encoding.UTF8.GetBytes(Svg);
        var stored = Ok(await _host.AsCallerAsync(admin, s => plans(s).UploadAsync(site.Level, new FloorPlanUpload(new MemoryStream(svg), svg.Length, "plan.svg", 0.05, 0, 0), Ct)));
        stored.SizeBytes.Should().BeGreaterThan(0, "a clean plan is still taken");
        FilesIn(_files).Should().Be(1);
    }

    [Fact]
    public async Task FloorPlanUpload_Should_RefuseAsUnavailableAndStoreNothing_When_TheScannerIsDownOutsideVmLocal()
    {
        var admin = await AdministratorAsync("fpd");
        var site = await SiteAsync(admin, "XUFB");
        var svg = Encoding.UTF8.GetBytes(Svg);

        var refused = await _down.AsCallerAsync(admin, s => s.GetRequiredService<ISvcFloorPlans>()
            .UploadAsync(site.Level, new FloorPlanUpload(new MemoryStream(svg), svg.Length, "plan.svg", 0.05, 0, 0), Ct));

        refused.ErrorMessages.Should().Equal(UploadScanErrors.ScannerUnavailable);
        (await _host.ReadAsync<long>("SELECT count(*) FROM floor_plan WHERE level_id = @id", site.Level)).Should().Be(0);
        FilesIn(_downFiles).Should().Be(0);
        (await RefusalsAsync(admin, "FloorPlan")).Count.Should().Be(0, "no threat was found");
    }

    #endregion

    #region SSIM

    /// <summary>An SSIM file (time mode U) with one daily leg from DOH to the airport.</summary>
    private string Ssim(string airport)
    {
        var now = _host.Clock.GetUtcNow().UtcDateTime;
        string Date(DateTime d) => d.ToString("ddMMMyy", CultureInfo.InvariantCulture).ToUpperInvariant();
        var line = new StringBuilder(new string(' ', 200));
        void Put(int start, string value) => line.Remove(start - 1, value.Length).Insert(start - 1, value);
        Put(1, "3"); Put(3, "QR "); Put(6, "0901"); Put(10, "0101J");
        Put(15, Date(now.AddDays(-5))); Put(22, Date(now.AddDays(30))); Put(29, "1234567");
        Put(37, "DOH"); Put(40, "0800"); Put(44, "0800"); Put(48, "+0000");
        Put(55, airport); Put(58, "1200"); Put(62, "1200"); Put(66, "+0000"); Put(71, "T1"); Put(73, "359");
        return string.Join("\n", "1AIRLINE STANDARD SCHEDULE DATA SET".PadRight(200), "2UQR ".PadRight(200), line.ToString()) + "\n";
    }

    [Fact]
    public async Task FlightSchedules_Should_RefuseAndAuditAtPreviewAndImport_When_TheFileIsTheEicarTestFile()
    {
        var admin = await AdministratorAsync("ssim");
        var site = await SiteAsync(admin, "XUSA");
        var schedules = (IServiceProvider s) => s.GetRequiredService<ISvcFlightSchedules>();
        var eicar = Eicar.Bytes();

        var preview = await _host.AsCallerAsync(admin, s => schedules(s).PreviewAsync(site.Code, new MemoryStream(eicar), 7, Ct));
        var import = await _host.AsCallerAsync(admin, s => schedules(s).ImportAsync(site.Code, new MemoryStream(eicar), 7, "not-a-token", Ct));

        preview.ErrorMessages.Should().Equal(UploadScanErrors.Malware);
        import.ErrorMessages.Should().Equal([UploadScanErrors.Malware], "scanned again before the token is even read");
        var audited = await RefusalsAsync(admin, "FlightSchedule");
        audited.Count.Should().Be(2);
        HoldsTheFilesFingerprintOnly(audited.Latest, "FlightSchedule", eicar);
        (await _host.ReadAsync<long>("SELECT count(*) FROM flight_leg WHERE site_code = @secret", secret: site.Code)).Should().Be(0);

        var text = Encoding.ASCII.GetBytes(Ssim(site.Airport));
        var clean = Ok(await _host.AsCallerAsync(admin, s => schedules(s).PreviewAsync(site.Code, new MemoryStream(text), 7, Ct)));
        clean.Legs.Should().BeGreaterThan(0, "a clean file is still read, from its first byte after the scan");
        var imported = Ok(await _host.AsCallerAsync(admin, s => schedules(s).ImportAsync(site.Code, new MemoryStream(text), 7, clean.PreviewToken, Ct)));
        imported.Applied.Should().Be(clean.Legs);
    }

    [Fact]
    public async Task FlightSchedules_Should_RefuseAsUnavailable_When_TheScannerIsDownOutsideVmLocal()
    {
        var admin = await AdministratorAsync("ssimd");
        var site = await SiteAsync(admin, "XUSB");
        var text = Encoding.ASCII.GetBytes(Ssim(site.Airport));

        var preview = await _down.AsCallerAsync(admin, s => s.GetRequiredService<ISvcFlightSchedules>().PreviewAsync(site.Code, new MemoryStream(text), 7, Ct));
        var import = await _down.AsCallerAsync(admin, s => s.GetRequiredService<ISvcFlightSchedules>().ImportAsync(site.Code, new MemoryStream(text), 7, "x", Ct));

        preview.ErrorMessages.Should().Equal(UploadScanErrors.ScannerUnavailable);
        import.ErrorMessages.Should().Equal(UploadScanErrors.ScannerUnavailable);
        (await _host.ReadAsync<long>("SELECT count(*) FROM flight_leg WHERE site_code = @secret", secret: site.Code)).Should().Be(0);
    }

    #endregion

    #region CSV imports

    private static Func<Stream> File(byte[] bytes) => () => new MemoryStream(bytes);

    [Fact]
    public async Task DeviceImport_Should_RefuseAndAuditAtPreviewAndCommit_When_TheFileIsTheEicarTestFile()
    {
        var admin = await AdministratorAsync("dev");
        var site = await SiteAsync(admin, "XUDA");
        var imports = (IServiceProvider s) => s.GetRequiredService<ISvcDeviceImports>();
        var eicar = Eicar.Bytes();

        var preview = await _host.AsCallerAsync(admin, s => imports(s).PreviewAsync(site.Code, File(eicar), Ct));
        var commit = await _host.AsCallerAsync(admin, s => imports(s).CommitAsync(site.Code, File(eicar), "not-a-token", Ct));

        preview.ErrorMessages.Should().Equal(UploadScanErrors.Malware);
        commit.ErrorMessages.Should().Equal(UploadScanErrors.Malware);
        var audited = await RefusalsAsync(admin, "DeviceImport");
        audited.Count.Should().Be(2);
        HoldsTheFilesFingerprintOnly(audited.Latest, "DeviceImport", eicar);
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE action = 'DeviceImport.Previewed' AND target_name = @secret AND after_summary LIKE '%malware%'",
            secret: site.Code)).Should().Be(1, "the import records its refusal as it records every other");
        (await _host.ReadAsync<long>("SELECT count(*) FROM device WHERE site_code = @secret", secret: site.Code)).Should().Be(0);

        // A clean file is still read and checked row by row: the site has no published profile, so its queue zone is refused.
        var csv = Encoding.UTF8.GetBytes(DeviceHeader + "\r\nS-01,StereoVision,PC2SE,HttpsPush,Xovis,Ntp,T1,L0,20,16,5,0,Snake A\r\n");
        var clean = Ok(await _host.AsCallerAsync(admin, s => imports(s).PreviewAsync(site.Code, File(csv), Ct)));
        clean.Rows.Should().Be(1);
        clean.Errors.Should().ContainSingle().Which.Reason.Should().NotBe(UploadScanErrors.Malware);
    }

    [Fact]
    public async Task DeskCodeMappingImport_Should_RefuseAtPreviewAndCommitAndStillImportACleanFile_When_TheFileIsTheEicarTestFile()
    {
        var admin = await AdministratorAsync("map");
        var site = await SiteAsync(admin, "XUMA");
        var imports = (IServiceProvider s) => s.GetRequiredService<ISvcDeskCodeMappingImports>();
        var eicar = Eicar.Bytes();

        var preview = await _host.AsCallerAsync(admin, s => imports(s).PreviewAsync(site.Code, File(eicar), Ct));
        var commit = await _host.AsCallerAsync(admin, s => imports(s).CommitAsync(site.Code, File(eicar), "not-a-token", Ct));

        preview.ErrorMessages.Should().Equal(UploadScanErrors.Malware);
        commit.ErrorMessages.Should().Equal(UploadScanErrors.Malware);
        (await RefusalsAsync(admin, "DeskCodeMappingImport")).Count.Should().Be(2);
        (await _host.ReadAsync<long>("SELECT count(*) FROM desk_code_mapping WHERE site_code = @secret", secret: site.Code)).Should().Be(0);

        var csv = Encoding.UTF8.GetBytes(MappingHeader + "\nAman,IMM-01,IMM,D01\n");
        var clean = Ok(await _host.AsCallerAsync(admin, s => imports(s).PreviewAsync(site.Code, File(csv), Ct)));
        Ok(await _host.AsCallerAsync(admin, s => imports(s).CommitAsync(site.Code, File(csv), clean.PreviewToken, Ct))).Mappings.Should().Be(1);
        (await _host.ReadAsync<long>("SELECT count(*) FROM desk_code_mapping WHERE site_code = @secret", secret: site.Code)).Should().Be(1);
    }

    [Fact]
    public async Task CsvImports_Should_RefuseAsUnavailable_When_TheScannerIsDownOutsideVmLocal()
    {
        var admin = await AdministratorAsync("csvd");
        var site = await SiteAsync(admin, "XUCB");
        var devices = Encoding.UTF8.GetBytes(DeviceHeader + "\r\nS-01,StereoVision,PC2SE,HttpsPush,Xovis,Ntp,T1,L0,20,16,5,0,Snake A\r\n");
        var mappings = Encoding.UTF8.GetBytes(MappingHeader + "\nAman,IMM-01,IMM,D01\n");

        var results = new[]
        {
            (await _down.AsCallerAsync(admin, s => s.GetRequiredService<ISvcDeviceImports>().PreviewAsync(site.Code, File(devices), Ct))).ErrorMessages,
            (await _down.AsCallerAsync(admin, s => s.GetRequiredService<ISvcDeviceImports>().CommitAsync(site.Code, File(devices), "x", Ct))).ErrorMessages,
            (await _down.AsCallerAsync(admin, s => s.GetRequiredService<ISvcDeskCodeMappingImports>().PreviewAsync(site.Code, File(mappings), Ct))).ErrorMessages,
            (await _down.AsCallerAsync(admin, s => s.GetRequiredService<ISvcDeskCodeMappingImports>().CommitAsync(site.Code, File(mappings), "x", Ct))).ErrorMessages
        };

        results.Should().AllSatisfy(errors => errors.Should().Equal(UploadScanErrors.ScannerUnavailable));
        (await _host.ReadAsync<long>("SELECT count(*) FROM desk_code_mapping WHERE site_code = @secret", secret: site.Code)).Should().Be(0);
    }

    #endregion

    #region Site configuration

    private static ImportSiteConfigurationRequest Request(SiteConfigurationBundleViewModel bundle) =>
        new(bundle.FormatVersion, bundle.SourceSite, bundle.ExportedAtUtc, new Dictionary<string, string>(bundle.Manifest), new Dictionary<string, string>(bundle.Parts));

    private Task<long> RecordsAsync(string site) =>
        _host.ReadAsync<long>("""
            SELECT (SELECT count(*) FROM terminal WHERE site_code = @secret) + (SELECT count(*) FROM level WHERE site_code = @secret)
                 + (SELECT count(*) FROM checkpoint WHERE site_code = @secret) + (SELECT count(*) FROM floor_plan WHERE site_code = @secret)
            """, secret: site);

    [Fact]
    public async Task SiteConfigurationImport_Should_RefuseKeepNothingAndAudit_When_APartIsTheEicarTestFile()
    {
        var admin = await AdministratorAsync("cfg");
        var source = await SiteAsync(admin, "XUCA");
        var svg = Encoding.UTF8.GetBytes(Svg);
        Ok(await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcFloorPlans>().UploadAsync(source.Level, new FloorPlanUpload(new MemoryStream(svg), svg.Length, "plan.svg", 0.05, 0, 0), Ct)));
        Ok(await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest("XUCT", "XUCT"), Ct)));
        Ok(await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcTopology>().CreateAirportAsync(new CreateAirportRequest("UCT", null, "Airport UCT", "Asia/Dubai"), Ct)));
        var exported = Ok(await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSiteConfiguration>().ExportAsync(source.Code, Ct)));
        var eicar = Eicar.Bytes();
        // The floor plan's file replaced and the manifest recomputed to match: anyone can, so only the scan stops it.
        var request = Request(exported.Bundle);
        var part = SiteConfigurationBundles.FloorPlanFile(1);
        request.Parts[part] = Convert.ToBase64String(eicar);
        request.Manifest[part] = SiteConfigurationBundles.Sha256(eicar);
        var filesBefore = FilesIn(_files);

        var refused = await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSiteConfiguration>().ImportAsync("XUCT", request, "UCT", Ct));

        refused.ErrorMessages.Should().Equal(UploadScanErrors.Malware);
        (await RecordsAsync("XUCT")).Should().Be(0, "nothing of the bundle is kept");
        FilesIn(_files).Should().Be(filesBefore, "no file is stored");
        var audited = await RefusalsAsync(admin, "SiteConfiguration");
        audited.Count.Should().Be(1);
        HoldsTheFilesFingerprintOnly(audited.Latest, "SiteConfiguration", eicar);
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE action = 'SiteConfiguration.ImportRefused' AND target_name = 'XUCT' AND after_summary LIKE '%malware%'"))
            .Should().Be(1, "the import records its refusal through its own path");

        var imported = Ok(await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSiteConfiguration>().ImportAsync("XUCT", Request(exported.Bundle), "UCT", Ct)));
        imported.Counts.Should().NotBeNull("the clean bundle, every part scanned, is still imported");
        (await _host.ReadAsync<long>("SELECT count(*) FROM floor_plan WHERE site_code = 'XUCT'")).Should().Be(1);
    }

    [Fact]
    public async Task SiteConfigurationImport_Should_RefuseAsUnavailableAndKeepNothing_When_TheScannerIsDownOutsideVmLocal()
    {
        var admin = await AdministratorAsync("cfgd");
        var source = await SiteAsync(admin, "XUCD");
        Ok(await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest("XUCE", "XUCE"), Ct)));
        Ok(await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcTopology>().CreateAirportAsync(new CreateAirportRequest("UCE", null, "Airport UCE", "Asia/Dubai"), Ct)));
        var exported = Ok(await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSiteConfiguration>().ExportAsync(source.Code, Ct)));

        var refused = await _down.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSiteConfiguration>().ImportAsync("XUCE", Request(exported.Bundle), "UCE", Ct));

        refused.ErrorMessages.Should().Equal(UploadScanErrors.ScannerUnavailable);
        (await RecordsAsync("XUCE")).Should().Be(0);
    }

    [Fact]
    public async Task SiteConfigurationImport_Should_RefuseAsUnavailableAtTheBundlesDeadline_When_ThePartsTogetherTakeLongerThanOneScan()
    {
        // Eight parts (seven configuration parts and a plan) at 2 s each: every part within its own 5 s, 16 s together. One
        // deadline of MalwareScan:TimeoutSeconds (5 s) for the bundle ends it after the third part, so a bundle of slow parts
        // cannot hold the one upload slot for a budget per part (CWE-400).
        var admin = await AdministratorAsync("cfgs");
        await _slow.CreateUserAsync("it.scan.cfgs.slow");
        var source = await SiteAsync(admin, "XUCF");
        var svg = Encoding.UTF8.GetBytes(Svg);
        Ok(await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcFloorPlans>().UploadAsync(source.Level, new FloorPlanUpload(new MemoryStream(svg), svg.Length, "plan.svg", 0.05, 0, 0), Ct)));
        Ok(await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest("XUCG", "XUCG"), Ct)));
        Ok(await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcTopology>().CreateAirportAsync(new CreateAirportRequest("UCG", null, "Airport UCG", "Asia/Dubai"), Ct)));
        var exported = Ok(await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSiteConfiguration>().ExportAsync(source.Code, Ct)));
        exported.Bundle.Parts.Should().HaveCount(8);
        _clamd.Clear();
        _clamd.Mode = FakeClamd.Behaviour.Delay;
        _clamd.Delay = TimeSpan.FromSeconds(2);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var refused = await _slow.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSiteConfiguration>().ImportAsync("XUCG", Request(exported.Bundle), "UCG", Ct));

        refused.ErrorMessages.Should().Equal(UploadScanErrors.ScannerUnavailable);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "one 5 s deadline for the bundle, not 5 s a part");
        _clamd.Connections.Should().BeLessThan(5, "no part is sent after the deadline");
        (await RecordsAsync("XUCG")).Should().Be(0, "nothing of the bundle is kept");
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE action = 'SiteConfiguration.ImportRefused' AND target_name = 'XUCG'"))
            .Should().Be(1, "the import records its refusal");
    }

    #endregion

    #region Advertising

    private static byte[] Png()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(16, 8, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bitmap.Erase(SKColors.Teal);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private AdvertisingMediaUpload Advertisement(byte[] bytes)
    {
        var today = DateOnly.FromDateTime(_host.Clock.GetUtcNow().UtcDateTime);
        return new AdvertisingMediaUpload(new MemoryStream(bytes), bytes.Length, "upload.png", "Gulf Air", "Winter sun", today, today.AddDays(30));
    }

    private Task<long> AdvertisementsAsync(string site) => _host.ReadAsync<long>("SELECT count(*) FROM advertising_media WHERE site_code = @secret", secret: site);

    [Fact]
    public async Task AdvertisingUpload_Should_RefuseStoreNothingAndAudit_When_TheFileIsTheEicarTestFile()
    {
        var admin = await AdministratorAsync("ad");
        var site = await SiteAsync(admin, "XUAA");
        var media = (IServiceProvider s) => s.GetRequiredService<ISvcAdvertisingMedia>();
        var eicar = Eicar.Bytes();
        var filesBefore = FilesIn(_files);

        var refused = await _host.AsCallerAsync(admin, s => media(s).UploadAsync(site.Code, Advertisement(eicar), Ct));

        refused.ErrorMessages.Should().Equal(UploadScanErrors.Malware);
        (await AdvertisementsAsync(site.Code)).Should().Be(0);
        FilesIn(_files).Should().Be(filesBefore);
        var audited = await RefusalsAsync(admin, "AdvertisingMedia");
        audited.Count.Should().Be(1);
        HoldsTheFilesFingerprintOnly(audited.Latest, "AdvertisingMedia", eicar);

        Ok(await _host.AsCallerAsync(admin, s => media(s).UploadAsync(site.Code, Advertisement(Png()), Ct)));
        (await AdvertisementsAsync(site.Code)).Should().Be(1, "a clean image is still taken");
    }

    [Fact]
    public async Task AdvertisingUpload_Should_RefuseAsUnavailable_When_TheScannerIsDownOutsideVmLocal()
    {
        var admin = await AdministratorAsync("add");
        var site = await SiteAsync(admin, "XUAB");

        var refused = await _down.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAdvertisingMedia>().UploadAsync(site.Code, Advertisement(Png()), Ct));

        refused.ErrorMessages.Should().Equal(UploadScanErrors.ScannerUnavailable);
        (await AdvertisementsAsync(site.Code)).Should().Be(0);
        FilesIn(_downFiles).Should().Be(0);
    }

    #endregion
}
