using System.Diagnostics;
using System.Globalization;
using System.Text;
using Ariva.Core;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Security;
using Ariva.Core.Services;
using Ariva.Core.Services.Administration;
using Ariva.Core.Services.Imports;
using Ariva.Core.Services.Sensing;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Security;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.IntegrationTests.CsvImports;

/// <summary>
/// ARV-123a against PostgreSQL with the real scripts: a device CSV previewed (a dry run of the registration, nothing kept)
/// and committed with its token registers every device in Commissioning with a working new credential; a row the
/// registration refuses at commit time keeps nothing of the rows before it; a commit rolled back after its devices were
/// registered keeps nothing and is audited as failed once; the token is refused for another user, site, file or kind and
/// after 15 minutes; audit entries hold the file's hash and counts, never a cell or a credential. Measures a commit at the
/// row bound in one transaction.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DeviceImportTests(PostgresFixture fixture) : IAsyncDisposable
{
    private const string Header = "code,family,model,transport,dialect,clockSource,terminal,level,x,y,mountingHeightMetres,orientationDegrees,queueZone";
    private const string HostileModel = "<img src=x onerror=alert(1)>";

    private readonly AccountsHost _host = new(fixture, database: TestDatabase.Administration);

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ISvcDeviceImports Imports(IServiceProvider s) => s.GetRequiredService<ISvcDeviceImports>();

    private static T Ok<T>(Fluentx.Result<T> result)
    {
        result.HasErrors.Should().BeFalse(string.Join(", ", result.ErrorMessages ?? []));
        return result.Data;
    }

    private static string Rect(double x, double y, double w, double h) =>
        string.Create(CultureInfo.InvariantCulture, $"{x} {y},{x + w} {y},{x + w} {y + h},{x} {y + h}");

    private static Func<Stream> File(string text) => () => new MemoryStream(Encoding.UTF8.GetBytes(text));

    private static string Row(string code, string model = "PC2SE", double x = 20, string zone = "Snake A") =>
        string.Create(CultureInfo.InvariantCulture, $"{code},StereoVision,\"{model.Replace("\"", "\"\"", StringComparison.Ordinal)}\",HttpsPush,Xovis,Ntp,T1,L0,{x},16,5,0,{zone}");

    private static string Csv(params string[] rows) => Header + "\r\n" + string.Join("\r\n", rows) + "\r\n";

    private async Task<Guid> AdministratorAsync(string name)
    {
        var admin = await _host.CreateUserAsync($"it.dimp.{name}", roles: [RoleCodes.SystemAdministrator]);
        await _host.ReadAsync<int>("UPDATE \"user\" SET all_sites = true WHERE id = @id RETURNING 1", admin);
        return admin;
    }

    /// <summary>A site with one level (100 x 50 m) and a published profile: queue Snake A (10..34 x 10..22) with its lines.</summary>
    private async Task SiteAsync(Guid admin, string site)
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
    }

    private Task<long> DevicesAsync(string site) => _host.ReadAsync<long>("SELECT count(*) FROM device WHERE site_code = @secret", secret: site);

    [Fact]
    public async Task PreviewThenCommit_Should_RegisterEveryDeviceInCommissioningWithANewCredential_When_TheTokenIsThePreviews()
    {
        var admin = await AdministratorAsync("ok");
        await SiteAsync(admin, "XCIA");
        var csv = Csv(Row("S-01", HostileModel), Row("S-02", x: 22), Row("S-03", x: 24));

        var preview = Ok(await _host.AsCallerAsync(admin, s => Imports(s).PreviewAsync("XCIA", File(csv), Ct)));

        preview.Errors.Should().BeEmpty();
        preview.Rows.Should().Be(3);
        preview.Devices.Select(d => (d.Row, d.Code)).Should().Equal((2, "S-01"), (3, "S-02"), (4, "S-03"));
        preview.Devices[0].Model.Should().Be(HostileModel, "a value is data");
        preview.FileSha256.Should().Be(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(csv))));
        preview.PreviewToken.Should().NotBeNullOrEmpty();
        preview.ExpiresUtc.Should().Be(_host.Clock.GetUtcNow().UtcDateTime + CsvImportLimits.PreviewLifetime);
        (await DevicesAsync("XCIA")).Should().Be(0, "a preview is a dry run");

        var committed = Ok(await _host.AsCallerAsync(admin, s => Imports(s).CommitAsync("XCIA", File(csv), preview.PreviewToken, Ct)));

        committed.Devices.Should().Be(3);
        committed.Credentials.Count.Should().Be(3, "one new credential per device");
        committed.Credentials.Select(c => c.Code).Should().Equal("S-01", "S-02", "S-03");
        foreach (var issued in committed.Credentials)
        {
            DeviceCredentials.IsWellFormed(issued.Credential).Should().BeTrue();
            var hash = await _host.ReadAsync<string>("SELECT credential_hash FROM device WHERE site_code = 'XCIA' AND code = @secret", secret: issued.Code);
            DeviceCredentials.Matches(issued.Credential, hash).Should().BeTrue("the credential works");
        }

        (await _host.ReadAsync<long>("SELECT count(*) FROM device WHERE site_code = 'XCIA' AND state = 'Commissioning'")).Should().Be(3);
        (await _host.ReadAsync<string>("SELECT model FROM device WHERE site_code = 'XCIA' AND code = 'S-01'")).Should().Be(HostileModel, "stored as data");
        var previewed = await _host.ReadAsync<string>("SELECT after_summary FROM audit_entry WHERE action = 'DeviceImport.Previewed' AND target_name = 'XCIA'");
        var committedAudit = await _host.ReadAsync<string>("SELECT after_summary FROM audit_entry WHERE action = 'DeviceImport.Committed' AND target_name = 'XCIA'");
        foreach (var (summary, outcome) in new[] { (previewed, "\"previewed\""), (committedAudit, "\"committed\"") })
        {
            new[] { preview.FileSha256, outcome, "\"rows\":3" }.Where(w => !summary.Contains(w, StringComparison.Ordinal)).Should().BeEmpty("hash, outcome and counts");
            new[] { "S-01", "onerror", "Snake" }.Where(w => summary.Contains(w, StringComparison.Ordinal)).Should().BeEmpty("no cell");
            System.Text.RegularExpressions.Regex.IsMatch(summary, "ard[kp]_").Should().BeFalse("no credential");
        }

        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE action = 'Device.Registered' AND target_name LIKE 'XCIA/%'"))
            .Should().Be(3, "the registration path's own entries, of the commit only");
    }

    [Fact]
    public async Task Preview_Should_AnswerEachRefusedRowWithItsColumnAndNoToken_When_RowsBreakTheRules()
    {
        var admin = await AdministratorAsync("rf");
        await SiteAsync(admin, "XCIB");
        var levelB = await LevelAsync("XCIB");
        Ok(await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcDevices>().RegisterAsync(new RegisterDeviceRequest("S-09", "StereoVision", "PC2SE", "HttpsPush",
            "Xovis", "Ntp", new DevicePlacement(levelB, 20, 16, 5, 0, "Snake A")), Ct)));
        var csv = Csv(Row("S-01"), Row("S-02", "=HYPERLINK(\"http://x\")"), Row("S-03", x: 90), Row("S-04", zone: "Snake Z"), Row("S-09"),
            Row("S-06").Replace(",L0,", ",L9,", StringComparison.Ordinal), Row("S-07").Replace(",5,0,", ",50,0,", StringComparison.Ordinal));

        var preview = Ok(await _host.AsCallerAsync(admin, s => Imports(s).PreviewAsync("XCIB", File(csv), Ct)));

        preview.PreviewToken.Should().BeNull("a file with a refused row cannot be committed");
        preview.Devices.Select(d => d.Code).Should().Equal("S-01");
        preview.Errors.Select(e => (e.Row, e.Column, e.Reason)).Should().BeEquivalentTo(new[]
        {
            (3, (int?)3, CsvImportErrors.FormulaPrefix),
            (4, 9, DeviceErrors.OutOfReach),
            (5, 13, DeviceErrors.ZoneNotFound),
            (6, 1, TopologyErrors.Duplicate),
            (7, 8, CsvImportErrors.LevelNotFound),
            (8, (int?)null, CsvImportErrors.RegistrationRefused)
        });
        preview.ErrorCount.Should().Be(6);
        // The registration's own fixed texts keep their column; an entity's message (it may name a value) becomes one fixed text.
        (await DevicesAsync("XCIB")).Should().Be(1, "only the device that was there");
    }

    private Task<Guid> LevelAsync(string site) => _host.ReadAsync<Guid>("SELECT id FROM level WHERE site_code = @secret", secret: site);

    [Fact]
    public async Task Commit_Should_KeepNothing_When_TheRegistrationRefusesALaterRowAfterEarlierOnesWereRegistered()
    {
        var admin = await AdministratorAsync("rb");
        await SiteAsync(admin, "XCIC");
        var csv = Csv(Row("S-01"), Row("S-02", x: 22), Row("S-03", x: 24));
        var token = Ok(await _host.AsCallerAsync(admin, s => Imports(s).PreviewAsync("XCIC", File(csv), Ct))).PreviewToken;
        // Between preview and commit, the third row's code is registered by hand: the commit checks everything again.
        var levelC = await LevelAsync("XCIC");
        Ok(await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcDevices>().RegisterAsync(new RegisterDeviceRequest("S-03", "StereoVision", "PC2SE", "HttpsPush",
            "Xovis", "Ntp", new DevicePlacement(levelC, 20, 16, 5, 0, "Snake A")), Ct)));

        var refused = await _host.AsCallerAsync(admin, s => Imports(s).CommitAsync("XCIC", File(csv), token, Ct));

        refused.ErrorMessages.Should().Equal(CsvImportErrors.AtRow(4, TopologyErrors.Duplicate));
        (await _host.ReadAsync<string>("SELECT string_agg(code, ',' ORDER BY code) FROM device WHERE site_code = 'XCIC'")).Should().Be("S-03", "rows 2 and 3 were rolled back");
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE action = 'DeviceImport.Refused' AND target_name = 'XCIC'")).Should().Be(1);
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE action = 'DeviceImport.Committed' AND target_name = 'XCIC'")).Should().Be(0);
    }

    [Fact]
    public async Task Commit_Should_BeAuditedAsFailedOnceAndKeepNothing_When_ItIsRolledBackAfterTheDevicesWereRegistered()
    {
        // As when the commit itself fails: the unit of work rolls back after a successful import and runs its post-rollback actions.
        var admin = await AdministratorAsync("cm");
        await SiteAsync(admin, "XCID");
        var csv = Csv(Row("S-01"), Row("S-02", x: 22));
        var token = Ok(await _host.AsCallerAsync(admin, s => Imports(s).PreviewAsync("XCID", File(csv), Ct))).PreviewToken;

        DeviceImportCommitViewModel committed;
        await using (var scope = _host.Provider.CreateAsyncScope())
        {
            var caller = (TestCurrentUser)scope.ServiceProvider.GetRequiredService<ICurrentUser>();
            caller.Id = admin;
            caller.UserName = "it-admin";
            committed = Ok(await Imports(scope.ServiceProvider).CommitAsync("XCID", File(csv), token, Ct));
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().RollbackAsync(Ct);
        }

        committed.Credentials.Count.Should().Be(2, "the devices were registered before the rollback");
        (await DevicesAsync("XCID")).Should().Be(0);
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE action = 'DeviceImport.Failed' AND target_name = 'XCID'")).Should().Be(1, "one failure, one entry");
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE action IN ('DeviceImport.Committed', 'Device.Registered') AND target_name LIKE 'XCID%'"))
            .Should().Be(0, "they went with the rollback");
    }

    [Fact]
    public async Task Commit_Should_RefuseTheToken_When_TheUserSiteFileOrKindIsNotThePreviewsOrItIsTooOld()
    {
        var admin = await AdministratorAsync("tk");
        var other = await AdministratorAsync("tk2");
        await SiteAsync(admin, "XCIE");
        await SiteAsync(admin, "XCIF");
        var csv = Csv(Row("S-01"));
        var token = Ok(await _host.AsCallerAsync(admin, s => Imports(s).PreviewAsync("XCIE", File(csv), Ct))).PreviewToken;
        var mappingToken = Ok(await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcDeskCodeMappingImports>()
            .PreviewAsync("XCIE", File("externalSystem,externalCode,checkpoint,desk\r\nAman,X1,NONE,D01\r\n"), Ct))).PreviewToken;

        foreach (var (what, caller, site, file, previewToken) in new[]
                 {
                     ("another user", other, "XCIE", csv, token),
                     ("another site", admin, "XCIF", csv, token),
                     ("another file", admin, "XCIE", csv.Replace("PC2SE", "PC2SF", StringComparison.Ordinal), token),
                     ("a token altered", admin, "XCIE", csv, token[..^1] + (token[^1] == 'A' ? "B" : "A"))
                 })
        {
            (await _host.AsCallerAsync(caller, s => Imports(s).CommitAsync(site, File(file), previewToken, Ct))).ErrorMessages
                .Should().Equal([CsvImportErrors.NotThePreview], what);
        }

        mappingToken.Should().BeNull("a mapping file whose desk does not exist gets no token");
        _host.Clock.Advance(CsvImportLimits.PreviewLifetime + TimeSpan.FromSeconds(1));
        (await _host.AsCallerAsync(admin, s => Imports(s).CommitAsync("XCIE", File(csv), token, Ct))).ErrorMessages
            .Should().Equal([CsvImportErrors.NotThePreview], "older than 15 minutes");
        (await DevicesAsync("XCIE") + await DevicesAsync("XCIF")).Should().Be(0);
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE action = 'DeviceImport.Refused' AND target_name IN ('XCIE', 'XCIF')")).Should().Be(5);
    }

    [Fact]
    public async Task PreviewAndCommit_Should_AnswerNotFound_When_TheSiteIsOutOfTheCallersReach()
    {
        var admin = await AdministratorAsync("sc");
        await SiteAsync(admin, "XCIG");
        var scoped = await _host.CreateUserAsync("it.dimp.scoped", roles: [RoleCodes.SystemAdministrator]);

        (await _host.AsCallerAsync(scoped, s => Imports(s).PreviewAsync("XCIG", File(Csv(Row("S-01"))), Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(scoped, s => Imports(s).CommitAsync("XCIG", File(Csv(Row("S-01"))), "token", Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(admin, s => Imports(s).PreviewAsync("NOPE", File(Csv(Row("S-01"))), Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
    }

    [Fact]
    public async Task Commit_Should_RegisterTheRowBoundInOneTransactionWellWithinARequestsTime_When_Measured()
    {
        var admin = await AdministratorAsync("bd");
        await SiteAsync(admin, "XCIH");
        var csv = Csv([.. Enumerable.Range(1, CsvImportLimits.MaxDeviceRows).Select(n => Row(string.Create(CultureInfo.InvariantCulture, $"S-{n}")))]);

        var clock = Stopwatch.StartNew();
        var token = Ok(await _host.AsCallerAsync(admin, s => Imports(s).PreviewAsync("XCIH", File(csv), Ct))).PreviewToken;
        var previewTime = clock.Elapsed;
        clock.Restart();
        var committed = Ok(await _host.AsCallerAsync(admin, s => Imports(s).CommitAsync("XCIH", File(csv), token, Ct)));
        var commitTime = clock.Elapsed;

        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{CsvImportLimits.MaxDeviceRows} devices: preview {previewTime.TotalSeconds:0.0} s, commit {commitTime.TotalSeconds:0.0} s"));
        committed.Credentials.Count.Should().Be(CsvImportLimits.MaxDeviceRows);
        (await DevicesAsync("XCIH")).Should().Be(CsvImportLimits.MaxDeviceRows);
        commitTime.Should().BeLessThan(TimeSpan.FromSeconds(60), "well within a request's time");
    }
}
